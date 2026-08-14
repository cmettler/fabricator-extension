using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Fabricator.Bridge;

/// <summary>
/// The per-catalog registry of live <see cref="ITransaction"/>s — the C# twin of the C++
/// <c>FabricatorTransactionManager</c> (slice 4a, docs/catalog-table-abstraction.md §2.3). One typed map
/// replacing the ad-hoc per-provider dictionaries (<c>_txns</c> + <c>_explicitTxns</c> on SQL Server; the
/// Delta txn buffer's outer map in a later slice). The ABI transport is unchanged: <c>set_active_txn</c>
/// still carries the id, and providers resolve it here.
/// </summary>
/// <remarks>
/// Deliberately NOT an owner of lifecycle policy: <see cref="GetOrCreate"/> is called lazily by the first
/// state-needing touch (so read-only autocommit statements allocate nothing), and completion is the
/// CALLER's job on the instance <see cref="Remove"/> returned — never on a still-registered transaction.
/// Callers guard <c>id != 0</c> themselves, exactly as the dictionaries' call sites did.
/// </remarks>
public sealed class TransactionManager<T> where T : class, ITransaction
{
    private readonly ConcurrentDictionary<long, T> _byId = new();
    private readonly Func<long, T> _factory;

    public TransactionManager(Func<long, T> factory) => _factory = factory;

    /// <summary>The live transaction for <paramref name="id"/>, created via the factory if absent.</summary>
    public T GetOrCreate(long id) => _byId.GetOrAdd(id, _factory);

    /// <summary>The live transaction for <paramref name="id"/>, or null (no state-needing touch yet).</summary>
    public T? TryGet(long id) => _byId.TryGetValue(id, out var txn) ? txn : null;

    /// <summary>
    /// Unregisters and returns the transaction for <paramref name="id"/>, or null if none exists (a
    /// read-only transaction that never created state, or one already finished). The caller completes the
    /// returned instance — removal-before-completion is the ordering contract.
    /// </summary>
    public T? Remove(long id) => _byId.TryRemove(id, out var txn) ? txn : null;

    /// <summary>
    /// Unregisters and returns EVERY live transaction (catalog teardown — e.g. a DETACH mid-transaction
    /// rolls back whatever is still open). Tolerates concurrent <see cref="Remove"/>s.
    /// </summary>
    public List<T> Drain()
    {
        var drained = new List<T>();
        foreach (var kvp in _byId)
        {
            if (_byId.TryRemove(kvp.Key, out var txn))
            {
                drained.Add(txn);
            }
        }
        return drained;
    }
}
