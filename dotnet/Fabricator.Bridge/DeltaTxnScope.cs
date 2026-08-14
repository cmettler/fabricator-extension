using System;
using System.Collections.Concurrent;
using EngineeredWood.DeltaLake.Table;

namespace Fabricator.Bridge;

/// <summary>
/// THE DELTA PROVIDER'S PER-TRANSACTION READ SCOPE — the SNAPSHOT PINS and the OPEN-TABLE reuse for one
/// DuckDB transaction, OWNED by its <see cref="DeltaTransaction"/> (slice 4b of
/// docs/catalog-table-abstraction.md §5). Until 2026-08-14 this was a process-global STATIC registry keyed
/// by <c>AmbientTransaction</c> id; it is now an instance a catalog threads into the read entry points
/// (<c>DeltaReader.GetSchema*</c>/<c>ListNativeScanFiles</c>, <c>DeltaNativeReader.Read</c>). What the
/// de-staticking buys: state scoped to the catalog that owns the transaction — a TRANSIENT catalog
/// (ExternalTableRouting) releasing its transaction can no longer touch an ATTACHED catalog's pins for the
/// same DuckDB transaction id, and a path-based global function can no longer leak registry entries nothing
/// releases (it now simply passes no scope and owns what it opens, the pre-cache behaviour).
///
/// <para><b>THE PINS (per-transaction snapshot pinning).</b> A query that touches several Delta tables
/// (a join) — or re-scans one table — reads a consistent point-in-time cut instead of resolving "latest"
/// independently per scan (which a concurrent writer could make inconsistent). At the transaction's first
/// Delta access one UTC instant is captured; each table's version is resolved as "latest commit ≤ that
/// instant" (via the always-written <c>commitInfo.timestamp</c>) and pinned per table, so re-scans and later
/// statements in the same transaction see the same version. An explicit <c>AT (...)</c> clause overrides
/// this per table. Autocommit ⇒ one transaction per statement ⇒ a single join reads a consistent cut;
/// explicit <c>BEGIN</c> ⇒ one transaction ⇒ repeatable read.</para>
///
/// <para><b>⚠ THE PIN HAS ONE OWNER NOW — THIS CLASS.</b> <c>PendingAppends.PinnedVersion</c>, which used to
/// be a SECOND store on the transaction buffer shadowing these pins on every sequential path (slice 1b's
/// surviving mutant), is a delegating property over this store since 4b. Consequence worth stating: the
/// releases' asymmetry below is LOAD-BEARING rather than defensive — dropping the pins from
/// <see cref="InvalidateTables"/> now re-resolves a mid-transaction re-read, and the gate in
/// <c>verify_delta_autocommit_pin</c> §12 catches exactly that.</para>
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
/// <para><b>⚠ THE TWO RELEASES ARE DIFFERENT AND THE DIFFERENCE IS LOAD-BEARING.</b> Dropping the WHOLE
/// scope — pins and tables — happens only when the owning transaction leaves the manager
/// (<c>DeltaCatalog.Commit/RollbackTransaction</c> removing it). But <see cref="InvalidateTables"/> (called
/// by every MUTATING catalog entry point — CREATE OR REPLACE, DROP, OPTIMIZE, VACUUM, identity create,
/// partition overwrite) drops the tables ONLY: <b>the pins survive mutation on purpose</b>, because the pin
/// IS the transaction's repeatable-read contract — a transaction that ran DML and then re-reads an
/// unrelated table must still read it at the pinned version. Collapsing the two releases into one would
/// silently break repeatable read after any same-transaction DML. (Within the pin's model staleness needs
/// no invalidation at all — every read resolves to the pinned version; the table drop guards the mutations
/// OUTSIDE that model. Coarse on purpose: over-invalidating costs one re-open, under-invalidating is a
/// silently stale read.) Since the pin unification this asymmetry is GATED — see the header note.</para>
///
/// <para>⚠ NEITHER release DISPOSES the dropped tables — a table may be SHARED with a read still in flight,
/// and engineered-wood's <c>Dispose</c> only sets a flag, so dropping the reference and letting the GC
/// reclaim is both safe and the measured behaviour (disposing a shared table dies at the FIRST read of
/// every suite that tried it).</para>
/// </summary>
internal sealed class DeltaTxnScope
{
    /// <summary>
    /// Tables held by this transaction, past which we stop ADDING (never evict — an entry at the cap is more
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
    /// <para>(The old static registry also carried a 4096-entry wholesale-clear bound on the TRANSACTION
    /// map. That guarded a world where nothing released; every transaction's scope is now removed with its
    /// owner by the manager, exactly as SQL Server's, so the panic bound is gone with the registry.)</para>
    /// </remarks>
    private const int MaxTablesPerTxn = 32;

    /// <summary>The table AND the filesystem it was opened over — <c>BuildNativeScanListAsync</c> needs
    /// both, and a shared table cannot hand its own filesystem back.</summary>
    internal readonly record struct OpenTable(DeltaTable Table, EngineeredWood.IO.ITableFileSystem Fs);

    /// <summary>The owning transaction's id — DIAGNOSTIC only (the cache-miss log line: two misses for one
    /// table in one statement mean the crossings ran under different transactions, a host-side question).</summary>
    internal long OwnerId { get; }

    internal DeltaTxnScope(long ownerId) => OwnerId = ownerId;

    /// <summary>The UTC instant the pins resolve against — captured at the transaction's FIRST
    /// <see cref="PinVersion"/>. ⚠ LAZY on purpose: the scope exists from the transaction object's creation
    /// (which a table publish or an explicit BEGIN can trigger), and capturing the instant THERE would
    /// silently move the point-in-time the pins mean (and put a clock read on a path that never had
    /// one).</summary>
    private DateTime? _instantUtc;
    private readonly object _instantLock = new();

    private readonly ConcurrentDictionary<string, long> _pins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpenTable> _tables = new(StringComparer.Ordinal);

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
    /// time as <paramref name="nowUtc"/> — the caller owns the clock.</summary>
    public long PinVersion(string tableKey, Func<DateTime, long> resolve, DateTime nowUtc)
    {
        var instant = InstantFor(nowUtc);
        return _pins.GetOrAdd(tableKey, _ => resolve(instant));
    }

    /// <summary>The already-pinned version for <paramref name="tableKey"/>, or null when no scan pinned it
    /// yet (buffered DML uses this so its ordinals match the version the DML's scan read).</summary>
    public long? TryGetPinned(string tableKey)
        => _pins.TryGetValue(tableKey, out var v) ? v : null;

    /// <summary>Pins <paramref name="version"/> for <paramref name="tableKey"/> unless one is already pinned
    /// (first wins, like <see cref="PinVersion"/>). The seed for callers that already HOLD the version (a
    /// DML profile probe, a held table's snapshot) — no resolve, and deliberately no instant capture, since
    /// the version was not derived from the transaction's instant.</summary>
    public void SetPinIfAbsent(string tableKey, long version) => _pins.TryAdd(tableKey, version);

    /// <summary>Re-keys a pin under a new table path — RENAME TABLE of a table CREATED in this transaction
    /// (dbt's tmp-swap). Without this the pin would stay under the old path and the renamed table's flush
    /// would lose its rebase base. First-wins on the new key, like every other pin write.</summary>
    public void RenamePin(string oldKey, string newKey)
    {
        if (_pins.TryRemove(oldKey, out var v))
        {
            _pins.TryAdd(newKey, v);
        }
    }

    // ── the open tables ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The table already open for <paramref name="path"/> in this transaction, or null.</summary>
    public OpenTable? TryGetTable(string path)
        => _tables.TryGetValue(path, out var t) ? t : null;

    /// <summary>
    /// Publishes <paramref name="opened"/> for <paramref name="path"/> and returns the entry that WON — two
    /// threads can both miss and both open, and the loser must use the winner rather than its own copy so
    /// that "one table per (txn, path)" stays true. Discarding the loser costs nothing: engineered-wood's
    /// <c>Dispose</c> only sets a flag.
    /// <para><c>Cached</c> is false when the entry was NOT retained (the per-transaction table cap) — the
    /// caller then owns what it opened and must dispose it, as it did before this reuse existed.</para>
    /// </summary>
    public (OpenTable Entry, bool Cached) PublishTable(string path, OpenTable opened)
    {
        // Checked BEFORE the add and tolerant of a race: two threads may both see Count just under the cap
        // and both add, so the cap is a bound on RETENTION, not an exact quota. Overshooting it by a couple
        // of entries is harmless; the point is that a catalog listing cannot retain hundreds.
        if (_tables.Count >= MaxTablesPerTxn && !_tables.ContainsKey(path))
        {
            return (opened, false);
        }
        return (_tables.GetOrAdd(path, opened), true);
    }

    /// <summary>Drops the transaction's held TABLES and keeps its PINS — for the mutating catalog entry
    /// points, whose immediate commits invalidate any held open table but must NOT cost the transaction its
    /// repeatable-read pins (see the class remarks — this asymmetry is gated since the pin
    /// unification).</summary>
    public void InvalidateTables() => _tables.Clear();
}
