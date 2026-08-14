namespace Fabricator.Bridge;

/// <summary>
/// One provider-side transaction, keyed by the DuckDB transaction id (<c>MetaTransaction.global_transaction_id</c>,
/// carried per-thread via <c>AmbientTransaction</c>/<c>set_active_txn</c>). Slice 4a of the catalog/table
/// abstraction (docs/catalog-table-abstraction.md §2.3/§5): the object that used to be "a dictionary value
/// keyed by long" — SQL Server's pinned connection state, and (in a later slice) the Delta txn buffer entry.
/// Owned by a per-catalog <see cref="TransactionManager{T}"/>; a bound <c>ITable</c> (a later slice) will be
/// owned by its transaction, so release becomes disposal rather than a cache sweep.
/// </summary>
/// <remarks>
/// Lifetime contract, preserved from the dictionaries this replaces: creation is LAZY (an autocommit
/// statement that only reads allocates nothing — the manager's <c>GetOrCreate</c> runs on the first
/// state-needing touch, not on <c>begin_transaction</c>), and <see cref="Complete"/> is called exactly once
/// by whoever removes the transaction from its manager. <see cref="System.IDisposable.Dispose"/> must be
/// safe after <see cref="Complete"/> (idempotent release).
/// </remarks>
public interface ITransaction : System.IDisposable
{
    /// <summary>The DuckDB transaction id this state belongs to. Never 0 (0 = no transaction).</summary>
    long Id { get; }

    /// <summary>
    /// True for a user <c>BEGIN … COMMIT</c>, false for the implicit per-statement autocommit wrapper —
    /// the v60 <c>is_explicit</c> flag, now a field ON the transaction instead of membership in a second
    /// id-keyed dictionary. A provider buffering transactional DML may only change statement-visible
    /// semantics for explicit transactions.
    /// </summary>
    bool IsExplicit { get; }

    /// <summary>
    /// Commit (<c>true</c>) or roll back (<c>false</c>) whatever this transaction holds, releasing its
    /// resources. Called once, by the code that removed this transaction from its manager — never while it
    /// is still registered (the ordering that makes read-state-after-remove bugs structural instead of
    /// disciplinary).
    /// </summary>
    void Complete(bool commit);
}
