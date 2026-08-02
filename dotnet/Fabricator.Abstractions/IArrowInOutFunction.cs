using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// A bound table-in-out call for the Phase 6 streaming exchange. Produced by
/// <c>IBackendCatalog.InOutBind</c> (resolving cost args + the input-table schema) and consumed by the
/// framework pump (<see cref="InOutExchangeStream"/>). <see cref="OutputSchema"/> is the FULL output
/// (input echo ++ the function's own columns). <see cref="DoExchange"/> is the streaming transform:
/// <paramref name="input"/> yields one <see cref="RecordBatch"/> per DuckDB input chunk (ends at EOF), and
/// the returned enumerable is the output the framework maps onto DuckDB's operator contract — a non-empty
/// batch = HAVE_MORE_OUTPUT, a <b>length-0 batch</b> = NEED_MORE_INPUT (the per-input-chunk sentinel the
/// author yields), end-of-enumerable = FINISHED. One binding may run one exchange at a time; it is reused
/// across prepared re-executions and disposed when the bind is torn down.
/// </summary>
public interface IArrowInOutBinding : IDisposable
{
    Schema OutputSchema { get; }

    IAsyncEnumerable<RecordBatch> DoExchange(IAsyncEnumerable<RecordBatch> input, CancellationToken ct = default);
}

/// <summary>
/// Optional capability for a binding that runs against a SQL connection: the framework sets the configured
/// transaction isolation level for the exchange before <see cref="IArrowInOutBinding.DoExchange"/> runs, so
/// the call's one transaction sees a consistent snapshot. Pure-C# bindings need not implement it.
/// </summary>
public interface IArrowInOutIsolation
{
    string IsolationLevel { set; }
}

/// <summary>
/// A provider-authored custom table-in-out function that drives the streaming exchange directly (the
/// free-form shape): <see cref="Bind"/> returns an <see cref="IArrowInOutBinding"/> whose <c>DoExchange</c>
/// the author writes — reading the input stream and yielding output, INCLUDING the length-0 sentinel after
/// each input chunk (cross-chunk state lives in DoExchange locals). Implement this directly when the output
/// schema depends on the call's args; for a FIXED output schema derive from <see cref="StaticInOutFunction"/>
/// (it supplies the <see cref="Bind"/> wiring — you still write DoExchange). Surfaced into the catalog as
/// <c>kind='inout'</c> and resolved by <c>IBackendCatalog.InOutBind</c>.
/// </summary>
public interface IInOutFunction
{
    /// <summary>Function name. Catalog: <c>SELECT * FROM db.schema.Name(&lt;input&gt;)</c>; global: the bare name.</summary>
    string Name { get; }

    /// <summary>
    /// The call signature, including the INPUT TABLE itself: one schema whose fields carry their style in
    /// metadata — build them with <see cref="Params"/>. An in-out function declares exactly one
    /// <see cref="Params.TableInput"/> field plus any constant "cost" args (typically
    /// <see cref="Params.Named"/>, e.g. <c>path := '…'</c>), whose supplied values arrive in
    /// <see cref="Bind"/>'s <c>args</c>.
    /// </summary>
    /// <remarks>
    /// The table input used to be a separate <c>InputSchema</c> member. It is a parameter like any other —
    /// it occupies a positional slot in DuckDB's own binder — and keeping it apart meant a third schema that
    /// nothing but a display column ever read. Declaring it here is also what lets a provider register an
    /// in-out function under any name it likes, instead of the host synthesising one.
    /// </remarks>
    Schema Parameters { get; }

    /// <summary>Binds one call: <paramref name="args"/> (nullable) are the constant "cost" args (1-row batch);
    /// <paramref name="inputSchema"/> is the actual input table's schema. Returns the per-call binding.</summary>
    IArrowInOutBinding Bind(RecordBatch? args, Schema inputSchema);
}

/// <summary>A catalog-bound table-in-out function (attach-time scope) — <see cref="IInOutFunction"/> plus the
/// <see cref="SchemaName"/>. For a connection-free, ATTACH-free in-out, implement the base
/// <see cref="IInOutFunction"/> and declare it as a global instead.</summary>
public interface ICatalogInOutFunction : IInOutFunction
{
    /// <summary>Target catalog schema (e.g. "dbo").</summary>
    string SchemaName { get; }
}
