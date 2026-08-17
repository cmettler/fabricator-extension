using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// The managed side of one <c>table_open</c> handle (ABI v72, docs/catalog-table-abstraction.md §2.4).
/// Wraps the table DEFINITION (+ the reference's AT clause) — deliberately NOT a binding: the C++ catalog
/// entry is shared across transactions while a bound <see cref="ITableBinding"/> is per-(table × transaction), so
/// every call re-binds against the CURRENT ambient transaction (§6's lazy-bind default — "the handle is the
/// DEFINITION, and each call resolves the ambient txn's binding; ambient stays the transport"). That is what
/// makes the handle's lifetime trivial: a definition holds no state, so a handle kept open in the entry
/// graveyard until catalog teardown cannot go stale — staleness is governed entirely by the binding layer,
/// which the per-transaction invalidation already owns.
/// </summary>
internal sealed class TableSession
{
    private readonly IBackendCatalog _catalog;
    private readonly ITable _definition;
    private readonly TableAt? _at;

    internal TableSession(IBackendCatalog catalog, ITable definition, TableAt? at)
    {
        _catalog = catalog;
        _definition = definition;
        _at = at;
    }

    /// <summary>
    /// Binds against the current ambient transaction and runs <paramref name="body"/>, disposing the
    /// binding afterwards when it is CALLER-OWNED per the <see cref="ITableBinding"/> ownership rule (no
    /// transaction, or an AT bind — a memoized transaction-owned binding is never disposed here).
    /// </summary>
    private T With<T>(Func<ITableBinding, T> body)
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

    /// <summary>The <c>table_info</c> answer — ONE typed JSON doc (ABI v73):
    /// <c>{"rowid":[...], "virtual":[{"name":..,"type":..}, ...]}</c>, rowid names in key order, both
    /// arrays always present. Provider-agnostic re-encoding of the two typed <see cref="ITableBinding"/> members,
    /// written with <see cref="Utf8JsonWriter"/> so user-controlled identifiers are escaped properly (the
    /// host parses with a real parser, yyjson — never the string-find shortcut, which is safe only for
    /// docs whose values are bare booleans). The stats members deliberately do NOT ride along (they stay a
    /// separate lazy entry so entry materialization — i.e. catalog ENUMERATION — never pays a stats
    /// query).</summary>
    internal string InfoJson() => With(t =>
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("rowid");
            foreach (var column in t.RowIdColumns())
            {
                json.WriteStringValue(column);
            }
            json.WriteEndArray();
            json.WriteStartArray("virtual");
            foreach (var vc in t.VirtualColumns())
            {
                json.WriteStartObject();
                json.WriteString("name", vc.Name);
                json.WriteString("type", vc.DuckDbType);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    });

    /// <summary>The <c>table_stats</c> answer — ONE typed JSON doc (ABI v73):
    /// <c>{"row_count":N, "ndv":{"col":N, ...}}</c>; <c>row_count</c> ABSENT means unknown (the old kinds
    /// 4/5 crossed these numbers as text). Lazy BY CONTRACT: the host calls this at first scan, never at
    /// entry materialization, and the warehouse never-issue-a-swallowable-statement rule lives inside the
    /// providers' typed cores (null/empty answers, no probe).</summary>
    internal string StatsJson() => With(t =>
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            if (t.ApproximateRowCount() is { } rowCount)
            {
                json.WriteNumber("row_count", rowCount);
            }
            json.WriteStartObject("ndv");
            foreach (var e in t.ColumnNdv())
            {
                json.WriteNumber(e.ColumnName, e.Ndv);
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    });

    /// <summary>The <c>table_scan</c> answer. The returned stream MAY outlive a caller-owned binding —
    /// sound because every provider's <see cref="ITableBinding.Scan"/> delegates by identity into the catalog and
    /// the stream owns its own resources (stated on the interface); the AT clause still rides
    /// <paramref name="specJson"/> for the scan itself (the host's BuildScanSpec, unchanged — the session's
    /// AT matters to <see cref="SchemaStream"/>, where the as-of column layout is resolved).</summary>
    internal IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues) =>
        With(t => t.Scan(specJson, filterValues));

    /// <summary>The <c>table_alter</c> action (ABI v74). Deliberately does NOT go through
    /// <see cref="With"/>: an ALTER is CATALOG work — provider caches, the transaction buffer, the DDL touch
    /// record — so it is dispatched on the catalog with the definition's names, and binding a table first
    /// would resolve a shape the statement is about to invalidate. What the handle bought is the identity
    /// (no name pair on the wire) and the typed doc, not a relocation of the work: the providers read the
    /// AMBIENT transaction themselves, which is how Delta buffers a schema-evolution ALTER into an open
    /// transaction rather than committing it alone.</summary>
    internal void Alter(AlterTableSpec spec, Field? column) =>
        _catalog.AlterTable(spec, _definition.SchemaName, _definition.TableName, column);
}
