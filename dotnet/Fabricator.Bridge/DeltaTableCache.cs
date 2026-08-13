using System;
using System.Collections.Concurrent;
using EngineeredWood.DeltaLake.Table;

namespace Fabricator.Bridge;

/// <summary>
/// Per-DuckDB-transaction reuse of an OPEN <see cref="DeltaTable"/>, so the several READ crossings a single
/// statement makes against one table replay the <c>_delta_log</c> ONCE instead of once each.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ MEASURED, and this is why it exists: a `SELECT three columns … LIMIT 1` on a Fabric lakehouse table at
/// v1850 spent <b>195 s of 291 s in five snapshot builds</b>, four of them redundant — the bind's column
/// fetch, the bind probe's schema open, and the real scan's schema and listing opens all replay the same log
/// at the same version. See <c>docs/delta-snapshot-caching.md</c> §0.
/// </para>
/// <para>
/// ⚠ WHY THE TABLE AND NOT THE IMMUTABLE <c>Snapshot</c> (which would be the tidier thing to cache): a
/// <c>DeltaTable</c> can only be constructed by <c>OpenAsync</c>/<c>CreateAsync</c>/<c>OpenOrCreateAsync</c>
/// — the snapshot-taking constructor is private — so serving a cached snapshot needs an additive
/// engineered-wood <c>FromSnapshot</c> factory, and we deliberately run on ZERO-patch upstream. Caching the
/// table needs nothing upstream. The trade is that a mutable object is shared, which is safe here only
/// because of the two rules below.
/// </para>
/// <para>
/// <b>RULE 1 — READS ONLY.</b> Every assignment to engineered-wood's <c>_currentSnapshot</c> is in the
/// constructor, <c>RefreshAsync</c>, or a commit/write path; none is on a read path (verified against the
/// pinned engineered-wood, and it is an invariant upstream has never promised — re-check it at a bump). A
/// writer must therefore open its own table. That is independently required anyway: the commit flush opens
/// FRESH so its conflict range is empty, or every append racing a concurrent metadata edit starts failing
/// (<c>verify_delta_catalog_transactions</c> §41).
/// </para>
/// <para>
/// <b>RULE 2 — THE CACHED TABLE'S FILESYSTEM MUST NOT CAPTURE A HOST OPENER.</b> The opener is a
/// <c>ClientContext*</c> valid only for the ABI call that handed it to us.
/// <c>DuckDbTableFileSystem.Opener</c> prefers the <c>AmbientOpener</c> and keeps the constructed value as a
/// fallback, which its own comment documents as safe "because no object outlives its call today … and
/// becomes load-bearing the moment something is cached". A cached table is exactly that, so its filesystem is
/// built with opener 0: the ambient becomes the only source, and its absence fails loudly instead of
/// dereferencing a dangling pointer — a use-after-free neither Windows nor glibc would necessarily fault on.
/// </para>
/// <para>
/// Staleness is bounded by the same rule the existing snapshot PIN already imposes: within a transaction
/// every read resolves to the pinned version anyway, so a cached table cannot make a read older than the pin
/// already makes it. What the cache must still respect is a mutation OUTSIDE that model — an immediate
/// commit (CREATE OR REPLACE, DROP, OPTIMIZE, VACUUM, identity create, partition overwrite) — so every
/// mutating catalog entry point drops the whole transaction's cache. Coarse on purpose: over-invalidating
/// costs one re-open, under-invalidating is a silently stale read.
/// </para>
/// </remarks>
internal static class DeltaTableCache
{
    // Same bound and rationale as SnapshotPinning: one id per autocommit statement, so the map would
    // otherwise grow with the session. Clearing wholesale is correctness-neutral — it only re-opens.
    private const int MaxTxns = 4096;

    /// <summary>
    /// Tables cached per transaction, past which we stop ADDING (never evict — an entry at the cap is more
    /// likely to be re-read than a new one is).
    /// </summary>
    /// <remarks>
    /// ⚠ THIS BOUND EXISTS FOR CATALOG ENUMERATION, which is the one shape where the cache is pure cost.
    /// <c>information_schema.tables</c> / <c>duckdb_tables()</c> materialise EVERY table, one
    /// <c>MetadataKind.Columns</c> crossing each, so without a cap one statement would retain a
    /// <c>Snapshot</c> per table — and a snapshot holds <c>ActiveFiles</c>, an <c>AddFile</c> per data file
    /// carrying its <c>Stats</c> JSON (min/max per column), partition values and tags. Enumeration touches
    /// each table exactly ONCE, so all of that retention buys no reuse at all, and it lands on the path
    /// CLAUDE.md already records as the expensive one on OneLake.
    /// <para>A query, by contrast, re-reads a FEW tables several times each (bind column fetch, bind schema
    /// probe, scan schema, scan listing), which is what the cache is for. So the cap is set well above any
    /// realistic join width and far below a catalog listing: past it, extra tables simply behave as they did
    /// before the cache existed.</para>
    /// </remarks>
    private const int MaxTablesPerTxn = 32;

    /// <summary>The table AND the filesystem it was opened over — <c>BuildNativeScanListAsync</c> needs both,
    /// and a cached table cannot hand its own filesystem back.</summary>
    internal readonly record struct OpenTable(DeltaTable Table, EngineeredWood.IO.ITableFileSystem Fs);

    private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, OpenTable>> Txns = new();

    /// <summary>The table already open for this (transaction, path), or null.</summary>
    public static OpenTable? TryGet(long txnId, string path)
        => txnId != 0
           && Txns.TryGetValue(txnId, out var byPath)
           && byPath.TryGetValue(path, out var t)
            ? t
            : null;

    /// <summary>
    /// Publishes <paramref name="opened"/> for this (transaction, path) and returns the entry that WON — two
    /// threads can both miss and both open, and the loser must use the winner rather than its own copy so
    /// that "one table per (txn, path)" stays true. Discarding the loser costs nothing: engineered-wood's
    /// <c>Dispose</c> only sets a flag.
    /// <para><c>Cached</c> is false when the entry was NOT retained (no transaction to scope it to, or the
    /// per-transaction table cap is reached) — the caller then owns what it opened and must dispose it, as it
    /// did before this cache existed.</para>
    /// </summary>
    public static (OpenTable Entry, bool Cached) Publish(long txnId, string path, OpenTable opened)
    {
        if (txnId == 0)
        {
            return (opened, false); // untracked crossing: no transaction to scope the reuse to
        }
        if (Txns.Count > MaxTxns)
        {
            Txns.Clear();
        }
        var byPath = Txns.GetOrAdd(txnId, _ => new ConcurrentDictionary<string, OpenTable>(StringComparer.Ordinal));
        // Checked BEFORE the add and tolerant of a race: two threads may both see Count just under the cap
        // and both add, so the cap is a bound on RETENTION, not an exact quota. Overshooting it by a couple
        // of entries is harmless; the point is that a catalog listing cannot retain hundreds.
        if (byPath.Count >= MaxTablesPerTxn && !byPath.ContainsKey(path))
        {
            return (opened, false);
        }
        return (byPath.GetOrAdd(path, opened), true);
    }

    /// <summary>Drops every table cached for a transaction. Called on commit and rollback beside
    /// <see cref="SnapshotPinning.Release"/>, and by every mutating catalog entry point (see RULE 1).</summary>
    public static void Release(long txnId)
    {
        if (txnId != 0)
        {
            Txns.TryRemove(txnId, out _);
        }
    }
}
