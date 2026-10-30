// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Apache.Arrow;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Fabricator.SqlServer;

// The correlated form of a discovered TVF: `SELECT … FROM t, db.dbo.tvf(t.a)`. DuckDB hands the lateral session
// one chunk of distinct argument tuples at a time; each chunk becomes ONE statement,
//   SELECT p.[__fab_row], f.* FROM (VALUES (0, CAST(@p0_0 AS <param type>), …), …) AS p([__fab_row], [a0], …)
//   CROSS APPLY [s].[tvf](p.[a0], …) AS f
// and the row number in VALUES is the provenance the host needs to stamp the correlated columns on each output row.
// It replaces the `<tvf>_each(<table>)` in-out form, which did the same CROSS APPLY behind a table argument.
// A literal call (`db.dbo.tvf(1)`) never comes here: it stays the ordinary scan with projection and filter pushdown.
internal sealed class SqlServerTvfLateral : ILateralFunctionBinding
{
    // the same category as every other statement this provider sends
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql");

    private readonly SqlServerCatalog _owner;
    private readonly string _qualified;      // [schema].[func]
    private readonly string[] _paramSqlTypes; // the TVF's parameter types, for the VALUES CAST
    private readonly string _isolation;

    public SqlServerTvfLateral(SqlServerCatalog owner, string schemaName, string functionName, Schema inputSchema,
                               string isolation)
    {
        _owner = owner;
        _qualified = SqlServerCatalog.Quote(schemaName) + "." + SqlServerCatalog.Quote(functionName);
        var pars = owner.FunctionParameters(schemaName, functionName, wantReturn: false);
        if (inputSchema.FieldsList.Count != pars.Count)
        {
            throw new ArgumentException(
                $"fabricator: {schemaName}.{functionName} takes {pars.Count} arguments, the call passes {inputSchema.FieldsList.Count}");
        }
        _paramSqlTypes = pars.Select(p => p.sqlType).ToArray();
        _isolation = isolation;
        OutputSchema = owner.GetFunctionOutputSchema(schemaName, functionName);
    }

    public Schema OutputSchema { get; }

    public ILateralSession Open() => new Session(this);

    public void Dispose()
    {
    }

    private sealed class Session : ILateralSession
    {
        private readonly SqlServerTvfLateral _b;
        // One connection + read transaction per session (per pipeline thread), opened on the first chunk: all of
        // its chunks read one consistent view at the configured isolation, as the `_each` form did.
        private SqlConnection? _conn;
        private SqlTransaction? _txn;

        public Session(SqlServerTvfLateral binding) => _b = binding;

        public LateralResult Call(RecordBatch input)
        {
            int rows = input.Length;
            int cols = _b._paramSqlTypes.Length;
            if (rows == 0)
            {
                return LateralResult.Empty;
            }
            if (_conn == null)
            {
                (_conn, _txn) = _b._owner.BeginInOutScope(_b._isolation);
            }
            var batches = new List<RecordBatch>();
            var origin = new List<int>();
            // SQL Server takes at most ~2100 parameters per statement
            int maxRows = Math.Max(1, 2000 / Math.Max(1, cols));
            for (int start = 0; start < rows; start += maxRows)
            {
                int end = Math.Min(start + maxRows, rows);
                RunChunk(input, start, end, batches, origin);
            }
            if (origin.Count == 0)
            {
                foreach (var b in batches)
                {
                    b.Dispose();
                }
                return LateralResult.Empty;
            }
            return new LateralResult(Concatenate(batches), origin.ToArray());
        }

        private void RunChunk(RecordBatch input, int start, int end, List<RecordBatch> batches, List<int> origin)
        {
            int cols = _b._paramSqlTypes.Length;
            var sb = new StringBuilder("SELECT p.[__fab_row], f.* FROM (VALUES ");
            var sqlParams = new List<SqlParameter>();
            for (int r = start; r < end; r++)
            {
                sb.Append(r > start ? ", (" : "(").Append(r);
                for (int c = 0; c < cols; c++)
                {
                    var pn = $"@p{r}_{c}";
                    sb.Append(", CAST(").Append(pn).Append(" AS ").Append(_b._paramSqlTypes[c]).Append(')');
                    sqlParams.Add(new SqlParameter(pn, ArrowValueReader.ReadScalar(input.Column(c), r) ?? (object)DBNull.Value));
                }
                sb.Append(')');
            }
            sb.Append(") AS p([__fab_row]");
            for (int c = 0; c < cols; c++)
            {
                sb.Append(", [a").Append(c).Append(']');
            }
            sb.Append(") CROSS APPLY ").Append(_b._qualified).Append('(');
            for (int c = 0; c < cols; c++)
            {
                sb.Append(c > 0 ? ", " : "").Append("p.[a").Append(c).Append(']');
            }
            sb.Append(") AS f");

            using var command = _conn!.CreateCommand();
            command.CommandText = sb.ToString();
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("tvf lateral {Function} rows {Start}..{End} of {Rows}: {Sql}", _b._qualified, start, end - 1,
                             input.Length, command.CommandText.Length <= 300 ? command.CommandText : command.CommandText.Substring(0, 300) + "…");
            }
            command.CommandType = CommandType.Text;
            command.Transaction = _txn;
            SqlServerCatalog.AddParameters(command, sqlParams);
            var reader = command.ExecuteReader();
            using var stream = new DbDataReaderArrowStream(_conn, command, reader, ownsConnection: false);
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                var rowNumbers = (Int32Array)batch.Column(0);
                for (int i = 0; i < batch.Length; i++)
                {
                    origin.Add(rowNumbers.GetValue(i)!.Value);
                }
                // the TVF's own columns move into the output batch, which now owns them; only the row numbers are
                // released here (disposing `batch` would release the moved columns too)
                var columns = new IArrowArray[_b.OutputSchema.FieldsList.Count];
                for (int c = 0; c < columns.Length; c++)
                {
                    columns[c] = batch.Column(c + 1);
                }
                rowNumbers.Dispose();
                batches.Add(new RecordBatch(_b.OutputSchema, columns, batch.Length));
            }
        }

        private RecordBatch Concatenate(List<RecordBatch> batches)
        {
            if (batches.Count == 1)
            {
                return batches[0];
            }
            var columns = new IArrowArray[_b.OutputSchema.FieldsList.Count];
            for (int c = 0; c < columns.Length; c++)
            {
                columns[c] = ArrowArrayConcatenator.Concatenate(batches.Select(b => b.Column(c)).ToList());
            }
            int length = batches.Sum(b => b.Length);
            foreach (var b in batches)
            {
                b.Dispose();
            }
            return new RecordBatch(_b.OutputSchema, columns, length);
        }

        public void Dispose()
        {
            try
            {
                _txn?.Commit(); // read-only: releases the view
            }
            catch
            {
                // best-effort: a broken connection has nothing left to release
            }
            _txn?.Dispose();
            _conn?.Dispose();
        }
    }
}
