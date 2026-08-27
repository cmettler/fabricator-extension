// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

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
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

// Phase 6.2 streaming-exchange binding for a discovered TVF `_each`: applies the TVF once per input row via
// SQL-Server CROSS APPLY, streamed through the gate-based exchange operator (no per-chunk materialization —
// the streaming successor to the retired InOutSessionImpl). Output = the echoed input columns (typed as the
// TVF's PARAMETERS — C# CASTs the VALUES to them) ++ the TVF's output columns. One pinned connection +
// transaction (the configured isolation) wraps all chunks for a consistent snapshot, opened lazily on the
// first chunk and committed when the input stream ends. A top-level internal class (like
// SqlServerScalarFunction) holding a SqlServerCatalog; reaches its internal helpers (FunctionParameters /
// BeginInOutScope / GetFunction*Schema / Quote / AddParameters).
internal sealed class SqlServerTvfEach : IInOutBinding, IInOutIsolation
{
    private readonly SqlServerCatalog _owner;
    private readonly string _qualified;     // [schema].[func]
    private readonly string[] _colNames;     // input column names (VALUES aliases + CROSS APPLY args)
    private readonly string[] _colSqlTypes;  // SQL types for the VALUES CAST (the TVF's positional params)
    private string _isolation = "";

    public SqlServerTvfEach(SqlServerCatalog owner, string schemaName, string functionName, Schema inputSchema)
    {
        _owner = owner;
        _qualified = SqlServerCatalog.Quote(schemaName) + "." + SqlServerCatalog.Quote(functionName);
        _colNames = inputSchema.FieldsList.Select(f => f.Name).ToArray();
        // Input columns map positionally to the TVF's parameters; CAST the VALUES columns to those SQL
        // types so CROSS APPLY binds (and an all-NULL chunk still type-checks).
        var pars = owner.FunctionParameters(schemaName, functionName, wantReturn: false);
        _colSqlTypes = new string[_colNames.Length];
        for (int i = 0; i < _colNames.Length; i++)
        {
            _colSqlTypes[i] = i < pars.Count ? pars[i].sqlType : "sql_variant";
        }
        // Output = the echoed input columns (p.*) ++ the TVF's output columns (f.*). The p.* columns come
        // back typed as the TVF PARAMETERS (the VALUES are CAST to them), named by the input columns.
        var pFields = new List<Field>(_colNames.Length);
        var paramFields = owner.GetFunctionParamSchema(schemaName, functionName).FieldsList;
        for (int i = 0; i < _colNames.Length; i++)
        {
            var dataType = i < paramFields.Count ? paramFields[i].DataType : inputSchema.FieldsList[i].DataType;
            pFields.Add(new Field(_colNames[i], dataType, nullable: true));
        }
        OutputSchema = new Schema(pFields.Concat(owner.GetFunctionOutputSchema(schemaName, functionName).FieldsList),
                                  metadata: null);
    }

    public Schema OutputSchema { get; }

    public string IsolationLevel { set => _isolation = value ?? ""; }

    // Stream the CROSS APPLY per input chunk on one pinned connection + transaction (consistent snapshot),
    // opened lazily here and committed when the input ends. The per-input sentinel is yielded per chunk.
    public async IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (conn, txn) = _owner.BeginInOutScope(_isolation);
        try
        {
            await foreach (var chunk in input.WithCancellation(ct))
            {
                using (chunk)
                {
                    await foreach (var outBatch in RunCrossApply(conn, txn, chunk))
                    {
                        yield return outBatch;
                    }
                }
                yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel (NEED_MORE_INPUT)
            }
            txn.Commit(); // read-only: releases the snapshot view
        }
        finally
        {
            txn.Dispose();
            conn.Dispose();
        }
    }

    public void Dispose()
    {
    }

    // One input batch -> `SELECT p.*, f.* FROM (VALUES …) p(cols) CROSS APPLY [s].[func](p.cols) f`,
    // sub-chunked under the ~2100-parameter cap; yields the per-query result batches.
    private async IAsyncEnumerable<RecordBatch> RunCrossApply(SqlConnection conn, SqlTransaction txn, RecordBatch batch)
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
                sb.Append(c > 0 ? ", " : "").Append(SqlServerCatalog.Quote(_colNames[c]));
            }
            sb.Append(") CROSS APPLY ").Append(_qualified).Append('(');
            for (int c = 0; c < cols; c++)
            {
                sb.Append(c > 0 ? ", " : "").Append("p.").Append(SqlServerCatalog.Quote(_colNames[c]));
            }
            sb.Append(") AS f");

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
}
