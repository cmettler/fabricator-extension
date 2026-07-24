using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Shared plumbing for SQL-generating table functions (<see cref="ISqlTableFunction"/> /
/// <see cref="ICatalogSqlTableFunction"/>): validates one call's constant arguments, invokes the provider's
/// generator, and checks the result before it goes back to the host's <c>bind_replace</c> hook (which parses
/// it and substitutes it for the function call). Used by both the global (handle-0) and catalog paths so the
/// contract is identical either way. See docs/macros-and-sqlgen-functions.md §2.
/// </summary>
public static class SqlGen
{
    /// <summary>
    /// Field-metadata marker on a parameter field: <c>"1"</c> = this is a NAMED parameter, not positional.
    /// A <c>table_sql</c> function's <c>get_function_param_schema</c> returns positional fields followed by
    /// named ones, distinguished by this tag — the same field-metadata channel the volatility signal uses
    /// (<see cref="ScalarFunctionMetadata.VolatileKey"/>), so no extra ABI entry is needed. The host splits
    /// them when it registers the DuckDB signature (positional <c>arguments</c> vs <c>named_parameters</c>).
    /// </summary>
    public const string NamedParamKey = "fabricator.named";

    /// <summary>Positional fields ++ named fields tagged with <see cref="NamedParamKey"/> — the single
    /// parameter schema the host reads to register a SQL-generating table function's signature.</summary>
    public static Schema ParamSchema(Schema positional, Schema named)
    {
        var fields = new List<Field>(positional.FieldsList);
        foreach (var f in named.FieldsList)
        {
            var metadata = new Dictionary<string, string>();
            if (f.Metadata is not null)
            {
                foreach (var kv in f.Metadata)
                {
                    metadata[kv.Key] = kv.Value;
                }
            }
            metadata[NamedParamKey] = "1";
            fields.Add(new Field(f.Name, f.DataType, f.IsNullable, metadata));
        }
        return new Schema(fields, metadata: null);
    }

    /// <summary>Generate for a GLOBAL (connection-free) SQL-generating table function.</summary>
    public static string Generate(ISqlTableFunction fn, RecordBatch? args)
    {
        ValidateArgs(fn.Name, fn.Parameters, args);
        return Check(fn.Name, fn.GenerateSql(args ?? EmptyArgs()));
    }

    /// <summary>Generate for a CATALOG-BOUND SQL-generating table function, whose generator also receives the
    /// catalog's ATTACH alias + the live catalog for bind-time lookups.</summary>
    public static string Generate(ICatalogSqlTableFunction fn, SqlGenContext ctx, RecordBatch? args)
    {
        ValidateArgs(fn.Name, fn.Parameters, args);
        return Check(fn.Name, fn.GenerateSql(ctx, args ?? EmptyArgs()));
    }

    /// <summary>
    /// Rejects a NULL value for a NON-NULLABLE declared parameter up front, with a clear message naming the
    /// parameter — the <c>query_table()</c> precedent ("Cannot use NULL as function argument"), except we
    /// honor the provider's own nullability so a function that MEANS to accept NULL simply declares the field
    /// nullable. Columns are matched by field name (the host names them after the parameters) and fall back to
    /// positional order.
    /// </summary>
    private static void ValidateArgs(string fnName, Schema declared, RecordBatch? args)
    {
        if (args is null || args.Length == 0)
        {
            return;
        }
        for (int c = 0; c < args.ColumnCount; c++)
        {
            var array = args.Column(c);
            if (!array.IsNull(0))
            {
                continue;
            }
            var name = args.Schema.FieldsList[c].Name;
            // A named parameter that is not in the positional list is only present when SUPPLIED, and the
            // function declared its type; treat an unknown name as nullable-permitted (the generator sees it).
            var field = declared.GetFieldByName(name)
                        ?? (c < declared.FieldsList.Count ? declared.FieldsList[c] : null);
            if (field is not null && !field.IsNullable)
            {
                throw new ArgumentException(
                    $"fabricator: function '{fnName}' does not accept NULL for argument '{name}'");
            }
        }
    }

    private static string Check(string fnName, string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException(
                $"fabricator: function '{fnName}' generated no SQL (GenerateSql must return one SELECT statement)");
        }
        return sql;
    }

    /// <summary>A zero-column, single-row batch — what a no-argument function's generator receives, so an
    /// implementation never has to null-check.</summary>
    private static RecordBatch EmptyArgs() =>
        new(new Schema(System.Array.Empty<Field>(), metadata: null), System.Array.Empty<IArrowArray>(), 1);
}
