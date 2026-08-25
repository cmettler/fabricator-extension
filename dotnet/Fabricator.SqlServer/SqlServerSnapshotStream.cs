using System;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// A stream over a connection that was put into SNAPSHOT isolation, which restores the isolation level and
/// releases the connection when the read ends.
/// </summary>
/// <remarks>
/// <para><b>⚠⚠ IT EXISTS BECAUSE SNAPSHOT ISOLATION LEAKS THROUGH THE CONNECTION POOL — MEASURED 2026-08-25,
/// and the code comment it corrects said the opposite.</b> A connection is pooled by connection string, so a
/// connection put into SNAPSHOT and then disposed comes back out of the pool STILL AT SNAPSHOT. Measured with
/// <c>Max Pool Size=1</c>, so the next open is provably the same physical connection, reading
/// <c>sys.dm_exec_sessions.transaction_isolation_level</c> at each step:</para>
/// <code>
/// before                                 ReadCommitted   &lt;- the pool starts clean, which is what makes the rest mean something
/// inside the snapshot transaction        Snapshot
/// same connection after COMMIT           Snapshot        &lt;- session-scoped: the level outlives the transaction
/// next open from the pool                Snapshot        &lt;- THE LEAK
/// next open after an explicit restore    ReadCommitted   &lt;- the fix
/// bare SET, next open from the pool      Snapshot        &lt;- BOTH spellings leak, and this is the shipped one
/// </code>
/// <para><b>⚠ BOTH SPELLINGS, and that is what makes this more than a CDC concern.</b>
/// <c>BeginTransaction(IsolationLevel.Snapshot)</c> (the snapshot leg of <c>cdc.changes</c>) and a bare
/// <c>SET TRANSACTION ISOLATION LEVEL SNAPSHOT</c> (the shipped <c>mssql_materialize=false</c> route) leak
/// identically. The second one's own comment asserted that <c>sp_reset_connection</c> "puts the isolation
/// level back to the default"; the measurement above says it does not. Its CONCLUSION was still right — set
/// the level on every such open rather than once — so nothing was wrong downstream of it; what the wrong
/// reason hid is that the level then travels the other way, out of our read and into whatever runs next on
/// that connection.</para>
/// <para><b>What the leak costs, stated rather than implied.</b> Mostly it is invisible: a later READ at
/// SNAPSHOT returns the same rows a READ COMMITTED one would, only versioned. What is NOT invisible is a DDL
/// inside an explicit transaction, which SQL Server refuses outright at snapshot isolation — <c>Msg 3964,
/// "this DDL statement is not allowed inside a snapshot isolation transaction"</c>. That is how this was
/// found: <c>cdc.changes(enable := true, include := 'snapshot+changes')</c> failed on its own
/// <c>sp_cdc_enable_table</c>, several statements after the snapshot leg that had leaked the level.</para>
/// <para>⚠ RESTORING TO READ COMMITTED IS RIGHT HERE AND WOULD NOT BE EVERYWHERE. It is SQL Server's own
/// default, and this class is only ever wrapped around a connection WE set to SNAPSHOT for one read — never
/// around a transaction's pinned connection, whose level the caller chose
/// (<c>mssql_read_isolation</c> / the ATTACH option) and which is not returned to the pool mid-transaction.
/// </para>
/// </remarks>
internal sealed class SqlServerSnapshotStream : IArrowArrayStream
{
    private readonly IArrowArrayStream _inner;
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;

    /// <param name="inner">The reader stream. It must NOT own the connection — this class does.</param>
    /// <param name="connection">The connection to restore and release.</param>
    /// <param name="transaction">
    /// The snapshot transaction, when the isolation came from <c>BeginTransaction</c> rather than from a
    /// session-scoped <c>SET</c>. Committed before the restore.
    /// </param>
    internal SqlServerSnapshotStream(IArrowArrayStream inner, SqlConnection connection,
                                     SqlTransaction? transaction = null)
    {
        _inner = inner;
        _connection = connection;
        _transaction = transaction;
    }

    public Schema Schema => _inner.Schema;

    public System.Threading.Tasks.ValueTask<RecordBatch?> ReadNextRecordBatchAsync(
        System.Threading.CancellationToken cancellationToken = default)
        => _inner.ReadNextRecordBatchAsync(cancellationToken);

    /// <remarks>
    /// ⚠ ORDER IS THE WHOLE POINT: the reader first (a SET cannot run while it is open), then the
    /// transaction, then the restore, then the connection. Every step is independently guarded — a broken or
    /// already-closed connection must not turn releasing a finished read into an exception.
    /// </remarks>
    public void Dispose()
    {
        var connection = _connection;
        var transaction = _transaction;
        _connection = null;
        _transaction = null;
        try
        {
            _inner.Dispose();
        }
        finally
        {
            if (transaction is not null)
            {
                try
                {
                    // COMMIT rather than ROLLBACK: it read nothing to undo, and committing is what releases
                    // the tempdb version store the view was held against.
                    transaction.Commit();
                }
                catch (Exception)
                {
                    // Already gone (a killed session, a doomed transaction). Nothing was written.
                }
                finally
                {
                    transaction.Dispose();
                }
            }
            if (connection is not null)
            {
                try
                {
                    if (connection.State == System.Data.ConnectionState.Open)
                    {
                        using var reset = connection.CreateCommand();
                        reset.CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED";
                        reset.CommandType = System.Data.CommandType.Text;
                        reset.ExecuteNonQuery();
                    }
                }
                catch (Exception)
                {
                    // ⚠⚠ A CONNECTION WE COULD NOT RESET MUST NOT GO BACK TO THE POOL, and Dispose() ALONE
                    // does not prevent that — it RETURNS a healthy connection to the pool rather than
                    // closing it. An earlier comment here claimed "SqlClient retires a broken connection
                    // rather than pooling it", which is true of a BROKEN one and says nothing about a
                    // healthy connection whose reset failed for another reason (a command timeout, an
                    // attention). That connection is still at SNAPSHOT, and handing it back is exactly the
                    // leak this class exists to close.
                    //
                    // ⚠ ClearPool is the only tool SqlClient offers — there is no per-connection "do not
                    // pool this" — so it is a blunt instrument on a path that should never fire: it evicts
                    // every idle connection for this connection string, and other threads simply reconnect.
                    // Acceptable precisely BECAUSE it is unreachable in normal operation; if it ever starts
                    // firing, that is a signal worth having rather than a cost worth avoiding.
                    try
                    {
                        SqlConnection.ClearPool(connection);
                    }
                    catch (Exception)
                    {
                        // Nothing further is available. The connection is disposed below either way.
                    }
                }
                finally
                {
                    connection.Dispose();
                }
            }
        }
    }
}
