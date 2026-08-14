using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// Per-DuckDB-transaction APPEND buffering for the Delta provider — the explicit-transaction support.
/// Plain appends (INSERT / COPY append) buffer here instead of committing per statement; the catalog's
/// <c>CommitTransaction</c> flushes each table's buffer as ONE atomic Delta commit and
/// <c>RollbackTransaction</c> discards it (uncommitted data files are invisible orphans — vacuum's job,
/// exactly Spark's shape). DuckDB wraps EVERY statement in a transaction, so in autocommit the flush fires
/// at statement end and behavior is identical to per-statement commits; only explicit
/// <c>BEGIN … COMMIT/ROLLBACK</c> changes semantics (atomic multi-INSERT, rollback undoes).
///
/// <para>Since slice 4b (docs/catalog-table-abstraction.md §5) this class is the per-table buffer SHAPE
/// (<see cref="PendingAppends"/>) plus its static stream helpers; the per-transaction BOOKKEEPING it used
/// to carry — the (txnId → tables) outer map and the separate explicit-mark set — lives on
/// <see cref="DeltaTransaction"/>, one object per transaction in the catalog's
/// <see cref="TransactionManager{T}"/>.</para>
///
/// <para>EAGER-WRITE model: in explicit transactions the DATA is always written to storage at statement
/// time (streamed COPY under native_write, per-statement WriteDataFilesAsync otherwise) and the buffer
/// holds ACTIONS + POSITIONS — WrittenDataFile records, deleted positions per pinned-snapshot ordinal
/// (0x780000+idx for this transaction's own pending files — same-txn DELETEs become DVs born on their
/// adds), pending metaData/protocol, CdcFile actions, identity high-water marks, app-txn versions.
/// In-memory RecordBatches survive only in narrow fallbacks (iceberg, identity-under-ALTER, partitioned
/// pending-create, autocommit's park-and-flush-at-statement-end). Reads inside the transaction go
/// through the VIRTUAL-TABLE overlays (DeltaCatalog.ScanCodec / the native reader's pending inputs /
/// ScanPendingCreated) — read-your-writes across appends, deletes, updates, ALTERs and created tables.
/// Atomicity is PER TABLE: Delta has no cross-table transaction, so a multi-table COMMIT writes one
/// Delta commit per table, sequentially.</para>
/// </summary>
internal static class DeltaTxnBuffer
{
    internal sealed class PendingAppends
    {
        /// <summary>The owning transaction and this buffer's table path — what lets
        /// <see cref="PinnedVersion"/> delegate to the ONE pin store. <c>Path</c> is mutable for the
        /// created-table RENAME re-key (<see cref="DeltaTransaction.RenameTable"/>).</summary>
        internal PendingAppends(DeltaTransaction owner, string path)
        {
            Owner = owner;
            Path = path;
        }

        internal DeltaTransaction Owner { get; }
        internal string Path { get; set; }

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

        /// <summary>
        /// The transaction's snapshot pin for THIS table — a delegating view over the transaction scope's
        /// ONE pin store (slice 4b's pin unification). This used to be a FIELD, i.e. a SECOND store that
        /// shadowed the scope's pins on every path a sequential suite reaches — which is exactly why slice
        /// 1b's InvalidateTables-keeps-pins mutant SURVIVED. Every reader and every <c>??=</c> seed now
        /// goes through the scope, so the pin has one owner and the releases' asymmetry is gated
        /// (verify_delta_autocommit_pin §12).
        /// <para>The setter is FIRST-WINS (<see cref="DeltaTxnScope.SetPinIfAbsent"/>), matching both the
        /// old field's exclusively-<c>??=</c> call sites and PinVersion's GetOrAdd — a pin, once taken,
        /// is never moved within a transaction.</para>
        /// </summary>
        public long? PinnedVersion
        {
            get => Owner.Scope.TryGetPinned(Path);
            set
            {
                if (value is { } v)
                {
                    Owner.Scope.SetPinIfAbsent(Path, v);
                }
            }
        }
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
            new(System.StringComparer.Ordinal);

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
        public EngineeredWood.DeltaLake.Table.DeltaTable? HeldTable;
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
    }

    private static readonly Microsoft.Extensions.Logging.ILogger Log =
        FabricatorLog.CreateLogger("Fabricator.Delta");

    /// <summary>
    /// Disposes the buffer entry's held EW transaction and table, <b>transaction first</b> — its cleanup
    /// needs the table's filesystem. Runs on EVERY exit from a (DuckDB txn, table)'s life: commit,
    /// rollback, an exception out of the flush, and the catalog-teardown Drain sweep (which is why it
    /// lives here beside <see cref="DisposeBatches"/> rather than on the catalog). Idempotent — the fields
    /// are nulled before the disposals run.
    ///
    /// <para>Disposing the transaction is what ABORTS it, which is the reclamation the flush used to get
    /// from <c>await using</c>: a flush that does not commit takes back what EW's own writers staged during
    /// it — measured, a buffered DELETE whose commit is refused otherwise leaves a
    /// <c>deletion_vector_*.bin</c>, because <c>StageRowDeletesAsync</c> writes the vector at STAGING time,
    /// before the precondition is judged. After a SUCCESSFUL commit the abort is a no-op (EW #49 empties
    /// the ledger the instant the commit json is durable) — which is also why this is only safe from #49
    /// onward.</para>
    ///
    /// <para>Never throws: it runs from finally blocks that may already be carrying the user's real error;
    /// a cleanup failure must not replace it.</para>
    /// </summary>
    public static void DisposeHeld(PendingAppends pending)
    {
        var txn = pending.HeldTxn;
        var table = pending.HeldTable;
        pending.HeldTxn = null;
        pending.HeldTable = null;
        if (txn is not null)
        {
            try { txn.DisposeAsync().GetAwaiter().GetResult(); }
            catch (System.Exception ex)
            {
                Log.LogWarning("delta held transaction dispose failed ({Reason}) — staged files may remain "
                                + "as orphans for VACUUM", ex.Message);
            }
        }
        if (table is not null)
        {
            try { table.DisposeAsync().GetAwaiter().GetResult(); }
            catch (System.Exception ex)
            {
                Log.LogWarning("delta held table dispose failed ({Reason})", ex.Message);
            }
        }
    }

    /// <summary>Disposes buffered batches (rollback / after flush).</summary>
    public static void DisposeBatches(PendingAppends pending)
    {
        foreach (var b in pending.Batches)
        {
            b.Dispose();
        }
        pending.Batches.Clear();
    }

    /// <summary>
    /// Projects the buffered batches to the scan's advertised schema (columns matched by name; the
    /// trailing virtual rowid, when requested, is SYNTHESIZED from a high ordinal range — pending rows
    /// carry no stable position, and DML against them inside the transaction is rejected anyway, so the
    /// ids only need scan-local uniqueness).
    /// </summary>
    public static async IAsyncEnumerable<RecordBatch> ProjectPending(
        PendingAppends pending, Schema target, string rowIdColumn, long rowIdOrdinalBase,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long position = 0;
        // Snapshot: an INSERT…SELECT-from-self buffers new batches into the SAME list while this scan is
        // (logically) open — enumerate the rows present when the scan started, never a mutating list.
        var snapshot = pending.Batches.ToArray();
        foreach (var batch in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var arrays = new List<IArrowArray>(target.FieldsList.Count);
            foreach (var field in target.FieldsList)
            {
                if (string.Equals(field.Name, rowIdColumn, StringComparison.Ordinal))
                {
                    var b = new Int64Array.Builder();
                    for (int i = 0; i < batch.Length; i++)
                    {
                        b.Append((rowIdOrdinalBase << 40) | position++);
                    }
                    arrays.Add(b.Build());
                    continue;
                }
                int idx = FindColumn(batch.Schema, field.Name);
                if (idx < 0)
                {
                    throw new InvalidOperationException(
                        $"delta transaction read: buffered insert lacks column '{field.Name}'.");
                }
                // Fresh ArrayData wrapper (buffers shared, no copy): the consumer disposes the yielded
                // batch, and the buffered original must survive further scans + the commit-time flush.
                arrays.Add(Apache.Arrow.ArrowArrayFactory.BuildArray(batch.Column(idx).Data.Clone()));
            }
            yield return new RecordBatch(target, arrays, batch.Length);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Excludes this transaction's pending-DELETEd rows from a rowid-carrying scan stream (the codec-path
    /// read-your-writes for buffered DML): <paramref name="source"/> batches end with the virtual
    /// <c>_metadata.row_id</c> column; rows whose decoded (ordinal, position) is in
    /// <paramref name="deletedByOrdinal"/> are dropped, and when <paramref name="dropRowId"/> is set the
    /// trailing rowid column is removed (the scan didn't ask for it — it was forced for the exclusion).
    /// </summary>
    public static async IAsyncEnumerable<RecordBatch> ExcludeDeleted(
        IAsyncEnumerable<RecordBatch> source,
        IReadOnlyDictionary<int, HashSet<long>> deletedByOrdinal,
        bool dropRowId, int rowIdPositionBits,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long posMask = (1L << rowIdPositionBits) - 1;
        await foreach (var batch in source.WithCancellation(ct).ConfigureAwait(false))
        {
            int ridCol = batch.ColumnCount - 1;
            if (batch.Column(ridCol) is not Int64Array rids)
            {
                yield return batch;
                continue;
            }
            var keep = new List<int>(batch.Length);
            for (int i = 0; i < batch.Length; i++)
            {
                long rid = rids.GetValue(i) ?? -1;
                int ordinal = (int)(rid >> rowIdPositionBits);
                if (deletedByOrdinal.TryGetValue(ordinal, out var set) && set.Contains(rid & posMask))
                {
                    continue; // pending-deleted in this transaction
                }
                keep.Add(i);
            }
            int outCols = dropRowId ? ridCol : batch.ColumnCount;
            if (keep.Count == batch.Length && outCols == batch.ColumnCount)
            {
                yield return batch;
                continue;
            }
            // Output shape derived from THIS batch (drop the trailing rowid when it was only forced for
            // the exclusion) — the caller may still reconcile to a pending-ALTER schema afterwards.
            var outFields = new List<Field>(outCols);
            for (int c = 0; c < outCols; c++)
            {
                outFields.Add(batch.Schema.FieldsList[c]);
            }
            var outSchema = new Schema(outFields, batch.Schema.Metadata);
            var columns = new List<IArrowArray>(outCols);
            for (int c = 0; c < outCols; c++)
            {
                // ArrowCompute.Take replaced DeletionVectorFilter.TakeRowsPublic (EW 04eaac4); same signature.
                columns.Add(keep.Count == batch.Length
                    ? batch.Column(c)
                    : EngineeredWood.Arrow.ArrowCompute.Take(batch.Column(c), keep));
            }
            yield return new RecordBatch(outSchema, columns, keep.Count);
        }
    }

    /// <summary>
    /// Projects a TRANSIENT batch stream (a host read of eagerly-written pending files) to the scan's
    /// advertised schema — columns matched by name (ownership moves: the projected batch references the
    /// source arrays directly and the consumer disposes them; unlike <see cref="ProjectPending"/> there
    /// is no parked original to protect). The trailing virtual rowid, when requested, is synthesized
    /// like ProjectPending's (scan-local uniqueness only — DML against pending rows is rejected).
    /// </summary>
    public static async IAsyncEnumerable<RecordBatch> ProjectStream(
        IAsyncEnumerable<RecordBatch> source, Schema target, string rowIdColumn, long rowIdOrdinalBase,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long position = 0;
        await foreach (var batch in source.WithCancellation(ct).ConfigureAwait(false))
        {
            var arrays = new List<IArrowArray>(target.FieldsList.Count);
            foreach (var field in target.FieldsList)
            {
                if (string.Equals(field.Name, rowIdColumn, StringComparison.Ordinal))
                {
                    var b = new Int64Array.Builder();
                    for (int i = 0; i < batch.Length; i++)
                    {
                        b.Append((rowIdOrdinalBase << 40) | position++);
                    }
                    arrays.Add(b.Build());
                    continue;
                }
                int idx = FindColumn(batch.Schema, field.Name);
                if (idx < 0)
                {
                    throw new InvalidOperationException(
                        $"delta transaction read: pending data file lacks column '{field.Name}'.");
                }
                arrays.Add(batch.Column(idx));
            }
            yield return new RecordBatch(target, arrays, batch.Length);
        }
    }

    /// <summary>Concatenates two batch enumerables (base scan ++ pending overlay).</summary>
    public static async IAsyncEnumerable<RecordBatch> Concat(
        IAsyncEnumerable<RecordBatch> first, IAsyncEnumerable<RecordBatch> second,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var b in first.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return b;
        }
        await foreach (var b in second.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return b;
        }
    }

    /// <summary>Adapts an <see cref="IArrowArrayStream"/> to an enumerable (for overlaying onto a stream
    /// whose producer returns the interface, e.g. the native reader). Disposes the stream at the end.</summary>
    public static async IAsyncEnumerable<RecordBatch> AsEnumerable(
        IArrowArrayStream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            while (await stream.ReadNextRecordBatchAsync(ct).ConfigureAwait(false) is { } batch)
            {
                yield return batch;
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static int FindColumn(Schema schema, string name)
    {
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            if (string.Equals(schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}
