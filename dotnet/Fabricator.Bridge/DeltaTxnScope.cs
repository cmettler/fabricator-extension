using System;
using System.Collections.Concurrent;
using EngineeredWood.DeltaLake.Table;

namespace Fabricator.Bridge;

/// <summary>
/// THE DELTA PROVIDER'S PER-TRANSACTION SCOPE — one owned object per DuckDB transaction holding the state
/// that used to live in two static, process-global, (txnId, path)-keyed maps: the SNAPSHOT PINS (was
/// <c>SnapshotPinning</c>) and the OPEN-TABLE reuse (was <c>DeltaTableCache</c>). Merged 2026-08-14 as slice
/// 1b of docs/catalog-table-abstraction.md §5 — this class is the seed of that design's
/// <c>ITransaction</c>-owned bound tables, kept behind static accessors until the interface slice so the
/// call sites stay mechanical. What the merge buys today: ONE registry, ONE commit/rollback release instead
/// of two calls that both had to be remembered, and the pin + the open table for a path sitting in one
/// object instead of two maps that only agreed by convention.
///
/// <para><b>THE PINS (per-transaction snapshot pinning).</b> A query that touches several Delta tables
/// (a join) — or re-scans one table — reads a consistent point-in-time cut instead of resolving "latest"
/// independently per scan (which a concurrent writer could make inconsistent). At the transaction's first
/// Delta access one UTC instant is captured; each table's version is resolved as "latest commit ≤ that
/// instant" (via the always-written <c>commitInfo.timestamp</c>) and pinned per (txn, table), so re-scans
/// and later statements in the same transaction see the same version. An explicit <c>AT (...)</c> clause
/// overrides this per table. Keyed by the DuckDB <c>global_transaction_id</c>
/// (<see cref="AmbientTransaction"/>); <c>txnId == 0</c> is NOT pinned — every <c>PinVersion</c> caller
/// guards, and the caller reads latest. Autocommit ⇒ one id per statement ⇒ a single join reads a
/// consistent cut; explicit <c>BEGIN</c> ⇒ one id for the whole transaction ⇒ repeatable read.</para>
///
/// <para><b>THE OPEN TABLES (per-transaction <see cref="DeltaTable"/> reuse).</b> The several READ
/// crossings a single statement makes against one table replay the <c>_delta_log</c> ONCE instead of once
/// each. ⚠ MEASURED, and this is why it exists: a `SELECT three columns … LIMIT 1` on a Fabric lakehouse
/// table at v1850 spent <b>195 s of 291 s in five snapshot builds</b>, four of them redundant.
/// See <c>docs/delta-snapshot-caching.md</c> §0.</para>
///
/// <para>⚠ WHY THE TABLE AND NOT THE IMMUTABLE <c>Snapshot</c> (which would be the tidier thing to hold): a
/// <c>DeltaTable</c> can only be constructed by <c>OpenAsync</c>/<c>CreateAsync</c>/<c>OpenOrCreateAsync</c>
/// — the snapshot-taking constructor is private — so serving a cached snapshot needs an additive
/// engineered-wood <c>FromSnapshot</c> factory, and we deliberately run on ZERO-patch upstream. Holding the
/// table needs nothing upstream. The trade is that a mutable object is shared, which is safe here only
/// because of the two rules below.</para>
///
/// <para><b>RULE 1 — READS ONLY.</b> Every assignment to engineered-wood's <c>_currentSnapshot</c> is in the
/// constructor, <c>RefreshAsync</c>, or a commit/write path; none is on a read path (verified against the
/// pinned engineered-wood, and it is an invariant upstream has never promised — re-check it at a bump). A
/// writer must therefore open its own table. That is independently required anyway: the commit flush opens
/// FRESH so its conflict range is empty, or every append racing a concurrent metadata edit starts failing
/// (<c>verify_delta_catalog_transactions</c> §41).</para>
///
/// <para><b>RULE 2 — A SHARED TABLE'S FILESYSTEM MUST NOT CAPTURE A HOST OPENER.</b> The opener is a
/// <c>ClientContext*</c> valid only for the ABI call that handed it to us.
/// <c>DuckDbTableFileSystem.Opener</c> prefers the <c>AmbientOpener</c> and keeps the constructed value as a
/// fallback, which its own comment documents as safe "because no object outlives its call today … and
/// becomes load-bearing the moment something is cached". A shared table is exactly that, so its filesystem
/// is built with opener 0: the ambient becomes the only source, and its absence fails loudly instead of
/// dereferencing a dangling pointer — a use-after-free neither Windows nor glibc would necessarily fault
/// on.</para>
///
/// <para><b>⚠ THE TWO RELEASES ARE DIFFERENT AND THE DIFFERENCE IS LOAD-BEARING.</b>
/// <see cref="Release"/> (commit/rollback) drops the WHOLE scope — pins and tables. But
/// <see cref="InvalidateTables"/> (called by every MUTATING catalog entry point — CREATE OR REPLACE, DROP,
/// OPTIMIZE, VACUUM, identity create, partition overwrite) drops the tables ONLY: <b>the pins survive
/// mutation on purpose</b>, because the pin IS the transaction's repeatable-read contract — a transaction
/// that ran DML and then re-reads an unrelated table must still read it at the pinned version. Collapsing
/// the two releases into one would silently break repeatable read after any same-transaction DML. (Within
/// the pin's model staleness needs no invalidation at all — every read resolves to the pinned version; the
/// table drop guards the mutations OUTSIDE that model. Coarse on purpose: over-invalidating costs one
/// re-open, under-invalidating is a silently stale read.)
/// <para>⚠ DEFENSIVE, NOT GATED — a mutant collapsing <see cref="InvalidateTables"/> into
/// <see cref="Release"/> SURVIVES the suite (run 2026-08-14, verify_delta_autocommit_pin full 67), and
/// necessarily: the pin is DOUBLE-STORED. <c>PendingAppends.PinnedVersion</c> on the transaction buffer —
/// which <see cref="InvalidateTables"/> never touches — carries the version for every explicit-transaction
/// read and every buffered DML, so wherever a sequential suite could observe a re-pin, the buffer answers
/// first; the exposed shape (a mutating entry point, then a re-read racing a CONCURRENT commit) needs a
/// second writer mid-transaction, which sqllogictest cannot produce. The double storage is itself the
/// finding: unifying the pin into ONE owner is the ITransaction slice's job
/// (docs/catalog-table-abstraction.md §2.3).</para>
///
/// <para>⚠ NEITHER release DISPOSES the dropped tables — a table may be SHARED with a read still in flight,
/// and engineered-wood's <c>Dispose</c> only sets a flag, so dropping the reference and letting the GC
/// reclaim is both safe and the measured behaviour (disposing a shared table dies at the FIRST read of
/// every suite that tried it).</para>
/// </summary>
internal sealed class DeltaTxnScope
{
    /// <summary>Wholesale-clear bound on the registry: one id per autocommit statement, so the map would
    /// otherwise grow with the session. Clearing is correctness-neutral — pins re-resolve, tables re-open.
    /// (Was two independent 4096 bounds, one per map; merged they clear together, which both classes'
    /// comments already declared harmless.)</summary>
    private const int MaxTxns = 4096;

    /// <summary>
    /// Tables held per transaction, past which we stop ADDING (never evict — an entry at the cap is more
    /// likely to be re-read than a new one is).
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
    /// did before the reuse existed.</para>
    /// </remarks>
    private const int MaxTablesPerTxn = 32;

    /// <summary>The table AND the filesystem it was opened over — <c>BuildNativeScanListAsync</c> needs
    /// both, and a shared table cannot hand its own filesystem back.</summary>
    internal readonly record struct OpenTable(DeltaTable Table, EngineeredWood.IO.ITableFileSystem Fs);

    private static readonly ConcurrentDictionary<long, DeltaTxnScope> Txns = new();

    /// <summary>The UTC instant the pins resolve against — captured at the transaction's FIRST
    /// <see cref="PinVersion"/>, exactly as the predecessor did. ⚠ LAZY on purpose: the scope object can be
    /// created earlier by a table publish, and capturing the instant THERE would silently move the
    /// point-in-time the pins mean (and put a clock read on a path that never had one).</summary>
    private DateTime? _instantUtc;
    private readonly object _instantLock = new();

    private readonly ConcurrentDictionary<string, long> _pins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpenTable> _tables = new(StringComparer.Ordinal);

    private static DeltaTxnScope For(long txnId)
    {
        if (Txns.Count > MaxTxns)
        {
            Txns.Clear();
        }
        return Txns.GetOrAdd(txnId, _ => new DeltaTxnScope());
    }

    private DateTime InstantFor(DateTime nowUtc)
    {
        if (_instantUtc is { } already)
        {
            return already;
        }
        lock (_instantLock)
        {
            _instantUtc ??= nowUtc; // first pinner wins, as the predecessor's GetOrAdd did
            return _instantUtc.Value;
        }
    }

    // ── the pins ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the pinned version for <paramref name="tableKey"/> in this transaction, resolving it
    /// once via <paramref name="resolve"/> (given the transaction's pinned instant) and keeping it.
    /// Subsequent scans of the same table in the same transaction return the kept version without
    /// re-resolving; a concurrent seeder wins harmlessly (GetOrAdd never overwrites). Pass the current UTC
    /// time as <paramref name="nowUtc"/> — the caller owns the clock. ⚠ Callers guard <c>txnId != 0</c>
    /// (all four sites do); an unguarded 0 would create a process-shared "no transaction" scope.</summary>
    public static long PinVersion(long txnId, string tableKey, Func<DateTime, long> resolve, DateTime nowUtc)
    {
        var scope = For(txnId);
        var instant = scope.InstantFor(nowUtc);
        return scope._pins.GetOrAdd(tableKey, _ => resolve(instant));
    }

    /// <summary>The already-pinned version for <paramref name="tableKey"/> in this transaction, or null when
    /// no scan pinned it yet (buffered DML uses this so its ordinals match the version the DML's scan
    /// read).</summary>
    public static long? TryGetPinned(long txnId, string tableKey)
        => Txns.TryGetValue(txnId, out var scope) && scope._pins.TryGetValue(tableKey, out var v) ? v : null;

    // ── the open tables ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The table already open for this (transaction, path), or null.</summary>
    public static OpenTable? TryGetTable(long txnId, string path)
        => txnId != 0
           && Txns.TryGetValue(txnId, out var scope)
           && scope._tables.TryGetValue(path, out var t)
            ? t
            : null;

    /// <summary>
    /// Publishes <paramref name="opened"/> for this (transaction, path) and returns the entry that WON — two
    /// threads can both miss and both open, and the loser must use the winner rather than its own copy so
    /// that "one table per (txn, path)" stays true. Discarding the loser costs nothing: engineered-wood's
    /// <c>Dispose</c> only sets a flag.
    /// <para><c>Cached</c> is false when the entry was NOT retained (no transaction to scope it to, or the
    /// per-transaction table cap is reached) — the caller then owns what it opened and must dispose it, as
    /// it did before this reuse existed.</para>
    /// </summary>
    public static (OpenTable Entry, bool Cached) PublishTable(long txnId, string path, OpenTable opened)
    {
        if (txnId == 0)
        {
            return (opened, false); // untracked crossing: no transaction to scope the reuse to
        }
        var byPath = For(txnId)._tables;
        // Checked BEFORE the add and tolerant of a race: two threads may both see Count just under the cap
        // and both add, so the cap is a bound on RETENTION, not an exact quota. Overshooting it by a couple
        // of entries is harmless; the point is that a catalog listing cannot retain hundreds.
        if (byPath.Count >= MaxTablesPerTxn && !byPath.ContainsKey(path))
        {
            return (opened, false);
        }
        return (byPath.GetOrAdd(path, opened), true);
    }

    // ── the releases (⚠ two, and the difference is load-bearing — see the class remarks) ────────────────

    /// <summary>Drops the WHOLE scope — pins and tables. Commit and rollback only.</summary>
    public static void Release(long txnId) => Txns.TryRemove(txnId, out _);

    /// <summary>Drops the transaction's held TABLES and keeps its PINS — for the mutating catalog entry
    /// points, whose immediate commits invalidate any held open table but must NOT cost the transaction its
    /// repeatable-read pins.</summary>
    public static void InvalidateTables(long txnId)
    {
        if (txnId != 0 && Txns.TryGetValue(txnId, out var scope))
        {
            scope._tables.Clear();
        }
    }
}
