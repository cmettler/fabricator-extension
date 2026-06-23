using System;
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

// Streaming-exchange binding for a discovered stored-procedure `_each` (the in-out analog of SqlServerTvfEach):
// EXECs the proc once per input row, streamed through the same gate-based exchange operator. A proc can't be
// inline CROSS-APPLY'd, so it is EXEC'd per row.
//
// The ONE difference from SqlServerTvfEach (a read-only TVF on its own committed snapshot): the EXECs run on
// DuckDB's PINNED write connection + transaction (BeginWrite), so the proc's writes commit/roll back with
// DuckDB's COMMIT/ROLLBACK -- atomic in autocommit (statement-scoped) AND inside an explicit DuckDB BEGIN. So
// DoExchange must NOT commit and must NOT dispose the pinned scope (DuckDB's transaction manager owns it).
//
// Output = the echoed input columns (typed as the proc's parameters, named by the input columns) ++ the proc's
// result-set columns; the echo is produced server-side (INSERT the proc's result set into a table variable,
// then SELECT the input values + the captured rows). Result-set procs only -- an OUTPUT-param-only proc errors.
// A top-level internal class (like SqlServerTvfEach), holding a SqlServerCatalog and reaching its internals.
internal sealed class SqlServerProcEach : IArrowInOutBinding
{
    private readonly SqlServerCatalog _owner;
    private readonly string _qualified;        // [schema].[proc]
    private readonly string[] _colNames;        // input column names (echo aliases + positional EXEC args)
    private readonly string[] _colSqlTypes;     // proc parameter SQL types (CAST the echo)
    private readonly string[] _procParamNames;  // proc parameter names (de-@'d), positional
    private readonly List<(string name, string sqlType)> _resultCols; // proc result schema (DECLARE @t)

    public SqlServerProcEach(SqlServerCatalog owner, string schemaName, string functionName, Schema inputSchema)
    {
        _owner = owner;
        _qualified = SqlServerCatalog.Quote(schemaName) + "." + SqlServerCatalog.Quote(functionName);
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
        // Output = echoed input columns (param-typed, named by the input columns) ++ the proc's result columns
        // — matching the C++ exchange bind (input echo ++ the function's own output columns).
        var pFields = new List<Field>(_colNames.Length);
        using (var ps = owner.GetFunctionParamSchema(schemaName, functionName))
        {
            var paramFields = ps.Schema.FieldsList;
            for (int i = 0; i < _colNames.Length; i++)
            {
                var dataType = i < paramFields.Count ? paramFields[i].DataType : inputSchema.FieldsList[i].DataType;
                pFields.Add(new Field(_colNames[i], dataType, nullable: true));
            }
        }
        using (var os = owner.GetFunctionOutputSchema(schemaName, functionName))
        {
            OutputSchema = new Schema(pFields.Concat(os.Schema.FieldsList), metadata: null);
        }
    }

    public Schema OutputSchema { get; }

    // Stream the per-row proc EXEC on DuckDB's pinned write connection/transaction (BeginWrite), opened lazily
    // here per execution. NO commit + no dispose of the pinned scope: DuckDB's transaction manager drives
    // commit/rollback (the proc's writes are part of DuckDB's transaction). The per-input sentinel is yielded
    // per chunk; a thrown EXEC error propagates out -> fails the statement -> DuckDB rolls it back.
    public async IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (conn, txn, ownsScope) = _owner.BeginWrite();
        try
        {
            await foreach (var chunk in input.WithCancellation(ct))
            {
                using (chunk)
                {
                    for (int r = 0; r < chunk.Length; r++)
                    {
                        await foreach (var outBatch in RunProcRow(conn, txn, chunk, r))
                        {
                            yield return outBatch;
                        }
                    }
                }
                yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel (NEED_MORE_INPUT)
            }
            // No commit — DuckDB drives the pinned transaction's commit/rollback.
        }
        finally
        {
            // Never commit/dispose DuckDB's pinned scope; only a standalone one we somehow opened.
            if (ownsScope)
            {
                txn?.Dispose();
                conn.Dispose();
            }
        }
    }

    public void Dispose()
    {
    }

    // One input row -> `DECLARE @t TABLE(<proc result>); INSERT @t EXEC [s].[p] @param=@p,...; SELECT
    // <echoed input>, t.* FROM @t;` on the pinned connection; yields the result batches.
    private async IAsyncEnumerable<RecordBatch> RunProcRow(
        SqlConnection conn, SqlTransaction? txn, RecordBatch chunk, int row)
    {
        int cols = _colNames.Length;
        var sb = new StringBuilder("DECLARE @t TABLE (");
        for (int c = 0; c < _resultCols.Count; c++)
        {
            sb.Append(c > 0 ? ", " : "").Append(SqlServerCatalog.Quote(_resultCols[c].name)).Append(' ')
              .Append(_resultCols[c].sqlType);
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
              .Append(SqlServerCatalog.Quote(_colNames[c])).Append(", ");
        }
        sb.Append("t.* FROM @t AS t;");

        var command = conn.CreateCommand();
        command.CommandText = sb.ToString();
        command.CommandType = CommandType.Text;
        command.Transaction = txn;
        SqlServerCatalog.AddParameters(command, sqlParams);
        var reader = await command.ExecuteReaderAsync();
        using var res = new DbDataReaderArrowStream(conn, command, reader, ownsConnection: false);
        while (true)
        {
            var b = await res.ReadNextRecordBatchAsync();
            if (b is null)
            {
                break;
            }
            yield return b;
        }
    }
}
