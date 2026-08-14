using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Fabricator.Bridge;

/// <summary>
/// The Delta provider's per-DuckDB-transaction object (slice 4b of docs/catalog-table-abstraction.md §5):
/// ONE owner for what used to live in three id-keyed stores — <c>DeltaTxnBuffer</c>'s outer
/// (txnId → tables) map, its separate <c>_explicit</c> mark set, and the process-global STATIC
/// <c>DeltaTxnScope</c> registry (pins + open tables). Held per catalog in a
/// <see cref="TransactionManager{T}"/>, so a TRANSIENT catalog (ExternalTableRouting) committing under the
/// user's ambient transaction id structurally CANNOT touch an attached catalog's state for the same id —
/// the cross-catalog hazard the static registry carried.
///
/// <para><b>Lifecycle.</b> Created lazily: an explicit <c>BEGIN</c> (the host's BeginTransaction), the
/// first buffered write, or the first read-path crossing that pins/publishes. Removed — pins, open tables
/// and buffers together, atomically — by <c>DeltaCatalog.Commit/RollbackTransaction</c>, which then run the
/// flush/discard against the REMOVED object (nothing can resolve a mid-completion transaction — the
/// manager's ordering contract). <see cref="Complete"/> is the disposal backstop only: the business half of
/// commit (the per-table flush) needs the catalog (opener, write spec, engine flags) and stays there.</para>
/// </summary>
internal sealed class DeltaTransaction : ITransaction
{
    public DeltaTransaction(long id)
    {
        Id = id;
        Scope = new DeltaTxnScope(id);
    }

    public long Id { get; }

    /// <summary>Marked by an explicit user <c>BEGIN</c> (the host's BeginTransaction(isExplicit)). DML
    /// buffers only in explicit transactions; autocommit keeps the direct per-statement paths.</summary>
    public bool IsExplicit { get; set; }

    /// <summary>The transaction's READ scope — snapshot pins + open-table reuse. Threaded into the static
    /// <c>DeltaReader</c>/<c>DeltaNativeReader</c> read entry points by the catalog (they cannot reach the
    /// per-catalog manager themselves — the reason the old scope was a static registry).</summary>
    public DeltaTxnScope Scope { get; }

    private readonly ConcurrentDictionary<string, DeltaTxnBuffer.PendingAppends> _tables =
        new(StringComparer.Ordinal);

    /// <summary>The per-table buffers, for the commit/rollback loops. Live view — enumerate after the
    /// transaction left the manager (the flush does), when nothing can mutate it.</summary>
    public ICollection<KeyValuePair<string, DeltaTxnBuffer.PendingAppends>> Tables => _tables;

    public DeltaTxnBuffer.PendingAppends GetOrCreate(string tablePath)
        => _tables.GetOrAdd(tablePath, p => new DeltaTxnBuffer.PendingAppends(this, p));

    /// <summary>The table's buffer when it has PENDING WORK, else null — a read-only entry (read set /
    /// pins only) is deliberately invisible here, so pending-changes guards don't trip on reads.</summary>
    public DeltaTxnBuffer.PendingAppends? Get(string tablePath)
        => _tables.TryGetValue(tablePath, out var p) && p.HasAny ? p : null;

    public bool HasPending(string tablePath) => Get(tablePath) is not null;

    /// <summary>Re-keys ONE table's buffer under a new path (RENAME TABLE of a table CREATED in the same
    /// transaction — dbt's tmp-swap shape; the caller moves the storage folder). The entry's own
    /// <c>Path</c> and its snapshot PIN re-key with it — the pin is the flush's rebase base, and leaving it
    /// under the old path would silently lose it (the delegating <c>PinnedVersion</c> reads by path).
    /// False when the old path has no buffer or the new path already has one.</summary>
    public bool RenameTable(string oldPath, string newPath)
    {
        if (!_tables.TryRemove(oldPath, out var p) || !_tables.TryAdd(newPath, p))
        {
            return false;
        }
        p.Path = newPath;
        Scope.RenamePin(oldPath, newPath);
        return true;
    }

    /// <summary>Discards ONE table's buffer (a CREATE + DROP inside the same transaction cancels out).</summary>
    public void RemoveTable(string tablePath)
    {
        if (_tables.TryRemove(tablePath, out var p))
        {
            DeltaTxnBuffer.DisposeBatches(p);
        }
    }

    /// <summary>
    /// Disposal backstop: releases every entry's held EW transaction/table (transaction first — its abort
    /// is the reclamation of what EW's own writers staged) and buffered batches. Idempotent — the
    /// commit/rollback loops dispose per entry as they go, so for them this finds nothing left. The
    /// catalog-teardown <c>Drain</c> sweep and the rollback of a transaction the flush never reached are
    /// the callers that find work. <paramref name="commit"/> is unused: the COMMIT business (the per-table
    /// flush) lives in <c>DeltaCatalog.CommitTransaction</c>, which needs the opener and the engine flags —
    /// by the time this runs, both directions only clean up.
    /// </summary>
    public void Complete(bool commit)
    {
        foreach (var kv in _tables)
        {
            DeltaTxnBuffer.DisposeHeld(kv.Value);
            DeltaTxnBuffer.DisposeBatches(kv.Value);
        }
        _tables.Clear();
    }

    public void Dispose() => Complete(commit: false);
}
