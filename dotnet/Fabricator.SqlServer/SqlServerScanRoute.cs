// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

// THE CONNECTION ROUTER — the one place that answers "which connection does this read run on, and why"
// (docs/catalog-table-abstraction.md §4; slice 1 of its migration order). Extracted 2026-08-14 from
// ExecuteQuery's preamble, where the same decision was five booleans threaded through 130 lines. The rules
// below are TRANSCRIPTIONS of that preamble — the measured whys moved with the logic they justify — and the
// behaviour claim is that the existing routing suites (verify_read_isolation, verify_mars_dynamic,
// verify_mars_off_same_catalog, verify_read_write_same_catalog) pass at unchanged counts.
//
// The FOUR routes (docs/transactions.md §5.6a is the measured matrix behind them):
//
//   | route            | connection | isolation      | reader                          |
//   |------------------|-----------|-----------------|---------------------------------|
//   | pinned+streaming | txn's     | txn's           | held open (needs MARS)          |
//   | pinned+drained   | txn's     | txn's           | drained + closed before return  |
//   | pooled           | fresh     | READ COMMITTED  | streaming                       |
//   | pooled+SNAPSHOT  | fresh     | SNAPSHOT        | streaming                       |
//
// ⚠ What the router deliberately does NOT own: EnsureScanCannotSelfBlock (it needs the TABLE being scanned,
// which ExecuteQuery never sees — it stays at the scan layer that knows the name), and the pooled branch's
// SNAPSHOT precondition probe (an EXECUTION step on the opened connection, not a decision).
public sealed partial class SqlServerCatalog
{
    /// <summary>
    /// A resolved scan route. <see cref="Pinned"/> null means the pooled branch; <see cref="Drain"/> is the
    /// EFFECTIVE materialize (the read-isolation rule may force it true — see rule 2); <see cref="ExecGate"/>
    /// is non-null only on the no-MARS read-isolation path and is released with the reader already drained,
    /// never held across a stream the caller is still pulling; <see cref="Snapshot"/> applies to the pooled
    /// branch only. <see cref="Reason"/> is for the <c>query [...]</c> Debug line — the log says WHY a scan
    /// routed, not just where.
    /// </summary>
    internal readonly record struct ScanRoute(
        SqlConnection? Pinned,
        SqlTransaction? PinnedTransaction,
        object? ExecGate,
        bool Drain,
        bool Snapshot,
        string Reason);

    /// <summary>
    /// Resolves the route for one read. Rules evaluated in order; the first match wins. ⚠ Rule 2 has a
    /// deliberate SIDE EFFECT (it may CREATE the transaction's pinned connection) — that is the
    /// <c>mssql_read_isolation</c> opt-in's documented job (a READ creates the pin, else a transaction that
    /// has not written has no server-side transaction for the level to apply to), not router sloppiness.
    /// </summary>
    private ScanRoute RouteScan(bool readYourWrites, bool materialize, bool snapshotRead, long txnId,
                                bool pooledOnly = false)
    {
        // ── Rule 1: the CONTRADICTION refusal ─────────────────────────────────────────────────────────────
        // ⚠ ONLY WHEN THE FALSE WAS ASKED FOR — `MaterializeExplicitlyFalse`, not `!ResolveMaterialize()`.
        // Since `mssql_materialize` began DEFAULTING to MARS (2026-08-10), a no-MARS engine resolves it to
        // false with nobody having requested anything, so testing the RESOLVED value made this refusal fire
        // for every user who set `mssql_read_isolation` alone — a hard error on Fabric/Synapse, where the
        // default is always false. MEASURED on box with `mssql_mars='false'`: 3 of 3 runs refused. The whole
        // premise of the message below is that BOTH are active requests; a default is not a request.
        if (snapshotRead && txnId != 0 && MaterializeExplicitlyFalse() &&
            !string.IsNullOrEmpty(ResolveReadIsolation()))
        {
            // Both are ACTIVE requests and they contradict: one asks for every read to be inside the
            // transaction, the other for this particular read to be outside it on a pooled connection.
            // Honouring either silently would give the statement a view the user did not ask for, so refuse
            // and let them pick. (mssql_materialize=false only ever marks a same-catalog read+write scan, so
            // this cannot fire on an ordinary SELECT.)
            throw new System.InvalidOperationException(
                "fabricator: mssql_materialize=false and mssql_read_isolation contradict each other. The first " +
                "keeps a scan of the table being written STREAMING on a POOLED connection outside this " +
                "transaction; the second puts every read INSIDE it so they share one view. This scan cannot do " +
                "both. Either unset mssql_read_isolation, or leave mssql_materialize at its default (true), " +
                "which buffers that scan onto the transaction's own connection.");
        }

        // ── Rule 1b: POOLED-ONLY — the caller states that this read can never want the transaction ────────
        // ⚠⚠ IT IS A PROPERTY OF THE READER, NOT A PREFERENCE, and only one reader has it today:
        // `cdc.changes`. The capture job populates a change table ASYNCHRONOUSLY from COMMITTED log records,
        // so a change this transaction just made is not there yet on ANY connection — read-your-writes buys a
        // change reader NOTHING, on the pinned connection or off it. The pin is therefore pure cost, and
        // MEASURED cost in both directions:
        //   • rule 3 (MARS on, the transaction has written) pinned the change read onto the WRITE connection
        //     as a long STREAMING reader — the 595 hazard exactly ("Bulk Insert with another outstanding
        //     result set"), for a benefit that does not exist;
        //   • rule 2 (mssql_read_isolation, MARS off) pinned it AND DRAINED it, buffering an entire change
        //     window into memory and taking the ExecGate, again for nothing.
        // MEASURED before the flag existed, same statement, from the route log: autocommit `route=pooled`,
        // and inside a transaction that had written the source `route=pin (MARS)`.
        //
        // ⚠ It introduces no self-block hazard, and that is established rather than hoped: `DescribeQuery`
        // already opens its OWN pooled connection unconditionally, so `cdc.changes` cannot even BIND unless
        // the change table is visible to a pooled connection. A capture instance enabled in an uncommitted
        // transaction fails there first, with a sentence that says so.
        //
        // ⚠ It is deliberately NOT applied to the WINDOW resolution, which goes the other way for a real
        // reason: a capture instance enabled in THIS transaction IS visible to it, and the window has to see
        // it. That read is `readYourWrites: true` and keeps rule 3.
        if (pooledOnly)
        {
            return new ScanRoute(null, null, null, Drain: false, Snapshot: false, "pooled (reader opts out)");
        }

        // ── Rule 2: the READ-ISOLATION PIN (mssql_read_isolation opt-in) ──────────────────────────────────
        // CREATE the pin for a read, so a transaction that has not written still has a server-side
        // transaction for the level to apply to. Without this the rule below finds no state at all and the
        // read goes pooled — which is why the level alone was never enough.
        //
        // ⚠ NOT for a snapshotRead scan: that is mssql_materialize=false explicitly asking for a POOLED read
        // outside the transaction, the opposite request (refused above when both were asked for).
        // ⚠ NOT in autocommit (txnId == 0): there is no transaction to be stable across.
        //
        // ⚠ The pin is taken DIRECTLY rather than left to rule 3, and that is not style. Routing it through
        // `materialize` would couple this to a condition one rule away: if rule 3 ever stopped admitting the
        // read, the scan would go POOLED while EnsureScanCannotSelfBlock had already exempted it — and a
        // pooled read against this transaction's own uncommitted writes with MARS off is the UNBOUNDED HANG
        // of limitation 1.15, not an error. Exempting a check must be paid for by guaranteeing the condition
        // it checked for.
        if (txnId != 0 && !snapshotRead && !string.IsNullOrEmpty(ResolveReadIsolation()))
        {
            var opted = EnsureTxnConnection(txnId);
            SqlConnection? pinned;
            SqlTransaction? pinnedTransaction;
            bool optedMars; // this connection's OWN mode, read with the fields it describes
            lock (opted)
            {
                pinned = opted.Connection;
                pinnedTransaction = opted.Transaction;
                optedMars = opted.MarsEnabled;
            }
            if (optedMars)
            {
                return new ScanRoute(pinned, pinnedTransaction, null, materialize, Snapshot: false,
                                     "read_isolation pin");
            }
            // With MARS off the pinned connection admits ONE reader at a time, so BOTH halves are needed and
            // draining alone is NOT enough — measured: two scalar subqueries over one table start in the same
            // millisecond on two threads, so the second ExecuteReader lands while the first is still draining
            // ("The connection does not support MultipleActiveResultSets"). The drain bounds how long a
            // reader is open; the gate stops two from being open at once.
            //
            // This is the cost the opt-in trades streaming for on a no-MARS engine (Fabric/Synapse):
            // transaction-scoped consistency and streaming multi-ref reads are mutually exclusive there, and
            // it picks consistency.
            return new ScanRoute(pinned, pinnedTransaction, opted.ExecGate, Drain: true, Snapshot: false,
                                 "read_isolation pin, MARS off: drained+gated");
        }

        // ── Rule 3: an EXISTING PIN (the transaction has a connection — a write happened) ─────────────────
        // Read on that connection so the query sees uncommitted changes (read-your-writes). For a data SCAN
        // this is gated on MARS — an open scan reader and the transaction's DML can only coexist on one
        // connection under MARS, so with MARS off (Fabric/Synapse, or mssql_mars=false) scans take a fresh
        // pooled connection (documented warehouse trade-off — docs/transactions.md §5.1). Two exemptions,
        // both qualifying because the reader is closed before this call returns:
        //   • a METADATA read (readYourWrites) fully drains immediately — and reusing the pin is REQUIRED so
        //     a just-created table's metadata is visible (else the self-healing cache would evict the table
        //     the CREATE just made; see FabricatorSchemaEntry::CreateTable);
        //   • `materialize` drains the reader too — which is what restores READ-YOUR-WRITES on a no-MARS
        //     engine, where an ordinary scan is pooled and sees only committed state.
        //
        // ⚠ state.MarsEnabled, NOT a freshly resolved value: this decides whether the scan may reuse THIS
        // pinned connection, and only the mode it was opened with answers that.
        // ⚠ DEFENSIVE, NOT GATED — say so rather than implying a test covers it. Re-resolving here is a
        // mutant that SURVIVES the suite, and necessarily: a transaction belongs to ONE DuckDB connection,
        // so the two answers can differ only if that session changes `mssql_mars` BETWEEN pinning the
        // connection and this scan. That is meaningless as a request, and the failure it would cause (a scan
        // sent to a no-MARS pinned connection) is limitation 1.15's unbounded HANG — so a gate for it would
        // be a test that hangs rather than fails, which is worse than none.
        if (txnId != 0 && _txns.TryGet(txnId) is { } state)
        {
            lock (state)
            {
                if (state.Connection is not null && !snapshotRead &&
                    (state.MarsEnabled || readYourWrites || materialize))
                {
                    string why = state.MarsEnabled ? "pin (MARS)"
                        : readYourWrites ? "pin (metadata ryw, drains immediately)"
                                         : "pin (drained scan)";
                    return new ScanRoute(state.Connection, state.Transaction, null, materialize,
                                         Snapshot: false, why);
                }
            }
        }

        // ── Rule 4: POOLED — the default, and the mssql_materialize=false SNAPSHOT route ──────────────────
        // `snapshotRead` keeps the marked scan STREAMING on a pooled connection at SNAPSHOT isolation, so it
        // shares no connection with the pinned writer. The isolation SET + its precondition probe are
        // EXECUTION steps on the opened connection and stay in ExecuteQuery's pooled branch.
        return new ScanRoute(null, null, null, materialize, snapshotRead,
                             snapshotRead ? "pooled at SNAPSHOT (mssql_materialize=false)" : "pooled");
    }
}
