using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Fabricator.Bridge;

/// <summary>
/// The Delta provider's per-DuckDB-transaction object (slices 4b/4c of docs/catalog-table-abstraction.md
/// §5): ONE owner for what used to live in three id-keyed stores — <c>DeltaTxnBuffer</c>'s outer
/// (txnId → tables) map, its separate <c>_explicit</c> mark set, and the process-global STATIC
/// <c>DeltaTxnScope</c> registry (pins + open tables). Since 4c the per-table value is a
/// <see cref="DeltaBoundTable"/> — the buffered actions, the snapshot pin AND the shared open in ONE object
/// per (transaction, table), which is §2.3's "the bound table is memoized on the transaction" made literal:
/// <see cref="GetOrCreate"/> IS <see cref="ITableDefinition.Bind"/>'s memoization. Held per catalog in a
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
    public DeltaTransaction(long id, DeltaCatalog catalog)
    {
        Id = id;
        Catalog = catalog;
    }

    public long Id { get; }

    /// <summary>The owning catalog — how a memoized <see cref="DeltaBoundTable"/> reaches the opener, the
    /// scan core and the engine flags (a bound table is (definition × transaction), and both halves are
    /// per-catalog).</summary>
    internal DeltaCatalog Catalog { get; }

    /// <summary>Marked by an explicit user <c>BEGIN</c> (the host's BeginTransaction(isExplicit)). DML
    /// buffers only in explicit transactions; autocommit keeps the direct per-statement paths.</summary>
    public bool IsExplicit { get; set; }

    private readonly ConcurrentDictionary<string, DeltaBoundTable> _tables = new(StringComparer.Ordinal);

    /// <summary>The per-table bound objects, for the commit/rollback loops. Live view — enumerate after the
    /// transaction left the manager (the flush does), when nothing can mutate it. ⚠ Since 4c this also holds
    /// READ-ONLY bindings (pin/open-cache only) — the loops skip those via
    /// <see cref="DeltaBoundTable.HasTxnFootprint"/>.</summary>
    public ICollection<KeyValuePair<string, DeltaBoundTable>> Tables => _tables;

    /// <summary>The transaction's bound table for <paramref name="tablePath"/> — the memoization behind
    /// <see cref="DeltaTableDefinition.Bind"/> and every buffered-write/read-cache touch.</summary>
    public DeltaBoundTable GetOrCreate(string tablePath)
        => _tables.GetOrAdd(tablePath, p => new DeltaBoundTable(this, p));

    /// <summary>The bound table when it exists, WITHOUT creating one and WITHOUT the
    /// <see cref="Get"/> pending-work filter — the read paths peek at pins this way.</summary>
    public DeltaBoundTable? Peek(string tablePath)
        => _tables.TryGetValue(tablePath, out var p) ? p : null;

    /// <summary>The table's buffer when it has PENDING WORK, else null — a read-only entry (read set /
    /// pins / open cache) is deliberately invisible here, so pending-changes guards don't trip on reads.</summary>
    public DeltaBoundTable? Get(string tablePath)
        => _tables.TryGetValue(tablePath, out var p) && p.HasAny ? p : null;

    public bool HasPending(string tablePath) => Get(tablePath) is not null;

    /// <summary>Re-keys ONE table's binding under a new path (RENAME TABLE of a table CREATED in the same
    /// transaction — dbt's tmp-swap shape; the caller moves the storage folder). The snapshot PIN — the
    /// flush's rebase base — re-keys STRUCTURALLY now: it is a field on the object being moved, where 4b's
    /// separate pin store needed an explicit RenamePin beside the map move. False when the old path has no
    /// binding or the new path already holds one with PENDING WORK.</summary>
    /// <remarks>⚠ A footprint-less binding at the NEW path is EVICTED, not a conflict — since 4c the map
    /// also holds read-cache entries, and the dbt tmp-swap reads the target name earlier in the same
    /// transaction (its entry materialization binds it), so `RENAME m__dbt_tmp TO m` finds `m`'s read
    /// cache sitting on the key. That entry's pin and open describe the table just renamed AWAY from the
    /// path; serving either to the table arriving AT the path would be a stale read, so eviction is the
    /// correct direction, and the renamed table keeps ITS OWN pin as the flush's rebase base.</remarks>
    public bool RenameTable(string oldPath, string newPath)
    {
        if (!_tables.TryRemove(oldPath, out var p))
        {
            return false;
        }
        if (!_tables.TryAdd(newPath, p))
        {
            if (Peek(newPath) is not { HasTxnFootprint: false } stale
                || !_tables.TryRemove(newPath, out var evicted))
            {
                _tables.TryAdd(oldPath, p); // put it back — a genuine conflict must not lose the buffer
                return false;
            }
            evicted.DropOpen();
            if (!_tables.TryAdd(newPath, p))
            {
                _tables.TryAdd(oldPath, p);
                return false;
            }
        }
        p.Path = newPath;
        // The moved binding's own shared OPEN (if any) was opened over the OLD path's filesystem, which the
        // caller is renaming away on storage — carrying it to the new key would serve reads a DeltaTable
        // whose folder no longer exists ("No Delta table found"). 4b never re-keyed the open map, so the
        // open was silently orphaned there; dropping it is the same outcome made explicit. The PIN stays:
        // it is a version number resolved against the log that now lives AT the new path.
        p.DropOpen();
        return true;
    }

    /// <summary>Discards ONE table's binding (a CREATE + DROP inside the same transaction cancels out).</summary>
    public void RemoveTable(string tablePath)
    {
        if (_tables.TryRemove(tablePath, out var p))
        {
            DeltaTxnBuffer.DisposeBatches(p);
            p.DropOpen();
        }
    }

    // ── the pinned instant (moved from DeltaTxnScope, 4c) ───────────────────────────────────────────────

    /// <summary>The UTC instant the pins resolve against — captured at the transaction's FIRST
    /// <see cref="DeltaBoundTable.PinVersion"/>. ⚠ LAZY on purpose: the transaction object exists from an
    /// explicit BEGIN or a table publish, and capturing the instant THERE would silently move the
    /// point-in-time the pins mean (and put a clock read on a path that never had one).</summary>
    private DateTime? _instantUtc;
    private readonly object _instantLock = new();

    internal DateTime InstantFor(DateTime nowUtc)
    {
        if (_instantUtc is { } already)
        {
            return already;
        }
        lock (_instantLock)
        {
            _instantUtc ??= nowUtc; // first pinner wins, as the predecessors' GetOrAdd did
            return _instantUtc.Value;
        }
    }

    // ── the shared-open retention cap (moved from DeltaTxnScope, 4c) ────────────────────────────────────

    /// <summary>
    /// Shared opens retained by this transaction's bindings, past which we stop RETAINING (never evict —
    /// an open at the cap is more likely to be re-read than a new one is).
    /// </summary>
    /// <remarks>
    /// ⚠ THIS BOUND EXISTS FOR CATALOG ENUMERATION, which is the one shape where the reuse is pure cost.
    /// <c>information_schema.tables</c> / <c>duckdb_tables()</c> materialise EVERY table, one
    /// <c>MetadataKind.Columns</c> crossing each, so without a cap one statement would retain a
    /// <c>Snapshot</c> per table — and a snapshot holds <c>ActiveFiles</c>, an <c>AddFile</c> per data file
    /// carrying its <c>Stats</c> JSON (min/max per column), partition values and tags. Enumeration touches
    /// each table exactly ONCE, so all of that retention buys no reuse at all, and it lands on the path
    /// CLAUDE.md already records as the expensive one on OneLake.
    /// <para>A query, by contrast, re-reads a FEW tables several times each (bind column fetch, bind schema
    /// probe, scan schema, scan listing), which is what the reuse is for. So the cap is set well above any
    /// realistic join width and far below a catalog listing: past it, extra tables simply behave as they
    /// did before the reuse existed. Race-tolerant over-reservation is fine — it is a bound on RETENTION,
    /// not an exact quota.</para>
    /// </remarks>
    private const int MaxOpenTablesPerTxn = 32;

    private int _retainedOpens;

    internal bool TryRetainOpenSlot()
    {
        if (Interlocked.Increment(ref _retainedOpens) <= MaxOpenTablesPerTxn)
        {
            return true;
        }
        Interlocked.Decrement(ref _retainedOpens);
        return false;
    }

    internal void ReleaseOpenSlot() => Interlocked.Decrement(ref _retainedOpens);

    /// <summary>Drops the transaction's shared OPEN tables and keeps its PINS — for the mutating catalog
    /// entry points, whose immediate commits invalidate any held open table but must NOT cost the
    /// transaction its repeatable-read pins (the pin is the contract; the asymmetry is gated by
    /// <c>verify_delta_autocommit_pin</c> §12). Coarse on purpose: over-invalidating costs one re-open,
    /// under-invalidating is a silently stale read.</summary>
    public void InvalidateOpens()
    {
        foreach (var kv in _tables)
        {
            kv.Value.DropOpen();
        }
    }

    /// <summary>
    /// Disposal backstop: releases every binding's held EW transaction/table (transaction first — its abort
    /// is the reclamation of what EW's own writers staged) and buffered batches. Idempotent — the
    /// commit/rollback loops dispose per entry as they go, so for them this finds nothing left. The
    /// catalog-teardown <c>Drain</c> sweep and the rollback of a transaction the flush never reached are
    /// the callers that find work. Shared OPENS are dropped by reference only, never disposed (they may be
    /// mid-read; engineered-wood's Dispose latches a flag — the GC reclaims). <paramref name="commit"/> is
    /// unused: the COMMIT business (the per-table flush) lives in <c>DeltaCatalog.CommitTransaction</c>,
    /// which needs the opener and the engine flags — by the time this runs, both directions only clean up.
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
