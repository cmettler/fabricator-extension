using System;
using System.Collections.Generic;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using ArrowNet.Bridge;
using Microsoft.Data.SqlClient;

namespace ArrowNet.SqlServer;

// A discovered SQL Server table-valued function (inline or multi-statement TVF), resolved as
// `SELECT <cols> FROM db.schema.tvf(@a0, ...) WHERE <filter>`. Owns the TVF's SQL: the bind-time output
// schema (INFORMATION_SCHEMA.ROUTINE_COLUMNS) and the scan, with real SQL-level projection + best-effort
// filter pushdown (an inline TVF is inlined by SQL Server, so the predicates genuinely reach the base
// tables) via the shared ScanFromSource. The constant call args are POSITIONAL (input columns map to the
// TVF's parameters in order).
//
// Deliberately NOT an ICatalogTableFunction (unlike SqlServerProcedure / the custom C# functions): a pushdown
// source is stream-native — ScanFromSource returns a stream whose schema already reflects the PROJECTED
// columns (so it matches the projected batches). The ICatalogTableFunction shape (IAsyncEnumerable output +
// a bind-time OutputSchema fixed before the scan's projection is known) would force the full schema onto
// projected batches and crash arrow_ingest. Folding the TVF fully into ICatalogTableFunction needs the
// stream-returning session-handle ABI (deferred Phase 5). A top-level internal class (like
// SqlServerProcedure / SqlServerScalarFunction) holding a SqlServerCatalog and reaching its internal helpers.
internal sealed class SqlServerTableValuedFunction
{
    private readonly SqlServerCatalog _owner;
    private readonly string _schema;
    private readonly string _func;
    private readonly string _qualified;

    public SqlServerTableValuedFunction(SqlServerCatalog owner, string schemaName, string functionName)
    {
        _owner = owner;
        _schema = schemaName;
        _func = functionName;
        _qualified = SqlServerCatalog.Quote(schemaName) + "." + SqlServerCatalog.Quote(functionName);
    }

    // The TVF's full output columns (INFORMATION_SCHEMA.ROUTINE_COLUMNS), as a zero-row schema — for the
    // bind-time get_function_output_schema. (Independent of the args; the scan projects a subset of these.)
    public Schema OutputSchema
    {
        get
        {
            var cols = _owner.FunctionOutputColumns(_schema, _func);
            if (cols.Count == 0)
            {
                throw new ArgumentException($"mssql_net: '{_schema}.{_func}' has no describable result set");
            }
            var sb = new StringBuilder("SELECT ");
            for (int i = 0; i < cols.Count; i++)
            {
                sb.Append(i > 0 ? ", " : "").Append("CAST(NULL AS ").Append(cols[i].sqlType).Append(") AS ")
                  .Append(SqlServerCatalog.Quote(cols[i].name));
            }
            sb.Append(" WHERE 1 = 0");
            using var s = _owner.ExecuteQuery(sb.ToString());
            return s.Schema;
        }
    }

    // Execute over the constant POSITIONAL args (@a0, @a1, … — disjoint from the filter's @p*), streaming
    // `SELECT <projected> FROM [s].[f](@a…) WHERE <filter>` lazily. The returned stream's schema reflects the
    // pushed projection, so it matches the projected batches — return it directly (never re-wrap it with the
    // full OutputSchema). ScanFromSource consumes the filter constants synchronously while building the SELECT.
    public IArrowArrayStream ExecuteScan(RecordBatch args, string? specJson, IArrowArrayStream? filterValues)
    {
        // The caller owns `args` (a session binding reuses it across executions); read the values
        // synchronously here (copied into the SqlParameters) without disposing the batch.
        var argParams = new List<SqlParameter>();
        var argList = new StringBuilder();
        var fields = args.Schema.FieldsList;
        for (int c = 0; c < fields.Count; c++)
        {
            if (c > 0)
            {
                argList.Append(", ");
            }
            var pn = $"@a{c}";
            argList.Append(pn);
            argParams.Add(new SqlParameter(pn, ArrowValueReader.ReadScalar(args.Column(c), 0) ?? (object)DBNull.Value));
        }
        return _owner.ScanFromSource($"{_qualified}({argList})", argParams, specJson, filterValues);
    }
}
