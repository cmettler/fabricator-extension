using System;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The managed side of one <c>table_open</c> handle (ABI v72, docs/catalog-table-abstraction.md §2.4).
/// Wraps the table DEFINITION (+ the reference's AT clause) — deliberately NOT a binding: the C++ catalog
/// entry is shared across transactions while a bound <see cref="ITable"/> is per-(table × transaction), so
/// every call re-binds against the CURRENT ambient transaction (§6's lazy-bind default — "the handle is the
/// DEFINITION, and each call resolves the ambient txn's binding; ambient stays the transport"). That is what
/// makes the handle's lifetime trivial: a definition holds no state, so a handle kept open in the entry
/// graveyard until catalog teardown cannot go stale — staleness is governed entirely by the binding layer,
/// which the per-transaction invalidation already owns.
/// </summary>
internal sealed class TableSession
{
    private readonly IBackendCatalog _catalog;
    private readonly ITableDefinition _definition;
    private readonly TableAt? _at;

    internal TableSession(IBackendCatalog catalog, ITableDefinition definition, TableAt? at)
    {
        _catalog = catalog;
        _definition = definition;
        _at = at;
    }

    /// <summary>
    /// Binds against the current ambient transaction and runs <paramref name="body"/>, disposing the
    /// binding afterwards when it is CALLER-OWNED per the <see cref="ITable"/> ownership rule (no
    /// transaction, or an AT bind — a memoized transaction-owned binding is never disposed here).
    /// </summary>
    private T With<T>(Func<ITable, T> body)
    {
        var txn = _catalog.ResolveTransaction(AmbientTransaction.Current);
        var bound = _definition.Bind(txn, _at);
        bool callerOwned = txn is null || _at is not null;
        try
        {
            return body(bound);
        }
        finally
        {
            if (callerOwned)
            {
                bound.Dispose();
            }
        }
    }

    /// <summary>The <c>table_schema</c> answer: a zero-row stream whose Arrow SCHEMA is the table's column
    /// layout — the same carrier the old kind-2 used, kept deliberately (the host's PopulateReturnSchema is
    /// the proven import path, incl. VARIANT extension types; a bare ArrowSchema would fork the type
    /// conversion for zero gain). <see cref="ObjectNotFoundException"/> propagates to the export site,
    /// which maps it to the NOT_FOUND status — the absence contract, unchanged, one entry over.</summary>
    internal IArrowArrayStream SchemaStream() =>
        With(t => (IArrowArrayStream)new InMemoryArrayStream(t.Schema, System.Array.Empty<RecordBatch>()));

    /// <summary>The <c>table_info</c> answer — (role, name, type), all UTF-8: one <c>role='rowid'</c> row
    /// per row-identity column (key order, type empty), one <c>role='virtual'</c> row per provider virtual
    /// column (type = the declared DuckDB type text). Provider-agnostic re-encoding of the two typed
    /// <see cref="ITable"/> members; the stats members deliberately do NOT ride along (they stay a separate
    /// lazy entry so entry materialization — i.e. catalog ENUMERATION — never pays a stats query).</summary>
    internal IArrowArrayStream InfoStream() => With(t =>
    {
        var schema = new Schema(new[]
        {
            new Field("role", StringType.Default, nullable: false),
            new Field("name", StringType.Default, nullable: false),
            new Field("type", StringType.Default, nullable: true),
        }, metadata: null);
        var roles = new StringArray.Builder();
        var names = new StringArray.Builder();
        var types = new StringArray.Builder();
        int rows = 0;
        foreach (var column in t.RowIdColumns())
        {
            roles.Append("rowid");
            names.Append(column);
            types.Append(string.Empty);
            rows++;
        }
        foreach (var vc in t.VirtualColumns())
        {
            roles.Append("virtual");
            names.Append(vc.Name);
            types.Append(vc.DuckDbType);
            rows++;
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { roles.Build(), names.Build(), types.Build() }, rows);
        return (IArrowArrayStream)new InMemoryArrayStream(schema, new[] { batch });
    });

    /// <summary>The <c>table_stats</c> answer — (stat, column, value): a typed INT64 value column at last
    /// (the old kinds 4/5 crossed numbers as text). One <c>stat='row_count'</c> row when the provider
    /// surfaces one (absent = unknown), one <c>stat='ndv'</c> row per column with a distinct-count
    /// estimate. Lazy BY CONTRACT: the host calls this at first scan, never at entry materialization, and
    /// the warehouse never-issue-a-swallowable-statement rule lives inside the providers' typed cores
    /// (null/empty answers, no probe).</summary>
    internal IArrowArrayStream StatsStream() => With(t =>
    {
        var schema = new Schema(new[]
        {
            new Field("stat", StringType.Default, nullable: false),
            new Field("column", StringType.Default, nullable: false),
            new Field("value", Int64Type.Default, nullable: false),
        }, metadata: null);
        var stats = new StringArray.Builder();
        var columns = new StringArray.Builder();
        var values = new Int64Array.Builder();
        int rows = 0;
        if (t.ApproximateRowCount() is { } rowCount)
        {
            stats.Append("row_count");
            columns.Append(string.Empty);
            values.Append(rowCount);
            rows++;
        }
        foreach (var e in t.ColumnNdv())
        {
            stats.Append("ndv");
            columns.Append(e.ColumnName);
            values.Append(e.Ndv);
            rows++;
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { stats.Build(), columns.Build(), values.Build() },
                                    rows);
        return (IArrowArrayStream)new InMemoryArrayStream(schema, new[] { batch });
    });

    /// <summary>The <c>table_scan</c> answer. The returned stream MAY outlive a caller-owned binding —
    /// sound because every provider's <see cref="ITable.Scan"/> delegates by identity into the catalog and
    /// the stream owns its own resources (stated on the interface); the AT clause still rides
    /// <paramref name="specJson"/> for the scan itself (the host's BuildScanSpec, unchanged — the session's
    /// AT matters to <see cref="SchemaStream"/>, where the as-of column layout is resolved).</summary>
    internal IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues) =>
        With(t => t.Scan(specJson, filterValues));
}
