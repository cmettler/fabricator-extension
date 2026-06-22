using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using ArrowNet.Bridge;
using Microsoft.Data.SqlClient;

namespace ArrowNet.SqlServer;

// Stored-procedure table-in-out PUSH session for SqlServerCatalog (4g): a proc is EXEC'd once per input row on
// DuckDB's pinned write transaction (BeginWrite), so its writes commit/roll back with DuckDB's COMMIT/ROLLBACK.
// A nested partial of SqlServerCatalog for access to the catalog internals (BeginWrite / FunctionParameters /
// GetFunction*Schema / Quote / AddParameters) without widening them. Custom + discovered-TVF in-out instead
// STREAM on the Phase 6 exchange — see SqlServerTvfEach.cs / InOutExchange.cs (the materializing push sessions
// for those were retired in 6.2).
public sealed partial class SqlServerCatalog
{

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
