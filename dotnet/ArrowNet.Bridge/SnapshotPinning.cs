using System;
using System.Collections.Concurrent;

namespace ArrowNet.Bridge;

/// <summary>
/// Per-DuckDB-transaction snapshot pinning for the Delta providers, so a query that touches several Delta
/// tables (a join) — or re-scans one table — reads a <b>consistent point-in-time cut</b> instead of resolving
/// "latest" independently per scan (which a concurrent writer could make inconsistent). At the transaction's
/// first Delta access we capture one UTC instant; each table's version is then resolved as "latest commit ≤
/// that instant" (via the always-written <c>commitInfo.timestamp</c>) and <b>pinned per (txn, table)</b> so
/// re-scans and later statements in the same transaction see the same version. An explicit
/// <c>AT (...)</c> clause overrides this per table.
///
/// <para>Keyed by the DuckDB <c>global_transaction_id</c> (<see cref="AmbientTransaction"/>). <c>txnId == 0</c>
/// (no specific transaction) is NOT pinned — the caller reads latest. Autocommit ⇒ one id per statement ⇒ a
/// single join reads a consistent cut; explicit <c>BEGIN</c> ⇒ one id for the whole transaction ⇒ repeatable
/// read. The cache is bounded (cleared wholesale past a cap — correctness-neutral, only re-resolves).</para>
/// </summary>
public static class SnapshotPinning
{
    private const int MaxTxns = 4096;

    private sealed class TxnSnapshot
    {
        public DateTime InstantUtc;
        public readonly ConcurrentDictionary<string, long> Versions = new(StringComparer.Ordinal);
    }

    private static readonly ConcurrentDictionary<long, TxnSnapshot> Txns = new();

    /// <summary>The UTC instant pinned for this transaction (captured once, at first access). Pass the current
    /// UTC time as <paramref name="nowUtc"/> (the caller owns the clock — the workflow sandbox forbids
    /// <c>DateTime.UtcNow</c> in scripts, but this runs in the real Bridge runtime).</summary>
    public static DateTime InstantFor(long txnId, DateTime nowUtc)
    {
        if (Txns.Count > MaxTxns)
        {
            Txns.Clear();
        }
        return Txns.GetOrAdd(txnId, _ => new TxnSnapshot { InstantUtc = nowUtc }).InstantUtc;
    }

    /// <summary>Returns the pinned version for <paramref name="tableKey"/> in this transaction, resolving it once
    /// via <paramref name="resolve"/> (given the transaction's pinned instant) and caching it. Subsequent scans
    /// of the same table in the same transaction return the cached version without re-resolving.</summary>
    public static long PinVersion(long txnId, string tableKey, Func<DateTime, long> resolve, DateTime nowUtc)
    {
        var instant = InstantFor(txnId, nowUtc);
        var snap = Txns.GetOrAdd(txnId, _ => new TxnSnapshot { InstantUtc = instant });
        return snap.Versions.GetOrAdd(tableKey, _ => resolve(snap.InstantUtc));
    }

    /// <summary>Drops all pinned state for a transaction (call on commit/rollback if a hook is available; the
    /// cap makes this optional — memory is bounded regardless).</summary>
    public static void Release(long txnId) => Txns.TryRemove(txnId, out _);
}
