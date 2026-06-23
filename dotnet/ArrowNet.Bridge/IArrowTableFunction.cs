using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// A provider-authored custom table function, implemented in C# over Arrow. Mirrors DuckDB's bind→execute:
/// <see cref="Bind"/> receives the constant call arguments and returns a per-call
/// <see cref="IArrowTableFunctionBinding"/> whose output schema MAY depend on those arguments (e.g. a
/// generic <c>query(sql)</c> or a function whose column set follows a parameter). Surfaced into every
/// attached catalog and resolved as <c>SELECT * FROM db.SchemaName.Name(args)</c> through the same
/// table-function path as a discovered TVF — the catalog dispatches to the binding instead of generating SQL.
///
/// The definition object is shared (registered once); the per-call state lives on the binding it produces.
/// For a fixed (arg-independent) output schema, derive from <see cref="StaticTableFunction"/> to keep the
/// implementation a few lines.
/// </summary>
public interface IArrowTableFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }

    /// <summary>Function name, as called: <c>SELECT * FROM db.SchemaName.Name(args)</c>.</summary>
    string Name { get; }

    /// <summary>The argument fields, in positional order (names + Arrow types) — the call signature.</summary>
    Schema Parameters { get; }

    /// <summary>
    /// Binds one call: <paramref name="args"/> is a single (1-row) batch whose columns are the constant
    /// argument values (positional, matching <see cref="Parameters"/>). Returns a per-call binding carrying
    /// the resolved output schema + any state. Mirrors DuckDB's bind.
    /// </summary>
    IArrowTableFunctionBinding Bind(RecordBatch args);
}

/// <summary>
/// A bound table-function call (one per invocation; disposed after its scan). Holds the resolved
/// <see cref="OutputSchema"/> (which may depend on the bound arguments) and produces the rows. Its
/// <see cref="Execute"/> result must be a self-contained stream (owning any resources it needs) — the
/// binding is disposed as soon as the scan's stream has been handed off.
/// </summary>
public interface IArrowTableFunctionBinding : System.IDisposable
{
    /// <summary>The result columns (names + Arrow types), resolved for this call's arguments.</summary>
    Schema OutputSchema { get; }

    /// <summary>
    /// Whether projection + filter pushdown in <see cref="Execute"/>'s <see cref="TableFunctionScan"/> are
    /// honored at the source (true for an inline SQL TVF) or must be re-applied by DuckDB above the scan
    /// (false for a pure-C# function or a stored procedure, which can't be inline-wrapped).
    /// </summary>
    bool SupportsPushdown { get; }

    /// <summary>
    /// Produces the result rows, streamed asynchronously. <paramref name="scan"/> carries the projection +
    /// filter pushdown request; a binding that returns <see cref="SupportsPushdown"/> == false ignores it
    /// (DuckDB re-applies). Yield lazily (an async iterator) to stream large results without buffering — the
    /// host pulls one batch at a time.
    /// </summary>
    IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default);
}

/// <summary>
/// The projection + filter pushdown request handed to <see cref="IArrowTableFunctionBinding.Execute"/>.
/// <see cref="SpecJson"/> (null =&gt; SELECT *) is <c>{ "columns": [...], "filter": &lt;tree&gt; }</c>; the
/// filter tree references typed constants by index into <see cref="FilterValues"/> (null =&gt; no filter).
/// Same shape as the table-scan pushdown; a pure-C# binding ignores both.
/// </summary>
public sealed class TableFunctionScan
{
    public TableFunctionScan(string? specJson, IArrowArrayStream? filterValues)
    {
        SpecJson = specJson;
        FilterValues = filterValues;
    }

    public string? SpecJson { get; }
    public IArrowArrayStream? FilterValues { get; }

    /// <summary>The parsed <see cref="SpecJson"/> (projection + filter + time travel), or null when there is
    /// none — a convenience for custom table functions that want to honor the pushdown spec.</summary>
    public ScanSpec? Spec => ScanSpec.Parse(SpecJson);
}
