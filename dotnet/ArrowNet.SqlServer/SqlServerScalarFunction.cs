using System;
using System.Collections.Generic;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using ArrowNet.Bridge;
using Microsoft.Data.SqlClient;

namespace ArrowNet.SqlServer;

/// <summary>
/// A discovered SQL Server scalar UDF, expressed as an <see cref="ICatalogScalarFunction"/> so the catalog
/// dispatches every scalar function — provider-authored C# and discovered SQL — through one uniform path.
/// Parameter/return schemas come from <c>INFORMATION_SCHEMA</c> (via the catalog's shared schema queries);
/// <see cref="Invoke"/> applies the UDF over the input batch with chunked, parameterized
/// <c>SELECT [s].[f](@..) UNION ALL …</c> queries (≤ ~2100 params/query) and merges the per-chunk results
/// into one result column. Created on demand per call (cheap — it holds only the catalog + name).
/// </summary>
internal sealed class SqlServerScalarFunction : ICatalogScalarFunction
{
    private readonly SqlServerCatalog _catalog;

    public SqlServerScalarFunction(SqlServerCatalog catalog, string schemaName, string name)
    {
        _catalog = catalog;
        SchemaName = schemaName;
        Name = name;
    }

    public string SchemaName { get; }
    public string Name { get; }

    public Schema Parameters
    {
        get
        {
            using var s = _catalog.RoutineParamSchemaQuery(SchemaName, Name);
            return s.Schema;
        }
    }

    public Field Result
    {
        get
        {
            using var s = _catalog.RoutineReturnSchemaQuery(SchemaName, Name); // throws if not a scalar function
            return s.Schema.FieldsList[0];
        }
    }

    // One DataChunk in (≤ STANDARD_VECTOR_SIZE rows), one result column out. The only reason this isn't a
    // single SELECT is SQL Server's ~2100-parameter cap, which splits the batch into a few sub-queries whose
    // result columns are merged into one array via a typed builder (NOT array concatenation).
    public IArrowArray Invoke(RecordBatch args)
    {
        int paramCount = args.ColumnCount;
        var rows = ReadRows(args, paramCount);
        var qualified = SqlServerCatalog.Quote(SchemaName) + "." + SqlServerCatalog.Quote(Name);
        int maxRows = Math.Max(1, 2000 / Math.Max(1, paramCount));

        var resultValues = new List<object?>(rows.Count);
        IArrowType? resultType = null;
        for (int start = 0; start < rows.Count; start += maxRows)
        {
            int end = Math.Min(start + maxRows, rows.Count);
            var sb = new StringBuilder();
            var sqlParams = new List<SqlParameter>();
            for (int r = start; r < end; r++)
            {
                if (r > start)
                {
                    sb.Append(" UNION ALL ");
                }
                sb.Append("SELECT ").Append(qualified).Append('(');
                for (int c = 0; c < paramCount; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }
                    var pn = $"@p{r}_{c}";
                    sb.Append(pn);
                    sqlParams.Add(new SqlParameter(pn, rows[r][c] ?? (object)DBNull.Value));
                }
                sb.Append(") AS result");
            }
            using var sub = _catalog.ExecuteQuery(sb.ToString(), sqlParams);
            resultType ??= sub.Schema.FieldsList[0].DataType; // the UDF's return type, as SQL Server reports it
            using var reader = new ArrowDataReader(sub);
            while (reader.Read())
            {
                resultValues.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
            }
        }

        // An empty input batch yields no sub-query; fall back to the declared return type for the (empty) array.
        return BuildResultColumn(resultType ?? Result.DataType, resultValues);
    }

    // Reads the argument batch into boxed rows (reuses ArrowDataReader, the generic Arrow→value reader).
    private static List<object?[]> ReadRows(RecordBatch args, int paramCount)
    {
        var rows = new List<object?[]>(args.Length);
        using var reader = new ArrowDataReader(new InMemoryArrayStream(args.Schema, new[] { args }));
        while (reader.Read())
        {
            var vals = new object?[paramCount];
            for (int c = 0; c < paramCount; c++)
            {
                vals[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
            }
            rows.Add(vals);
        }
        return rows;
    }

    // Builds the one-column result array (typed as the UDF's return type) from boxed values (null => SQL NULL).
    // Mirrors the aggregate result builder; covers the common scalar return types.
    internal static IArrowArray BuildResultColumn(IArrowType type, IReadOnlyList<object?> values)
    {
        int n = values.Count;
        switch (type)
        {
            case Int16Type:
            {
                var b = new Int16Array.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt16(v)); }
                return b.Build();
            }
            case Int32Type:
            {
                var b = new Int32Array.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt32(v)); }
                return b.Build();
            }
            case Int64Type:
            {
                var b = new Int64Array.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt64(v)); }
                return b.Build();
            }
            case FloatType:
            {
                var b = new FloatArray.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToSingle(v)); }
                return b.Build();
            }
            case DoubleType:
            {
                var b = new DoubleArray.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToDouble(v)); }
                return b.Build();
            }
            case BooleanType:
            {
                var b = new BooleanArray.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToBoolean(v)); }
                return b.Build();
            }
            case StringType:
            {
                var b = new StringArray.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(v.ToString()); }
                return b.Build();
            }
            case Decimal128Type dt:
            {
                var b = new Decimal128Array.Builder(dt).Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToDecimal(v)); }
                return b.Build();
            }
            default:
                throw new NotSupportedException($"mssql_net: scalar return type {type} is not supported");
        }
    }
}
