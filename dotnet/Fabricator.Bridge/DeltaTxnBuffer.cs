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
/// Plain appends (INSERT / COPY append) buffer instead of committing per statement; the catalog's
/// <c>CommitTransaction</c> flushes each table's buffer as ONE atomic Delta commit and
/// <c>RollbackTransaction</c> discards it (uncommitted data files are invisible orphans — vacuum's job,
/// exactly Spark's shape). DuckDB wraps EVERY statement in a transaction, so in autocommit the flush fires
/// at statement end and behavior is identical to per-statement commits; only explicit
/// <c>BEGIN … COMMIT/ROLLBACK</c> changes semantics (atomic multi-INSERT, rollback undoes).
///
/// <para>Since slice 4c (docs/catalog-table-abstraction.md §5) this class is only the STATIC STREAM/DISPOSAL
/// HELPERS: the per-table buffer shape it used to declare (<c>PendingAppends</c>) is
/// <see cref="DeltaTableBinding"/> — the table-bound-to-a-transaction object, which also absorbed the snapshot
/// pin and the open-table reuse — and the per-transaction bookkeeping lives on
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
    public static void DisposeHeld(DeltaTableBinding pending)
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
    public static void DisposeBatches(DeltaTableBinding pending)
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
        DeltaTableBinding pending, Schema target, string rowIdColumn, long rowIdOrdinalBase,
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
