using System;
using System.Collections.Generic;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using EngineeredWood.DeltaLake.Table;

namespace Fabricator.Bridge;

/// <summary>The Delta provider's <see cref="ITableDefinition"/> — identity + the bind factory, transient in
/// the current transport (created per metadata/scan crossing by <see cref="DeltaCatalog.GetTable"/>).</summary>
internal sealed class DeltaTableDefinition : ITableDefinition
{
    private readonly DeltaCatalog _catalog;

    internal DeltaTableDefinition(DeltaCatalog catalog, string schemaName, string tableName)
    {
        _catalog = catalog;
        SchemaName = schemaName;
        TableName = tableName;
    }

    public string SchemaName { get; }
    public string TableName { get; }

    /// <summary>
    /// A plain bind against a live <see cref="DeltaTransaction"/> is MEMOIZED — it returns the
    /// transaction's one <see cref="DeltaBoundTable"/> for the path (which is also the append buffer, the
    /// snapshot pin and the open-table reuse: §2.3's "PendingAppends + the DeltaTableCache entry + the
    /// SnapshotPinning pin, unified"). An AT bind or a transaction-free bind is a fresh caller-owned
    /// instance — time travel is a property of the reference.
    /// </summary>
    public ITable Bind(ITransaction? transaction, TableAt? at = null)
    {
        string path = _catalog.TablePath(SchemaName, TableName);
        if (at is null && transaction is DeltaTransaction dt)
        {
            var bound = dt.GetOrCreate(path);
            bound.SetNames(SchemaName, TableName);
            return bound;
        }
        return new DeltaBoundTable(_catalog, SchemaName, TableName, path, at);
    }
}

/// <summary>
/// ONE Delta table bound to ONE DuckDB transaction (slice 4c of docs/catalog-table-abstraction.md §2.3) —
/// the object that used to be spelled as THREE stores keyed (txn, path): <c>DeltaTxnBuffer.PendingAppends</c>
/// (the buffered-write actions, verbatim below), the <c>DeltaTxnScope</c> pin map (the snapshot pin is a
/// plain field now — the 4b delegating property collapsed into its target), and the scope's open-table map
/// (the per-transaction <c>DeltaTable</c> reuse). Owned by its <see cref="DeltaTransaction"/> when memoized;
/// an AT or transaction-free bind is transient and caller-owned.
///
/// <para><b>THE BUFFER HALF (eager-write model, explicit transactions).</b> Plain appends buffer ACTIONS
/// here instead of committing per statement — the data is on storage at statement time and the catalog's
/// CommitTransaction flushes each table's buffer as ONE atomic Delta commit; RollbackTransaction discards it
/// and reclaims the eagerly-written files. In autocommit the flush fires at statement end, so behaviour is
/// identical to per-statement commits.</para>
///
/// <para><b>THE READ HALF (pin + open reuse).</b> The snapshot PIN is the transaction's repeatable-read
/// contract for this table — resolved once against the transaction's first-pin instant, never moved within
/// the transaction (every write is first-wins), and it SURVIVES <see cref="DropOpen"/> (the mutating entry
/// points drop the shared open only; the asymmetry is gated by <c>verify_delta_autocommit_pin</c> §12). The
/// shared OPEN table exists because the several read crossings one statement makes against one table should
/// replay the <c>_delta_log</c> ONCE (measured: 195 s of 291 s in redundant snapshot builds on a Fabric
/// table at v1850) — see the sharing rules on <see cref="TryGetOpen"/>.</para>
/// </summary>
internal sealed class DeltaBoundTable : ITable
{
    /// <summary>Memoized form: created by <see cref="DeltaTransaction.GetOrCreate"/>; the catalog comes from
    /// the owner. <c>Path</c> is mutable for the created-table RENAME re-key
    /// (<see cref="DeltaTransaction.RenameTable"/>) — the PIN re-keys with the object now, structurally.</summary>
    internal DeltaBoundTable(DeltaTransaction owner, string path)
    {
        Owner = owner;
        Path = path;
    }

    /// <summary>Transient form: an AT bind or a transaction-free bind (<see cref="DeltaTableDefinition.Bind"/>).
    /// Holds no buffer anything can flush and never retains an open (see <see cref="CanRetainOpen"/>).</summary>
    internal DeltaBoundTable(DeltaCatalog catalog, string schemaName, string tableName, string path, TableAt? at)
    {
        _catalog = catalog;
        SchemaName = schemaName;
        TableName = tableName;
        Path = path;
        _at = at;
    }

    private readonly DeltaCatalog? _catalog;
    private readonly TableAt? _at;

    /// <summary>The owning transaction — null for a transient (AT / transaction-free) bind.</summary>
    internal DeltaTransaction? Owner { get; }
    internal string Path { get; set; }

    internal DeltaCatalog Catalog => _catalog ?? Owner!.Catalog;

    /// <summary>The owner's DuckDB transaction id (diagnostic: the open-miss log line) — 0 when transient.</summary>
    internal long OwnerId => Owner?.Id ?? 0;

    /// <summary>The DuckDB-visible names, filled by <see cref="DeltaTableDefinition.Bind"/> (a memoized
    /// instance may be created first by a buffered-write path, which keys by PATH and has no names in hand).
    /// OVERWRITE, not first-wins: Bind derives the path FROM the names it sets, so the pair is consistent
    /// by construction — and after a created-table RENAME re-key the binding's old names are exactly what
    /// must NOT survive (first-wins made the post-swap scan of `m` run as `m__dbt_tmp` and open a folder
    /// the swap had renamed away).</summary>
    internal string? SchemaName { get; private set; }
    internal string? TableName { get; private set; }

    internal void SetNames(string schemaName, string tableName)
    {
        SchemaName = schemaName;
        TableName = tableName;
    }

    // ── the buffered-write half (the former DeltaTxnBuffer.PendingAppends, verbatim) ─────────────────────

    public Schema? BatchSchema;
    public List<RecordBatch> Batches { get; } = new();
    public List<WrittenDataFile> Files { get; } = new();
    public long Rows;

    // ---- Buffered DML (slice 2, explicit transactions only) ----
    // Deleted ABSOLUTE row positions per PINNED-snapshot file ordinal (transient-rowid encoding). A
    // buffered UPDATE contributes its old rows here + its post-image rows to Batches. Flushed as
    // deletion-vector remove/add actions fused into the ONE commit; validity is tied to PinnedVersion
    // (the flush conflict-aborts if the table moved).
    public Dictionary<int, HashSet<long>> DeletedByOrdinal { get; } = new();

    // The table's effective isolation (delta.isolationLevel): true = Serializable, false = WriteSerializable
    // (our catalog default when the property is absent — NOT "the Spark default", which is measured to be
    // Serializable; see DeltaCatalog._serializable). Read once per (txn, table) and cached — the OCC
    // conflict check + row-level relaxation at flush honor the TABLE's property, not a catalog-wide flag.
    public bool? Serializable;
    public bool HasAppend;
    public bool HasDelete;
    public bool HasUpdate;

    // ---- Buffered ALTER ADD COLUMN (slice 3, explicit transactions only) ----
    // The pending schema change: the metaData (+ merged protocol-upgrade) action fuses into the ONE
    // commit at flush; reads/binds mid-transaction overlay PendingArrowSchema (missing columns
    // backfilled as typed NULLs); writes run schema-overridden with PendingDeltaSchema (whose added
    // columns already carry their column-mapping ids/physical names). Chained adds compose (each
    // computes against the previous pending metadata).
    public EngineeredWood.DeltaLake.Actions.MetadataAction? PendingMetadata;
    public EngineeredWood.DeltaLake.Actions.ProtocolAction? PendingProtocol;
    public EngineeredWood.DeltaLake.Schema.StructType? PendingDeltaSchema;
    public Schema? PendingArrowSchema;
    public bool HasAlter;
    // RENAME overlay map: pending (new) TOP-LEVEL name -> the COMMITTED name the data is stored under
    // (composed across chained renames; a renamed pending-ADDed column has no entry — its committed
    // read is a NULL backfill either way). AlterOps tracks the buffered kinds for commitInfo.operation.
    public Dictionary<string, string> RenameMap { get; } = new(StringComparer.Ordinal);
    public HashSet<string> AlterOps { get; } = new(StringComparer.Ordinal);

    // ---- CREATE TABLE / CTAS inside an explicit transaction (hoist slice 5) ----
    // ⚠ THIS FLAG CHANGED MEANING. It was `PendingCreate` — "the create has not happened yet", the
    // table existing ONLY in this buffer until the flush created it. The create is now IMMEDIATE (the
    // same path an autocommit CREATE takes), so what the buffer needs to remember is the opposite fact:
    // that THIS transaction is the one that created the table, which is the only thing entitling
    // ROLLBACK to drop it. Everything else the deferred create carried (the parked schema, partition and
    // sort columns, WITH options, the pre-assigned column-mapping schema) went with it — the table
    // itself now holds all of that.
    //
    // The trade, accepted deliberately (docs/delta-transaction-hoist.md §3): v0 is visible to other
    // sessions for the transaction's life, and a ROLLBACK whose drop fails leaves an empty table behind.
    // What it buys: DELETE/UPDATE on a table created in the same transaction (both used to throw
    // NotSupportedException), the streaming native write for such a table, and the CDF probe.
    public bool CreatedInTxn;

    // ---- CDF capture (CDF tables in explicit transactions) ----
    // _change_data files are written at STATEMENT time (the rows are in hand: appended batches,
    // read-back deleted rows, update pre/post images) and — since hoist slice 1b+2 — their actions are
    // staged straight into HeldTxn rather than parked here. Because a commit carrying ANY cdc action is
    // read cdc-ONLY by the CDF reader (inference disabled), EVERY buffered statement on a CDF table
    // writes its cdc counterpart, inserts included. CdfEnabled caches the per-(txn, table) probe.
    public bool? CdfEnabled;

    // ---- Eager identity appends (chained high-water marks) ----
    // Identity values are GENERATED at statement time from the pinned snapshot's HWM (chained here
    // across the transaction's statements) and baked into the eagerly-written files; the flush
    // commits the final marks as the fused commit's metaData. A concurrent identity-consuming
    // commit necessarily carries its own metaData action -> the rebase metadata check aborts us
    // (Spark's concurrent-identity policy), so baked values never land on a moved HWM.
    public Dictionary<string, long> PendingIdentityHwm { get; } = new(StringComparer.Ordinal);

    // ---- APPLICATION TRANSACTION versions (Delta `txn` action — idempotent appends) ----
    // appId -> (version to commit, expected previous version — null = "must not exist yet").
    // Parked by delta.set_transaction_version; the flush validates the CAS against the LATEST
    // snapshot and emits one `txn` action per app in the SAME fused commit.
    public Dictionary<string, (long Version, long? Expected)> AppTxnVersions { get; } =
        new(StringComparer.Ordinal);

    // ---- The EW transaction machinery, OWNED BY THIS ENTRY rather than by the flush's scope ----
    // (hoist slice 1a — docs/delta-transaction-hoist.md). The flush used to open the table and
    // `await using` the transaction inside one method, so both died with the call. Parking them here
    // moves their LIFETIME to the (DuckDB txn, table) pair, which is the prerequisite for staging at
    // statement time instead of at COMMIT.
    //
    // ⚠ Holding a DeltaTable across ABI calls is only safe because of 142b350: the host-FS opener is a
    // ClientContext* valid for ONE call, and DuckDbTableFileSystem now reads AmbientOpener.Current
    // first rather than the value captured at construction. That fix's own comment predicted this
    // ("becomes load-bearing the moment something is cached"). All three ITableFileSystem
    // implementations were checked; see the doc's feasibility table.
    //
    // ⚠ DISPOSE txn BEFORE table — the transaction's cleanup needs the table's filesystem. The flush
    // expressed that by declaring the `await using` inside the try; here it is DisposeHeld's ordering.
    public DeltaTable? HeldTable;
    public EngineeredWood.DeltaLake.Table.DeltaTransaction? HeldTxn;

    // CreatedInTxn counts as "something pending" for the same reason PendingCreate did, but for a
    // DIFFERENT effect: not "there is a create to perform" but "there is a table this transaction owns",
    // which ROLLBACK must reach in order to drop it. Drop it from this list and a CREATE-only
    // transaction's rollback would find no entry and silently leave the table behind.
    public bool HasAny => Rows > 0 || DeletedByOrdinal.Count > 0 || PendingMetadata is not null
                          || CreatedInTxn || AppTxnVersions.Count > 0;

    // ---- READ SET (Spark ConflictChecker parity, for the logical rebase at COMMIT) ----
    // The PUSHED predicate of every in-transaction scan of this table — a superset of the rows the
    // scan actually consumed (DuckDB applies any unpushed residue above the scan, but the source only
    // returned rows matching the pushed part) — or ReadWholeTable when a scan had no pushable filter.
    // Deliberately NOT part of HasAny: a read-only entry must not trip pending-changes guards, and
    // Get() must keep returning null for it (no overlay). Only recorded in EXPLICIT transactions.
    public List<EngineeredWood.Expressions.Predicate> ReadPredicates { get; } = new();
    public bool ReadWholeTable;
    public bool HasReads => ReadWholeTable || ReadPredicates.Count > 0;

    /// <summary>
    /// Anything beyond the pure READ-CACHE state (the pin, the shared open, the row-tracking flag) —
    /// i.e. would this entry have existed BEFORE 4c put the read cache on the same object? The
    /// commit/rollback loops skip footprint-less entries so an autocommit read (which now creates one for
    /// its pin/open) produces neither log lines nor discard work on a statement rollback, exactly as
    /// before. Deliberately generous: it names every field any pre-4c GetOrCreate caller could set
    /// (including the Serializable/CdfEnabled probe caches a failed DML statement may have left alone), so
    /// only entries the read cache alone created can be skipped.
    /// </summary>
    internal bool HasTxnFootprint => HasAny || HasReads || Files.Count > 0 || Batches.Count > 0
                                     || HeldTxn is not null || HeldTable is not null
                                     || PendingIdentityHwm.Count > 0
                                     || Serializable is not null || CdfEnabled is not null;

    // ── the pin (the transaction's repeatable-read contract for this table) ─────────────────────────────

    private long? _pinned;
    private readonly object _pinLock = new();

    /// <summary>
    /// The transaction's snapshot pin for THIS table — ONE store, a plain field since 4c (4b's delegating
    /// property collapsed into its target when the scope dissolved into this object). The setter is
    /// FIRST-WINS, matching the exclusively-<c>??=</c> call sites and <see cref="PinVersion"/>'s
    /// resolve-once — a pin, once taken, is never moved within a transaction. It survives
    /// <see cref="DropOpen"/> on purpose (gated: <c>verify_delta_autocommit_pin</c> §12) and re-keys WITH
    /// the object on a created-table RENAME — structurally now, where 4b needed an explicit RenamePin.
    /// </summary>
    public long? PinnedVersion
    {
        get { lock (_pinLock) { return _pinned; } }
        set
        {
            if (value is { } v)
            {
                lock (_pinLock) { _pinned ??= v; }
            }
        }
    }

    /// <summary>Returns the pinned version, resolving it once via <paramref name="resolve"/> (given the
    /// owning transaction's pinned instant — captured at the transaction's FIRST pin) and keeping it.
    /// A concurrent seeder wins harmlessly: <paramref name="resolve"/> reads the log (idempotent) and the
    /// store is first-wins, the same tolerance the predecessor's GetOrAdd had. Pass the current UTC time —
    /// the caller owns the clock.</summary>
    public long PinVersion(Func<DateTime, long> resolve, DateTime nowUtc)
    {
        if (PinnedVersion is { } already)
        {
            return already;
        }
        var instant = Owner?.InstantFor(nowUtc) ?? nowUtc;
        long resolved = resolve(instant);
        lock (_pinLock)
        {
            _pinned ??= resolved;
            return _pinned.Value;
        }
    }

    // ── the shared open table (per-transaction DeltaTable reuse) ─────────────────────────────────────────

    /// <summary>The table AND the filesystem it was opened over — <c>BuildNativeScanListAsync</c> needs
    /// both, and a shared table cannot hand its own filesystem back.</summary>
    internal readonly record struct OpenTable(DeltaTable Table, EngineeredWood.IO.ITableFileSystem Fs);

    private OpenTable? _open;

    /// <summary>Only a memoized (transaction-owned) binding retains opens: a transient bind dies with its
    /// call, so retaining there would capture a filesystem nothing invalidates. DeltaReader treats a
    /// non-retaining binding exactly like no binding (caller owns + disposes what it opens).</summary>
    internal bool CanRetainOpen => Owner is not null;

    /// <summary>
    /// The table already open for this (transaction, path), or null. ⚠ SHARING RULES (unchanged from the
    /// scope this replaces): READS ONLY — no engineered-wood read path assigns <c>_currentSnapshot</c>
    /// (verified at the current pin; an unenforced upstream invariant, re-check at every bump), and a
    /// writer must open its own table anyway so the commit's conflict range stays empty. A shared table's
    /// filesystem is built with opener 0 (the ambient is the only source — a cached
    /// <c>ClientContext*</c> would be a use-after-free). NEITHER release path DISPOSES a dropped table:
    /// it may be shared with a read still in flight, and engineered-wood's Dispose latches a flag, so
    /// dropping the reference and letting the GC reclaim is both safe and the measured behaviour.
    /// </summary>
    internal OpenTable? TryGetOpen()
    {
        lock (_pinLock) { return _open; }
    }

    /// <summary>
    /// Publishes <paramref name="opened"/> and returns the entry that WON — two threads can both miss and
    /// both open, and the loser must use the winner so "one table per (txn, path)" stays true (discarding
    /// the loser costs nothing: Dispose only sets a flag). <c>Cached</c> is false when the entry was NOT
    /// retained — a transient binding, or the owner's per-transaction open cap
    /// (<see cref="DeltaTransaction.TryRetainOpenSlot"/>, the catalog-enumeration bound) — and the caller
    /// then owns what it opened and must dispose it, as it did before the reuse existed.
    /// </summary>
    internal (OpenTable Entry, bool Cached) PublishOpen(OpenTable opened)
    {
        lock (_pinLock)
        {
            if (_open is { } winner)
            {
                return (winner, true);
            }
            if (Owner is null || !Owner.TryRetainOpenSlot())
            {
                return (opened, false);
            }
            _open = opened;
            return (opened, true);
        }
    }

    /// <summary>Drops the shared open table and KEEPS the pin — the mutating catalog entry points' release
    /// (see <see cref="DeltaTransaction.InvalidateOpens"/>; the asymmetry is load-bearing and gated).</summary>
    internal void DropOpen()
    {
        lock (_pinLock)
        {
            if (_open is not null)
            {
                _open = null;
                Owner?.ReleaseOpenSlot();
            }
        }
    }

    // ── schema/info resolution (the ITable read surface) ────────────────────────────────────────────────

    /// <summary>The table's row-tracking flag, cached from the last schema resolution so the
    /// virtual-columns answer normally costs no extra <c>_delta_log</c> read (the columns fetch always
    /// precedes it in the host's entry materialization). Per-(txn, table) since 4c — the old catalog-wide
    /// <c>_rowTrackingByPath</c> was never invalidated, so a property change made it silently stale.</summary>
    internal bool? RowTracking;

    /// <inheritdoc/>
    /// <remarks>An AT binding answers the AS-OF schema (the time-travel entry's contract); otherwise the
    /// transaction's pending (buffered ALTER / CREATE) shape wins, then storage via the transaction's
    /// shared open. Absence is ESTABLISHED (no commit in <c>_delta_log</c> — the engine's own definition),
    /// never inferred from a failed fetch: an incomplete log, an expired credential or a brief outage
    /// keeps its real error instead of erasing the table from the catalog.</remarks>
    public Schema Schema
    {
        get
        {
            var catalog = Catalog;
            if (_at is { } at)
            {
                return DeltaReader.GetSchemaAt(catalog.Opener(), Path, at.Unit, at.Value, catalog.ReadBound(Path));
            }
            if (PendingArrowSchema is { } pendingSchema)
            {
                return pendingSchema;
            }
            Schema schema;
            bool rowTracking;
            try
            {
                schema = DeltaReader.GetSchemaAndRowTracking(
                    catalog.Opener(), Path, out rowTracking, catalog.ReadBound(Path));
            }
            catch (Exception ex)
            {
                if (DeltaReader.TableExists(catalog.Opener(), Path))
                {
                    throw;
                }
                throw new ObjectNotFoundException("table", Path, ex);
            }
            RowTracking = rowTracking;
            return schema;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Always the virtual <c>_metadata.row_id</c> — a TRANSIENT (file, position) rowid computed at
    /// scan time (no row-tracking feature needed; works on ANY Delta table). Enables UPDATE/DELETE
    /// (rowid-based, mirrors the SQL Server backend); DELETE is copy-on-write (plain add/remove).</remarks>
    public IReadOnlyList<string> RowIdColumns() => new[] { DeltaCatalog.RowIdColumn };

    /// <inheritdoc/>
    /// <remarks>The STABLE row-tracking id + commit version as queryable-by-name virtual columns
    /// (__delta_row_id / __delta_row_commit_version — the Delta materialized-column names; excluded from
    /// SELECT *). native_read + delta.enableRowTracking tables only — the native reader derives them per
    /// file (COALESCE(materialized, baseRowId + file_row_number) / defaultRowCommitVersion). A real user
    /// column with the same name shadows the virtual one at bind (DuckDB's TableBinding prefers real
    /// names).</remarks>
    public IReadOnlyList<VirtualColumn> VirtualColumns()
    {
        var catalog = Catalog;
        bool rowTracking = false;
        if (catalog.NativeRead)
        {
            if (RowTracking is { } known)
            {
                rowTracking = known;
            }
            else
            {
                DeltaReader.GetSchemaAndRowTracking(catalog.Opener(), Path, out rowTracking, catalog.ReadBound(Path));
                RowTracking = rowTracking;
            }
        }
        return catalog.NativeRead && rowTracking
            ? new[]
            {
                new VirtualColumn(DeltaNativeReader.RowTrackingIdColumn, "BIGINT"),
                new VirtualColumn(DeltaNativeReader.RowTrackingVersionColumn, "BIGINT"),
            }
            : System.Array.Empty<VirtualColumn>();
    }

    /// <inheritdoc/>
    /// <remarks>No row-count statistics surfaced (a snapshot COULD sum file stats, but nothing consumes it
    /// yet and enumeration must stay cheap — §3 item 5).</remarks>
    public long? ApproximateRowCount() => null;

    /// <inheritdoc/>
    public IReadOnlyList<NdvEntry> ColumnNdv() => System.Array.Empty<NdvEntry>();

    /// <inheritdoc/>
    public IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues) =>
        Catalog.ScanCore(
            SchemaName ?? throw new InvalidOperationException("delta bound table: scan before Bind named it"),
            TableName!, specJson, filterValues);

    /// <summary>Caller-owned (transient) bindings only — a memoized binding is disposed by its owning
    /// transaction's <see cref="DeltaTransaction.Complete"/>. Releases the held EW machinery and buffered
    /// batches (both empty on every transient binding today); the shared open is deliberately NOT disposed
    /// (see <see cref="TryGetOpen"/>'s rules).</summary>
    public void Dispose()
    {
        DeltaTxnBuffer.DisposeHeld(this);
        DeltaTxnBuffer.DisposeBatches(this);
    }
}
