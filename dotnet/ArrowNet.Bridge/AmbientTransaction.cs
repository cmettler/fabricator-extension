namespace ArrowNet.Bridge;

/// <summary>
/// The DuckDB transaction id (<c>global_transaction_id</c>) currently in effect on this thread, used by
/// a provider catalog to key its per-transaction connection state (so concurrent DuckDB transactions —
/// e.g. dbt <c>--threads N</c> building several models at once — each get their OWN provider connection
/// rather than colliding on one shared, non-thread-safe connection; see docs/transaction-concurrency.md).
///
/// The host sets it via the <c>set_active_txn</c> ABI entry IMMEDIATELY before each connection-using call,
/// on the SAME thread (the call is synchronous), so a backend can read it without the id having to be
/// threaded through every method signature + internal SQL helper. <c>0</c> means "no specific transaction"
/// (a fresh/pooled connection — autocommit-style). It is <c>[ThreadStatic]</c>, so concurrent ABI calls on
/// different threads carry independent ids.
///
/// The one place the id must cross a thread is the streaming bulk consumer (a background task on a pool
/// thread): there the host captures the id at <c>begin_bulk</c> and the consumer re-establishes it on its
/// own thread before opening the write — so this stays read-only on the bulk consumer side.
/// </summary>
public static class AmbientTransaction
{
    [ThreadStatic] private static long _current;

    /// <summary>The active DuckDB transaction id on this thread (0 = none / autocommit).</summary>
    public static long Current
    {
        get => _current;
        set => _current = value;
    }
}
