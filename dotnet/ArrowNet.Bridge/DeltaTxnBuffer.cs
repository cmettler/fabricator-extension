using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using EngineeredWood.DeltaLake.Table;

namespace ArrowNet.Bridge;

/// <summary>
/// Per-DuckDB-transaction APPEND buffering for the Delta provider — the explicit-transaction support.
/// Plain appends (INSERT / COPY append) buffer here instead of committing per statement; the catalog's
/// <c>CommitTransaction</c> flushes each table's buffer as ONE atomic Delta commit and
/// <c>RollbackTransaction</c> discards it (uncommitted data files are invisible orphans — vacuum's job,
/// exactly Spark's shape). DuckDB wraps EVERY statement in a transaction, so in autocommit the flush fires
/// at statement end and behavior is identical to per-statement commits; only explicit
/// <c>BEGIN … COMMIT/ROLLBACK</c> changes semantics (atomic multi-INSERT, rollback undoes).
///
/// <para>Two buffer shapes, decided by the catalog's write path: the STREAMING native write parks the
/// already-written files' <see cref="WrittenDataFile"/> records (bounded memory — bytes are on storage);
/// the codec/collect write parks the materialized <see cref="RecordBatch"/>es (RAM-bounded, like the
/// collect path itself). Reads inside the transaction overlay the pending data (read-your-writes);
/// non-append operations on a table with pending appends are rejected (append-only transactions —
/// buffered DML/DDL are later slices). Atomicity is PER TABLE: Delta has no cross-table transaction, so a
/// multi-table COMMIT writes one Delta commit per table, sequentially.</para>
/// </summary>
internal sealed class DeltaTxnBuffer
{
    internal sealed class PendingAppends
    {
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
        public long? PinnedVersion;
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

        // ---- Buffered CREATE TABLE / CTAS (slice 4, explicit transactions only) ----
        // The table exists ONLY in this buffer until COMMIT: nothing touches the _delta_log before the
        // flush (DuckDB's rollback callback has no opener, so rollback can only DISCARD — a written
        // commit-0 could never be cleaned up). PendingArrowSchema doubles as the table's schema; the flush
        // creates the table + writes all buffered rows (today's CTAS commit shape: v0 CREATE + one WRITE).
        public bool PendingCreate;
        public IReadOnlyList<string>? CreatePartitionColumns;

        public bool HasAny => Rows > 0 || DeletedByOrdinal.Count > 0 || PendingMetadata is not null
                              || PendingCreate;
    }

    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, PendingAppends>> _byTxn = new();
    // Explicit (user BEGIN..COMMIT) transaction ids, marked by BeginTransaction(v60). DML buffers only in
    // explicit transactions; autocommit keeps the direct per-statement paths.
    private readonly ConcurrentDictionary<long, byte> _explicit = new();

    public void MarkExplicit(long txnId)
    {
        if (txnId != 0)
        {
            _explicit.TryAdd(txnId, 0);
        }
    }

    public bool IsExplicit(long txnId) => txnId != 0 && _explicit.ContainsKey(txnId);

    public PendingAppends GetOrCreate(long txnId, string tablePath)
    {
        var tables = _byTxn.GetOrAdd(txnId, _ => new ConcurrentDictionary<string, PendingAppends>(StringComparer.Ordinal));
        return tables.GetOrAdd(tablePath, _ => new PendingAppends());
    }

    public PendingAppends? Get(long txnId, string tablePath)
        => _byTxn.TryGetValue(txnId, out var tables) && tables.TryGetValue(tablePath, out var p) && p.HasAny
            ? p
            : null;

    public bool HasPending(long txnId, string tablePath) => Get(txnId, tablePath) is not null;

    /// <summary>Discards ONE table's buffer (a CREATE + DROP inside the same transaction cancels out —
    /// nothing ever touched storage).</summary>
    public void RemoveTable(long txnId, string tablePath)
    {
        if (_byTxn.TryGetValue(txnId, out var tables) && tables.TryRemove(tablePath, out var p))
        {
            DisposeBatches(p);
        }
    }

    /// <summary>Removes and returns the transaction's whole buffer set (null when none); also clears the
    /// explicit-transaction mark.</summary>
    public ConcurrentDictionary<string, PendingAppends>? Remove(long txnId)
    {
        _explicit.TryRemove(txnId, out _);
        return _byTxn.TryRemove(txnId, out var tables) ? tables : null;
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
                columns.Add(keep.Count == batch.Length
                    ? batch.Column(c)
                    : EngineeredWood.DeltaLake.DeletionVectors.DeletionVectorFilter.TakeRowsPublic(
                        batch.Column(c), keep));
            }
            yield return new RecordBatch(outSchema, columns, keep.Count);
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
