using System;
using System.Collections.Generic;
using System.Data;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Fabricator.SqlServer;

/// <summary>
/// The initial-snapshot leg of <c>db.cdc.changes(...)</c> — slice 8 of docs/mssql-cdc.md, §5's
/// two-connection protocol.
/// </summary>
/// <remarks>
/// <para><b>What it buys: a "start from nothing" read.</b> A change stream can only ever tell you what
/// CHANGED; a consumer starting from an empty sink needs the state that was already there, and needs it to
/// join the stream at a position with no gap and no overlap. This is that handoff.</para>
/// <para><b>The protocol, and the point of it is that the LOCK IS SHORT-LIVED</b> (§5.2). Connection A takes
/// a shared table lock so no write can commit, forces the capture job forward, and reads the handoff
/// position; connection B pins a SNAPSHOT view of the table INSIDE that window; A then commits and the lock
/// is gone — B reads the whole table at leisure afterwards, from a view that is fixed at the instant the
/// lock was held. MEASURED end to end on the rig: while A held the lock a writer was BLOCKED, B's pinned
/// view still reported the pre-write state after A committed and a writer had landed an INSERT and an
/// UPDATE, and the stream from the handoff position delivered EXACTLY those two changes.</para>
/// <para><b>⚠ <c>TABLOCK, HOLDLOCK</c> — a SHARED lock, not <c>TABLOCKX</c></b> (§5.2a). It blocks writers,
/// which is all the protocol needs, and leaves ordinary readers alone. <c>HOLDLOCK</c> is required because
/// <c>TABLOCK</c> alone is STATEMENT-scoped and the window spans several statements.</para>
/// </remarks>
public sealed partial class SqlServerCatalog
{
    /// <summary>
    /// Runs §5's protocol and returns the handoff position together with the pinned snapshot stream.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ STEP 2 — <c>EXEC sys.sp_cdc_scan</c> — IS BEST-EFFORT, AND ITS FAILURE IS THE COMMON CASE
    /// RATHER THAN AN EXCEPTION. MEASURED 2026-08-25, and it re-scopes what this leg can promise.</b> The
    /// step exists to close the capture LAG: the watermark is asynchronous, so a transaction that committed
    /// just before the lock may not be captured yet, which puts its rows in the snapshot AND above the
    /// handoff. Forcing a scan under the lock removes that overlap. But the capture job holds
    /// <c>sp_replcmds</c> for the whole time it is running — including while it sits in its <c>WAITFOR</c>
    /// between polls, because <c>continuous = 1</c> means ONE long-lived <c>sp_cdc_scan</c> invocation that
    /// loops internally. So on a database whose capture job runs in its DEFAULT continuous mode the step is
    /// simply not available: <b>150 attempts across 6 trials over 61 seconds returned <c>Msg 22903</c> every
    /// single time</b>, and the holder was identified as
    /// <c>SQLAgent - TSQL JobStep</c> running <c>sp_cdc_scan</c> in <c>WAITFOR</c>. The positive control that
    /// makes that a finding rather than a broken probe: with the capture job STOPPED, the identical call
    /// succeeds in 3 ms.</para>
    /// <para><b>⇒ ONE attempt, never a retry loop, and a failure does not fail the read.</b> Retrying was
    /// measured to be pure waste — 25 attempts over 10 seconds, six trials, zero successes — and it would
    /// spend that time holding a table lock, which is the one thing §5.2 says this protocol must not do. The
    /// attempt is still worth making because it COSTS 2 ms and it SUCCEEDS wherever the job is not running
    /// continuously, which includes Azure SQL Database (no SQL Agent at all) and any database whose job an
    /// operator has stopped.</para>
    /// <para><b>⇒ AND THE GUARANTEE IS STATED HONESTLY: exactly-once when the scan runs, at-least-once
    /// otherwise</b> — never loss, in either case. The failure direction is a DUPLICATE for the transactions
    /// inside the capture lag: their rows are in the snapshot and their change rows are above the handoff, so
    /// they are delivered twice. That is the guarantee the established practice for this handoff offers
    /// unconditionally (§5.4), so the degraded case is the industry default rather than something below it.
    /// The outcome is LOGGED at Warning, which reaches <c>duckdb_logs</c>, so which of the two happened is
    /// answerable in SQL after the fact rather than only in a comment.</para>
    /// <para><b>⚠ A failing <c>sp_cdc_scan</c> does NOT poison the transaction — MEASURED, because the
    /// opposite would have made the whole protocol unbuildable.</b> After the 22903, <c>XACT_STATE() = 1</c>,
    /// <c>@@TRANCOUNT = 1</c>, the table lock was still granted, and a concurrent writer was still blocked.
    /// (§17.2 measured a DIFFERENT statement failure killing an ambient transaction outright, which is why
    /// this was worth establishing rather than assuming.)</para>
    /// </remarks>
    internal (byte[] Handoff, IArrowArrayStream Stream) CdcOpenSnapshot(CdcChangesPlan plan)
    {
        string schema = plan.SourceSchema ?? "dbo";
        string table = plan.SourceTable ?? plan.Source;
        string qualified = Quote(schema) + "." + Quote(table);
        CdcEnsureSnapshotCannotSelfBlock(qualified);
        if (plan.SnapshotSql is not { } snapshotSql)
        {
            throw new InvalidOperationException(
                $"cdc.changes: internal - a snapshot of '{plan.SourceName}' was requested but no snapshot "
                + "statement was composed at bind.");
        }

        SqlConnection? a = null;
        SqlTransaction? txA = null;
        SqlConnection? b = null;
        SqlTransaction? txB = null;
        InterruptScope? interrupt = null;
        try
        {
            // ⚠ B IS OPENED AND ITS PRECONDITION CHECKED BEFORE THE LOCK IS TAKEN, and that ordering is the
            // protocol's own discipline applied to itself: the lock freezes every writer on this table, so
            // nothing that can be done outside the window belongs inside it. A database without
            // ALLOW_SNAPSHOT_ISOLATION would otherwise pay a write freeze on every attempt before being told
            // it cannot do this at all — a cost borne by exactly the user already having a bad time.
            // ⚠ Only BeginTransaction and the pin have to be INSIDE: SNAPSHOT fixes its view at the first
            // statement that touches DATA, and that statement is the pin.
            b = OpenConnection();
            b.Open();
            EnsureSnapshotIsolationAllowed(b, "cdc.changes(include := '" + plan.Include + "')");

            a = OpenConnection();
            a.Open();
            txA = a.BeginTransaction();
            // Step 1: freeze writers. ⚠ TOP (1) rather than TOP (0) or a COUNT: the hint has to be attached
            // to a statement that really scans, and a full count on a large table would spend the whole
            // freeze reading rows nobody wants. MEASURED that TOP (1) holds OBJECT/S to the next statement
            // and blocks a writer — including on an EMPTY table, where "there is nothing to read" could
            // plausibly have let the optimiser skip the scan and the lock with it.
            CdcExecuteOn(a, txA, "SELECT TOP (1) 1 AS pin FROM " + qualified + " WITH (TABLOCK, HOLDLOCK)");

            // Step 2: best-effort. See the remarks — one attempt, and its failure is expected.
            bool scanned;
            try
            {
                CdcExecuteOn(a, txA, "EXEC sys.sp_cdc_scan");
                scanned = true;
            }
            catch (SqlException ex)
            {
                scanned = false;
                Log.LogWarning(
                    "cdc changes {Source}: could not force the capture job forward before the snapshot "
                    + "({Number}: {Message}) - this read is AT-LEAST-ONCE rather than exactly-once, so a "
                    + "transaction that committed within the capture lag may be delivered both in the "
                    + "snapshot and again in the change stream. Nothing is lost. Stop the capture job "
                    + "(sys.sp_cdc_stop_job) if the duplicates matter.",
                    plan.SourceName, ex.Number, ex.Message.Split('\n')[0]);
            }

            // Step 3: the handoff position, read on A in an ordinary transaction — which is what dissolves
            // §11 item 3 (nothing depends on how fn_cdc_get_max_lsn behaves inside a snapshot transaction).
            byte[]? p0 = CdcScalarOn(a, txA, "SELECT sys.fn_cdc_get_max_lsn()") as byte[];
            if (p0 is null)
            {
                throw new InvalidOperationException(
                    $"cdc.changes: a snapshot of '{plan.SourceName}' has no handoff position - "
                    + "sys.fn_cdc_get_max_lsn() is NULL, which means nothing in this database has been "
                    + "captured yet. The snapshot itself would be readable, but there would be no position "
                    + "to resume the change stream from, so the rows would be a dead end. Retry in one "
                    + "polling interval (cdc.health() reports it), or cdc.capture_now() to force a scan.");
            }

            // Step 4: pin B's view INSIDE the window. ⚠ The pin STATEMENT is required: SNAPSHOT isolation
            // fixes its view at the first statement that touches DATA, not at BEGIN TRANSACTION, so without
            // it the view would be taken after the release and could miss a write. MEASURED that TOP (1) 1
            // is enough — B still reported the pre-write state after A committed and a writer landed.
            // ⚠ BeginTransaction(Snapshot), never SET TRANSACTION ISOLATION LEVEL SNAPSHOT after a BEGIN:
            // SNAPSHOT is the one level SQL Server refuses to switch INTO mid-transaction (§5.5).
            txB = b.BeginTransaction(IsolationLevel.Snapshot);
            CdcExecuteOn(b, txB, "SELECT TOP (1) 1 AS pin FROM " + qualified);

            // Step 5: release. Everything after this runs with no lock held.
            txA.Commit();
            txA.Dispose();
            txA = null;
            a.Dispose();
            a = null;

            byte[] handoff = CdcChangesPlan.HandoffPosition(p0);
            Log.LogDebug("cdc changes {Source}: snapshot pinned, handoff {Handoff}, capture forced={Scanned}",
                         plan.SourceName, CdcChangesPlan.Hex(p0), scanned);

            // Step 6: the read itself, on B, in the pinned transaction.
            //
            // ⚠ IT GETS AN InterruptScope, like every other DATA scan (docs/cancellation.md). This is the
            // ONE statement here that can run long — the whole point of the protocol is that the lock is
            // released before it starts — so it is exactly the read a user reaches for Ctrl+C during. The
            // five short statements above deliberately do not have one: they run inside the lock window,
            // where the honest response to an interrupt is to finish and release rather than to abandon a
            // held table lock.
            interrupt = new InterruptScope(AmbientOpener.Current);
            var command = b.CreateCommand();
            command.CommandText = snapshotSql;
            command.CommandType = CommandType.Text;
            command.CommandTimeout = ResolveCommandTimeout();
            command.Transaction = txB;
            command.Parameters.Add(new SqlParameter("@handoff", SqlDbType.Binary, CdcChangesPlan.PositionBytes)
            {
                Value = handoff,
            });
            SqlDataReader reader;
            try
            {
                reader = command.ExecuteReaderAsync(interrupt.Token).GetAwaiter().GetResult();
            }
            catch
            {
                command.Dispose();
                throw;
            }
            // ⚠ ownsConnection: false — SqlServerSnapshotStream owns it, because the connection has to be
            // RESTORED to READ COMMITTED before it goes back to the pool. See that class: snapshot isolation
            // is session-scoped and survives both the commit and the pool round trip (MEASURED), so a
            // connection released from here still at SNAPSHOT poisons whatever runs on it next - which is
            // how this was found, as a Msg 3964 on a DDL several statements later.
            var stream = new SqlServerSnapshotStream(
                new DbDataReaderArrowStream(b, command, reader, ownsConnection: false, interrupt: interrupt),
                b, txB);
            // Owned by the stream from here; the catch below must not dispose them a second time.
            txB = null;
            b = null;
            interrupt = null;
            return (handoff, stream);
        }
        catch
        {
            // ⚠ A is rolled back rather than committed: it wrote nothing, and rolling back releases the
            // table lock at the earliest possible moment on the failure path too.
            CdcSafeRollback(txA);
            txA?.Dispose();
            a?.Dispose();
            CdcSafeRollback(txB);
            txB?.Dispose();
            b?.Dispose();
            interrupt?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Refuses the snapshot when THIS transaction holds uncommitted writes on the source table.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ AND IT IS UNCONDITIONAL, unlike the pooled-scan hazard next door — the three escapes that
    /// make that one safe do not apply here.</b> That check stands down under MARS (the scan shares the
    /// transaction's own connection, so it owns the locks), under <c>mssql_read_isolation</c> (same reason by
    /// another route) and under RCSI (a versioned reader never waits on a lock). None of them helps a
    /// <c>TABLOCK</c>: this protocol MUST take its lock on a connection of its own, because the lock's whole
    /// point is to be released by a COMMIT whose timing we control and committing the caller's transaction is
    /// not ours to do — and a lock REQUEST is not a read, so row versioning does not exempt it. The request
    /// would therefore wait on a lock only the caller's own transaction can release, which is limitation
    /// 1.15's unbounded hang rather than an error.</para>
    /// <para>⚠ It asks about the SOURCE table specifically, not about the transaction, so an explicit
    /// transaction that has written something ELSE snapshots normally.</para>
    /// </remarks>
    private void CdcEnsureSnapshotCannotSelfBlock(string qualified)
    {
        if (!TransactionHasWritten(qualified, out bool schemaChanged))
        {
            return;
        }
        throw new InvalidOperationException(
            $"cdc.changes: cannot snapshot {qualified} - this transaction has "
            + (schemaChanged ? "an uncommitted schema change on it" : "uncommitted writes to it")
            + ", and a snapshot has to take a table lock on a connection of its own so it can release it at "
            + "a moment of its choosing. That lock would wait for locks only this transaction can release, "
            + "which never returns. COMMIT (or ROLLBACK) first and read again, or read "
            + "include := 'changes', which takes no lock.");
    }

    private static void CdcSafeRollback(SqlTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }
        try
        {
            transaction.Rollback();
        }
        catch (Exception)
        {
            // The transaction may already be gone (a killed session, a doomed transaction). The caller is
            // throwing something more informative than anything this could add.
        }
    }

    private void CdcExecuteOn(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = ResolveCommandTimeout();
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private object? CdcScalarOn(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = ResolveCommandTimeout();
        command.Transaction = transaction;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
