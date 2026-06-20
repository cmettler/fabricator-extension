using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// A provider-authored custom table function, implemented in C# over Arrow. Like
/// <see cref="IArrowScalarFunction"/> but returns rows: it is surfaced into every attached catalog
/// alongside discovered table-valued functions, so it resolves as
/// <c>SELECT * FROM db.SchemaName.Name(args)</c> and runs through the very same table-function path
/// as a discovered TVF — the catalog dispatches to <see cref="Invoke"/> instead of generating SQL.
/// Reuses the existing <c>get_function_param_schema</c> / <c>get_function_output_schema</c> /
/// <c>execute_table</c>; no extra ABI. Arguments are positional (like a TVF's FROM-clause call).
///
/// Projection + filter pushdown do not reach a custom function (there is no SQL to push into): the
/// function returns its full result and DuckDB applies the projection (by column name) and any
/// filters above the scan.
/// </summary>
public interface IArrowTableFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }

    /// <summary>Function name, as called: <c>SELECT * FROM db.SchemaName.Name(args)</c>.</summary>
    string Name { get; }

    /// <summary>The argument fields, in positional order (names + Arrow types) — the call signature.</summary>
    Schema Parameters { get; }

    /// <summary>The result columns (names + Arrow types). Fixed (known at bind time).</summary>
    Schema OutputSchema { get; }

    /// <summary>
    /// Produces the result rows for one call. <paramref name="args"/> is a single (1-row) batch whose
    /// columns are the constant argument values (positional, matching <see cref="Parameters"/>). Each
    /// returned batch must conform to <see cref="OutputSchema"/>; yield lazily to stream large results.
    /// </summary>
    IEnumerable<RecordBatch> Invoke(RecordBatch args);
}
