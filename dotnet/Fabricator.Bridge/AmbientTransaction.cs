namespace Fabricator.Bridge;

/// <summary>
/// The DuckDB transaction id (<c>global_transaction_id</c>) currently in effect on this thread, used by
/// a provider catalog to key its per-transaction connection state (so concurrent DuckDB transactions —
/// e.g. dbt <c>--threads N</c> building several models at once — each get their OWN provider connection
/// rather than colliding on one shared, non-thread-safe connection; see docs/transaction-concurrency.md).
///
/// The host sets it via the <c>set_active_txn</c> ABI entry IMMEDIATELY before each connection-using call,
/// on the SAME thread (the call is synchronous), so a backend can read it without the id having to be
/// threaded through every method signature + internal SQL helper. <c>0</c> means "no specific transaction"
/// (a fresh/pooled connection — autocommit-style). It is an <see cref="System.Threading.AsyncLocal{T}"/>, so
/// concurrent ABI calls on different threads carry independent ids AND the id flows across <c>await</c> points
/// (enabling a sync-wrapper → <c>async</c>-core refactor without losing it on a pool-thread hop); for the
/// current all-sync code this behaves exactly like the former <c>[ThreadStatic]</c>.
///
/// The one place the id must cross a thread is the streaming bulk consumer (a background task on a pool
/// thread): there the host captures the id at <c>begin_bulk</c> and the consumer re-establishes it on its
/// own thread before opening the write — so this stays read-only on the bulk consumer side.
/// </summary>
public static class AmbientTransaction
{
    private static readonly System.Threading.AsyncLocal<long> _current = new();
    private static readonly System.Threading.AsyncLocal<bool> _joinOnly = new();

    /// <summary>The active DuckDB transaction id on this flow (0 = none / autocommit).</summary>
    public static long Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>
    /// "Join-only" mode for the next write: used by the raw `fabricator_exec` passthrough. When set, a write
    /// JOINS the active transaction's pinned connection **only if one already exists** (a DuckDB-managed write
    /// — INSERT/CTAS/DDL — is already in flight in this transaction), so the exec runs on that same connection
    /// (atomic with the transaction, sees its uncommitted writes). If no pinned connection exists it takes a
    /// fresh autocommit connection and does NOT create persistent transaction state — because a raw exec's
    /// target catalog never triggers DuckDB's transaction lifecycle, so nothing would ever commit a pinned
    /// connection (it would roll back at teardown). Normal DuckDB-managed writes leave this false (they create
    /// + own the per-transaction connection, committed by the transaction manager).
    /// </summary>
    public static bool JoinOnly
    {
        get => _joinOnly.Value;
        set => _joinOnly.Value = value;
    }
}
