using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// A provider-authored custom table function, implemented in C# over Arrow. Mirrors DuckDB's bind→execute:
/// <see cref="Bind"/> receives the constant call arguments and returns a per-call
/// <see cref="ITableFunctionBinding"/> whose output schema MAY depend on those arguments (e.g. a
/// generic <c>query(sql)</c> or a function whose column set follows a parameter). Surfaced into every
/// attached catalog and resolved as <c>SELECT * FROM db.SchemaName.Name(args)</c> through the same
/// table-function path as a discovered TVF — the catalog dispatches to the binding instead of generating SQL.
///
/// The definition object is shared (registered once); the per-call state lives on the binding it produces.
/// For a fixed (arg-independent) output schema, derive from <see cref="StaticTableFunction"/> to keep the
/// implementation a few lines.
/// </summary>
public interface ITableFunction
{
    /// <summary>Function name. Catalog: <c>SELECT * FROM db.schema.Name(args)</c>; global: the bare name.</summary>
    string Name { get; }

    /// <summary>
    /// The call signature: ONE schema whose fields are the parameters in declared order. A field's STYLE —
    /// positional (the default) or named — rides its metadata; build them with <see cref="Params"/>.
    /// </summary>
    /// <remarks>
    /// <para>A NAMED parameter is how an OPTIONAL argument is expressed: DuckDB positional table arguments
    /// have no defaults, so without one a function with three optional knobs forces every caller to write
    /// <c>fn(NULL, NULL, NULL)</c>. Named parameters must come LAST — DuckDB's own rule, checked at
    /// declaration time by <see cref="Params.Validate"/>.</para>
    /// <para><b>The binding reads arguments BY POSITION</b>, and position is simply this schema's field
    /// order. An argument the caller omitted arrives as NULL — deliberately indistinguishable from an
    /// explicit NULL, since that is the semantic a nullable trailing argument already had.</para>
    /// </remarks>
    Schema Parameters { get; }

    /// <summary>
    /// Whether this function's source orders strings the same way DuckDB does (byte/binary) — so string
    /// ordering comparisons (<c>&lt;</c> <c>&gt;</c> …) and <c>BETWEEN</c> are superset-safe to push into it.
    /// Default <c>false</c> (conservative: a function whose filter is rendered to a collation-dependent engine
    /// only gets string EQUALITY pushed). A byte-ordered reader — e.g. a Delta/Parquet lakehouse scan, whose
    /// statistics are byte-ordered like DuckDB's default — sets it <c>true</c>. (Only the host-FS / pushdown
    /// global table path consults this; a pure-compute function that ignores the pushed filter is unaffected.)
    /// </summary>
    bool StringOrderPushable => false;

    /// <summary>
    /// Binds one call: <paramref name="args"/> is a single (1-row) batch whose columns are the constant
    /// argument values (positional, matching <see cref="Parameters"/>). Returns a per-call binding carrying
    /// the resolved output schema + any state. Mirrors DuckDB's bind.
    /// </summary>
    ITableFunctionBinding Bind(RecordBatch args);
}

/// <summary>A catalog-bound custom table function (attach-time scope) — <see cref="ITableFunction"/> plus the
/// <see cref="SchemaName"/>. For a connection-free, ATTACH-free table function, implement the base
/// <see cref="ITableFunction"/> and declare it as a global instead.</summary>
public interface ICatalogTableFunction : ITableFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }
}

/// <summary>
/// A bound table-function call (one per invocation; disposed after its scan). Holds the resolved
/// <see cref="OutputSchema"/> (which may depend on the bound arguments) and produces the rows. Its
/// <see cref="Execute"/> result must be a self-contained stream (owning any resources it needs) — the
/// binding is disposed as soon as the scan's stream has been handed off.
/// </summary>
public interface ITableFunctionBinding : System.IDisposable
{
    /// <summary>The result columns (names + Arrow types), resolved for this call's arguments.</summary>
    Schema OutputSchema { get; }

    /// <summary>
    /// Whether the rows this binding yields are already FILTERED by the pushed predicate, so DuckDB need not
    /// re-apply it.
    /// </summary>
    /// <remarks>
    /// ⚠ THIS IS A GUARANTEE ABOUT THE RESULT, NOT A STATEMENT THAT THE FILTER WAS LOOKED AT. A binding may
    /// push the predicate downward for file / row-group SKIPPING and still return a SUPERSET — engineered-wood
    /// does exactly that (it prunes files and row groups, then never re-checks per row), so
    /// <c>fabricator_delta_scan</c> pushes the filter AND answers <c>false</c> here. Answering <c>true</c>
    /// without filtering every row is a WRONG ANSWER, not a missed optimisation: DuckDB will not re-apply.
    /// <para>⚠ <b>NOTHING READS THIS FLAG TODAY</b> — every binding declares it and no host code consults it,
    /// so a binding answering <c>true</c> would gain nothing and a binding answering <c>false</c> loses
    /// nothing. It is vocabulary for stating a guarantee, not yet a switch. ⚠ Do NOT read that as "so the
    /// value does not matter": the day it is wired, a <c>true</c> written carelessly becomes silently dropped
    /// rows. Its sibling <see cref="SupportsProjectionPushdown"/> IS read, so the two are not in the same
    /// state — that asymmetry is the whole reason they were split.</para>
    /// <para>Wiring it means having the host stop re-applying the predicate for a claiming binding, which on
    /// the CATALOG side is the <c>filter_pushdown = true</c> decision made from the provider's own
    /// <c>exact_filter_pushdown</c> capability (<c>fabricator_table_entry.cpp</c> /
    /// <c>FetchExactFilterPushdown</c>) — i.e. this flag is the table-FUNCTION analogue of a mechanism that
    /// already exists one layer over. Reuse that shape rather than inventing a second one.</para>
    /// </remarks>
    bool SupportsFilterPushdown { get; }

    /// <summary>
    /// Whether the batches this binding yields contain ONLY the columns the scan asked for, in which case the
    /// host maps them onto the declared schema BY NAME.
    /// </summary>
    /// <remarks>
    /// <para>⚠ ALSO A GUARANTEE ABOUT THE RESULT. A binding answering <c>true</c> must emit exactly the
    /// projected set for THIS scan — the batches and the stream's declared schema have to agree, or the host
    /// ingests columns that are not there (arrow_ingest reads past the end: SIGSEGV, not an error).</para>
    /// <para>⚠ Separate from <see cref="SupportsFilterPushdown"/> ON PURPOSE. They were ONE flag until
    /// 2026-08-13, which made the two Delta readers unable to prune columns: both push the filter and neither
    /// can promise a filtered result, so the single flag had to be <c>false</c> — and that also switched off
    /// the projection, which they could have honoured. One axis was hostage to the other.</para>
    /// <para><b>THIS one IS read — at exactly ONE site</b>, <c>BindingBoundTableFunction.Execute</c>, which
    /// narrows the DECLARED stream schema to the requested columns when it is true and hands over the full
    /// <see cref="OutputSchema"/> when it is false. That is the whole switch: the declaration is the contract
    /// with <c>arrow_ingest</c>, so declaring the projected set is what makes emitting the projected set legal.
    /// ⚠ Its sibling <see cref="SupportsFilterPushdown"/> is read NOWHERE (see its own remarks), and
    /// <see cref="IBoundTableFunction.MapResultByName"/> is a THIRD question that no binding is consulted
    /// about — the four <c>BindingBoundTableFunction</c> call sites pass a literal. Do not read the three as
    /// one mechanism.</para>
    /// <para>⚠ A comment here used to read <i>"NOTHING READS EITHER FLAG YET"</i>. That was accurate when the
    /// flags were split and went stale the moment the projection was wired, which is the failure this file
    /// keeps recording: a stale justification stops the next reader looking. <b>The claim is one grep</b>
    /// (<c>grep -rn "SupportsProjectionPushdown" dotnet/ --include=*.cs</c> minus declarations and doc
    /// comments) — re-run it before trusting this paragraph rather than after being surprised by it.</para>
    /// </remarks>
    bool SupportsProjectionPushdown { get; }

    /// <summary>
    /// Produces the result rows, streamed asynchronously. <paramref name="scan"/> carries the projection +
    /// filter pushdown request; each half may be ignored independently, and DuckDB re-applies whichever the
    /// binding does not claim (see <see cref="SupportsFilterPushdown"/> /
    /// <see cref="SupportsProjectionPushdown"/>). ⚠ A binding may still USE the filter for skipping while
    /// claiming neither. Yield lazily (an async iterator) to stream large results without buffering — the
    /// host pulls one batch at a time.
    /// </summary>
    IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default);
}

/// <summary>
/// The projection + filter pushdown request handed to <see cref="ITableFunctionBinding.Execute"/>.
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
