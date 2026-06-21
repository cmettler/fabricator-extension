using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using ArrowNet.Bridge;
using Microsoft.Data.SqlClient;

namespace ArrowNet.SqlServer;

// Table-in-out execution sessions for SqlServerCatalog (4g), split out of SqlServerBackend.cs to keep that
// file focused. These are nested in the SAME partial class, so they keep access to the catalog internals
// (BeginInOutScope / BeginWrite / FunctionParameters / GetFunction*Schema / Quote) without widening them.
public sealed partial class SqlServerCatalog
{
    // Session for a custom C#-authored table-in-out (IArrowTableInOutFunction). The C++ operator pushes
    // input chunks; each Process runs synchronously and its output is emitted immediately (per-chunk
    // streaming — no emit-at-end). Push runs serially (under a lock), so the function may keep mutable
    // state across calls.
    private sealed class CustomInOutSessionImpl : IInOutSession
    {
        private readonly IArrowTableInOutFunction _fn;
        private readonly Schema _inputSchema;
        private readonly ConcurrentQueue<RecordBatch> _ready = new();
        private readonly object _lock = new();
        private bool _aborted;

        public CustomInOutSessionImpl(IArrowTableInOutFunction fn, Schema inputSchema)
        {
            _fn = fn;
            _inputSchema = inputSchema;
        }

        public Schema InputSchema => _inputSchema;

        public void Push(RecordBatch chunk)
        {
            using (chunk)
            {
                lock (_lock)
                {
                    if (_aborted)
                    {
                        return;
                    }
                    foreach (var b in _fn.Process(chunk)) // enumerated (materialized) before the chunk is disposed
                    {
                        _ready.Enqueue(b);
                    }
                }
            }
        }

        public IArrowArrayStream DrainReady()
        {
            var list = new List<RecordBatch>();
            while (_ready.TryDequeue(out var b))
            {
                list.Add(b);
            }
            return new InMemoryArrayStream(_fn.OutputSchema, list);
        }

        // No emit-at-end: finishing just drains anything not yet pulled (normally empty).
        public IArrowArrayStream Finish() => DrainReady();

        public void Abort()
        {
            lock (_lock)
            {
                _aborted = true;
                while (_ready.TryDequeue(out var b))
                {
                    b.Dispose();
                }
            }
        }
    }

    private sealed class InOutSessionImpl : IInOutSession
    {
        private readonly SqlServerCatalog _owner;
        private readonly string _qualified;       // [schema].[func]
        private readonly string[] _colNames;       // input column names (VALUES aliases + CROSS APPLY args)
        private readonly string[] _colSqlTypes;    // SQL types for the VALUES CAST (positional TVF params)
        private readonly Schema _inputSchema;
        private readonly Schema _outputSchema;
        // Output is produced SYNCHRONOUSLY per input chunk (each Push runs that chunk's CROSS APPLY to
        // completion), so there is no lagging tail to drain after all input — which is what lets the
        // injected OperatorFinalize be a pure no-rows cleanup signal and removes any reliance on detecting
        // the "last" parallel input branch. Parallel branches feed the one session; the lock serializes them.
        private readonly ConcurrentQueue<RecordBatch> _ready = new();
        private readonly object _lock = new();
        private bool _aborted;
        // One pinned connection + transaction for the whole in-out call, so all per-chunk CROSS APPLY queries
        // share one consistent view (the configured isolation). Committed at finish / rolled back on abort;
        // the connection + transaction are disposed on abort (the final teardown).
        private readonly SqlConnection _conn;
        private readonly SqlTransaction _txn;
        private bool _scopeClosed;

        public Schema InputSchema => _inputSchema;

        public InOutSessionImpl(SqlServerCatalog owner, string schemaName, string functionName, Schema inputSchema,
                                string isolation)
        {
            _owner = owner;
            _qualified = Quote(schemaName) + "." + Quote(functionName);
            _inputSchema = inputSchema;
            _colNames = inputSchema.FieldsList.Select(f => f.Name).ToArray();
            // Input columns map positionally to the TVF's parameters; CAST the VALUES columns to those
            // SQL types so CROSS APPLY binds (and an all-NULL chunk still type-checks).
            var pars = owner.FunctionParameters(schemaName, functionName, wantReturn: false);
            _colSqlTypes = new string[_colNames.Length];
            for (int i = 0; i < _colNames.Length; i++)
            {
                _colSqlTypes[i] = i < pars.Count ? pars[i].sqlType : "sql_variant";
            }
            // Output = the echoed input columns (p.*) + the TVF's output columns (f.*). The p.* columns
            // come back typed as the TVF PARAMETERS (the VALUES are CAST to the param types above), not
            // as the pushed input schema — so build them from the param schema, named by the input
            // columns, to match what SQL Server actually returns (and the C++ bind's declared types).
            var pFields = new List<Field>(_colNames.Length);
            using (var ps = owner.GetFunctionParamSchema(schemaName, functionName))
            {
                var paramFields = ps.Schema.FieldsList;
                for (int i = 0; i < _colNames.Length; i++)
                {
                    var dataType = i < paramFields.Count ? paramFields[i].DataType : _inputSchema.FieldsList[i].DataType;
                    pFields.Add(new Field(_colNames[i], dataType, nullable: true));
                }
            }
            using (var os = owner.GetFunctionOutputSchema(schemaName, functionName))
            {
                _outputSchema = new Schema(pFields.Concat(os.Schema.FieldsList), metadata: null);
            }
            // Open the pinned connection + transaction last: if anything above throws, no connection leaks.
            (_conn, _txn) = owner.BeginInOutScope(isolation);
        }

        // Run this chunk's CROSS APPLY to completion and stash its full output (synchronous: no lagging
        // tail). The lock serializes parallel input branches feeding the one session; a CROSS APPLY error
        // throws here and propagates out through inout_push, failing the query.
        public void Push(RecordBatch chunk)
        {
            using (chunk)
            {
                lock (_lock)
                {
                    if (_aborted)
                    {
                        return;
                    }
                    foreach (var b in RunCrossApply(chunk)) // enumerated before the chunk is disposed
                    {
                        _ready.Enqueue(b);
                    }
                }
            }
        }

        public IArrowArrayStream DrainReady()
        {
            var list = new List<RecordBatch>();
            while (_ready.TryDequeue(out var b))
            {
                list.Add(b);
            }
            return new InMemoryArrayStream(_outputSchema, list);
        }

        // Clean finish: commit the in-out's transaction. For a read-only TVF a commit just releases the
        // (snapshot) view; the connection + transaction are disposed on abort (the final teardown). Then
        // drain any leftover.
        public IArrowArrayStream Finish()
        {
            lock (_lock)
            {
                if (!_scopeClosed)
                {
                    _scopeClosed = true;
                    _txn.Commit();
                }
            }
            return DrainReady();
        }

        public void Abort()
        {
            lock (_lock)
            {
                _aborted = true;
                if (!_scopeClosed)
                {
                    _scopeClosed = true;
                    try
                    {
                        _txn.Rollback();
                    }
                    catch
                    {
                        // best-effort: the transaction may already be doomed (e.g. a row EXEC failed)
                    }
                }
                while (_ready.TryDequeue(out var b))
                {
                    b.Dispose();
                }
                _txn.Dispose();
                _conn.Dispose();
            }
        }

        // One input batch -> `SELECT p.*, f.* FROM (VALUES …) p(cols) CROSS APPLY [s].[func](p.cols) f`,
        // sub-chunked to stay under SQL Server's ~2100-parameter cap; yields the per-query result batches.
        private IEnumerable<RecordBatch> RunCrossApply(RecordBatch batch)
        {
            int rows = batch.Length;
            int cols = _colNames.Length;
            if (rows == 0 || cols == 0)
            {
                yield break;
            }
            int maxRows = Math.Max(1, 2000 / cols);
            for (int start = 0; start < rows; start += maxRows)
            {
                int end = Math.Min(start + maxRows, rows);
                var sb = new StringBuilder("SELECT p.*, f.* FROM (VALUES ");
                var sqlParams = new List<SqlParameter>();
                for (int r = start; r < end; r++)
                {
                    if (r > start)
                    {
                        sb.Append(", ");
                    }
                    sb.Append('(');
                    for (int c = 0; c < cols; c++)
                    {
                        if (c > 0)
                        {
                            sb.Append(", ");
                        }
                        var pn = $"@p{r}_{c}";
                        sb.Append("CAST(").Append(pn).Append(" AS ").Append(_colSqlTypes[c]).Append(')');
                        sqlParams.Add(new SqlParameter(pn,
                            ArrowValueReader.ReadScalar(batch.Column(c), r) ?? (object)DBNull.Value));
                    }
                    sb.Append(')');
                }
                sb.Append(") AS p(");
                for (int c = 0; c < cols; c++)
                {
                    sb.Append(c > 0 ? ", " : "").Append(Quote(_colNames[c]));
                }
                sb.Append(") CROSS APPLY ").Append(_qualified).Append('(');
                for (int c = 0; c < cols; c++)
                {
                    sb.Append(c > 0 ? ", " : "").Append("p.").Append(Quote(_colNames[c]));
                }
                sb.Append(") AS f");

                // Run on the session's pinned connection + transaction (consistent view across all chunks).
                var command = _conn.CreateCommand();
                command.CommandText = sb.ToString();
                command.CommandType = CommandType.Text;
                command.Transaction = _txn;
                AddParameters(command, sqlParams);
                var reader = command.ExecuteReader();
                using var res = new DbDataReaderArrowStream(_conn, command, reader, ownsConnection: false);
                while (true)
                {
                    var b = res.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                    if (b is null)
                    {
                        break;
                    }
                    yield return b;
                }
            }
        }
    }

    // Table-in-out session for a discovered stored procedure (4g, per-row). A proc can't be inline
    // CROSS-APPLY'd, so it is EXEC'd once per input row. The EXECs run on DuckDB's pinned connection +
    // transaction (BeginWrite), so the proc's writes commit/roll back with DuckDB's COMMIT/ROLLBACK —
    // atomic in autocommit (statement-scoped) AND in an explicit DuckDB BEGIN. The input columns map
    // positionally to the proc's parameters; output = the echoed input columns + the proc's result-set
    // columns (the echo is produced server-side: INSERT the proc's result set into a table variable, then
    // SELECT the input values + the captured rows). Result-set procs only — an OUTPUT-param-only proc errors.
    private sealed class ProcInOutSessionImpl : IInOutSession
    {
        private readonly SqlServerCatalog _owner;
        private readonly string _qualified;        // [schema].[proc]
        private readonly string[] _colNames;        // input column names (echo aliases)
        private readonly string[] _colSqlTypes;     // proc parameter SQL types (CAST the echo + EXEC args)
        private readonly string[] _procParamNames;  // proc parameter names (de-@'d), positional
        private readonly List<(string name, string sqlType)> _resultCols; // proc result schema (DECLARE @t)
        private readonly Schema _inputSchema;
        private readonly Schema _outputSchema;
        private readonly ConcurrentQueue<RecordBatch> _ready = new();
        private readonly object _lock = new();
        private bool _aborted;
        // DuckDB-pinned connection + transaction (owns=false): never commit/rollback/dispose here — that's
        // DuckDB's transaction manager's job (CommitTransaction/RollbackTransaction).
        private readonly SqlConnection _conn;
        private readonly SqlTransaction? _txn;
        private readonly bool _ownsScope;

        public ProcInOutSessionImpl(SqlServerCatalog owner, string schemaName, string functionName, Schema inputSchema)
        {
            _owner = owner;
            _qualified = Quote(schemaName) + "." + Quote(functionName);
            _inputSchema = inputSchema;
            _colNames = inputSchema.FieldsList.Select(f => f.Name).ToArray();
            var pars = owner.FunctionParameters(schemaName, functionName, wantReturn: false);
            _colSqlTypes = new string[_colNames.Length];
            _procParamNames = new string[_colNames.Length];
            for (int i = 0; i < _colNames.Length; i++)
            {
                _colSqlTypes[i] = i < pars.Count ? pars[i].sqlType : "sql_variant";
                _procParamNames[i] = i < pars.Count ? pars[i].name : $"p{i}";
            }
            _resultCols = owner.ProcResultColumns(schemaName, functionName);
            if (_resultCols.Count == 0)
            {
                throw new NotSupportedException(
                    $"mssql_net: stored procedure {schemaName}.{functionName} has no describable result set; " +
                    "table-in-out (_each) currently supports procedures that return a result set");
            }
            // Output = echoed input columns (param-typed, named by the input columns) + the proc's result
            // columns — matching the C++ bind (echo arg types ++ get_function_output_schema).
            var pFields = new List<Field>(_colNames.Length);
            using (var ps = owner.GetFunctionParamSchema(schemaName, functionName))
            {
                var paramFields = ps.Schema.FieldsList;
                for (int i = 0; i < _colNames.Length; i++)
                {
                    var dataType = i < paramFields.Count ? paramFields[i].DataType : _inputSchema.FieldsList[i].DataType;
                    pFields.Add(new Field(_colNames[i], dataType, nullable: true));
                }
            }
            using (var os = owner.GetFunctionOutputSchema(schemaName, functionName))
            {
                _outputSchema = new Schema(pFields.Concat(os.Schema.FieldsList), metadata: null);
            }
            // Pin DuckDB's connection + transaction last (a setup failure above then leaks nothing).
            (_conn, _txn, _ownsScope) = owner.BeginWrite();
        }

        public Schema InputSchema => _inputSchema;

        // Per input row: EXEC the proc into a table variable, then SELECT the echoed input + captured rows.
        public void Push(RecordBatch chunk)
        {
            using (chunk)
            {
                lock (_lock)
                {
                    if (_aborted)
                    {
                        return;
                    }
                    for (int r = 0; r < chunk.Length; r++)
                    {
                        foreach (var b in RunProcRow(chunk, r))
                        {
                            _ready.Enqueue(b);
                        }
                    }
                }
            }
        }

        public IArrowArrayStream DrainReady()
        {
            var list = new List<RecordBatch>();
            while (_ready.TryDequeue(out var b))
            {
                list.Add(b);
            }
            return new InMemoryArrayStream(_outputSchema, list);
        }

        // Commit/rollback is DuckDB's (the EXECs ran on its pinned transaction), so finish/abort only
        // clean up this session's own resources.
        public IArrowArrayStream Finish() => DrainReady();

        public void Abort()
        {
            lock (_lock)
            {
                _aborted = true;
                while (_ready.TryDequeue(out var b))
                {
                    b.Dispose();
                }
            }
            // Only dispose if we somehow opened a standalone connection (not DuckDB's pinned one).
            if (_ownsScope)
            {
                _txn?.Dispose();
                _conn.Dispose();
            }
        }

        private IEnumerable<RecordBatch> RunProcRow(RecordBatch chunk, int row)
        {
            int cols = _colNames.Length;
            var sb = new StringBuilder("DECLARE @t TABLE (");
            for (int c = 0; c < _resultCols.Count; c++)
            {
                sb.Append(c > 0 ? ", " : "").Append(Quote(_resultCols[c].name)).Append(' ').Append(_resultCols[c].sqlType);
            }
            sb.Append(");\nINSERT INTO @t EXEC ").Append(_qualified);
            var sqlParams = new List<SqlParameter>();
            for (int c = 0; c < cols; c++)
            {
                var pn = $"@p{c}";
                sb.Append(c > 0 ? ", " : " ").Append('@').Append(_procParamNames[c]).Append('=').Append(pn);
                sqlParams.Add(new SqlParameter(pn, ArrowValueReader.ReadScalar(chunk.Column(c), row) ?? (object)DBNull.Value));
            }
            sb.Append(";\nSELECT ");
            for (int c = 0; c < cols; c++)
            {
                sb.Append("CAST(@p").Append(c).Append(" AS ").Append(_colSqlTypes[c]).Append(") AS ")
                  .Append(Quote(_colNames[c])).Append(", ");
            }
            sb.Append("t.* FROM @t AS t;");

            var command = _conn.CreateCommand();
            command.CommandText = sb.ToString();
            command.CommandType = CommandType.Text;
            command.Transaction = _txn;
            AddParameters(command, sqlParams);
            var reader = command.ExecuteReader();
            using var res = new DbDataReaderArrowStream(_conn, command, reader, ownsConnection: false);
            while (true)
            {
                var b = res.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                if (b is null)
                {
                    break;
                }
                yield return b;
            }
        }
    }
}
