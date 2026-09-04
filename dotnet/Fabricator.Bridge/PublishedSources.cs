// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// The registry behind <see cref="IHostConnection.Publish"/>: a relation staged on a PINNED connection,
/// handed to the hosting DuckDB as a named Arrow source under an opaque token — so a table a template
/// staged in its own temporary catalog can be scanned by the statement the template generated.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ IT IS LAZY AND IT DOES NOT BUFFER (user-directed, 2026-09-04).</b> A publication registers a
/// STATEMENT, not rows: nothing runs until the scan pulls, the rows then stream, and the resources are
/// released by the scan's own disposal of the stream. So there is no memory ceiling and no row cap — a
/// staged relation larger than memory is fine, because DuckDB streams it and spills its own side.
/// </para>
/// <para>
/// <b>⚠⚠ WHAT MAKES IT WORK IS A v84 PROPERTY THAT ALREADY EXISTED — worth knowing, because without it
/// laziness would need a lifetime protocol of its own.</b> <see cref="Host.HostConnection.Dispose"/> is
/// *"safe with result streams still outstanding: each holds its own reference to the underlying connection,
/// so it dies with the last of them rather than under a live stream"*. ⇒ once <c>Query</c> has RETURNED a
/// stream, nothing here needs to keep the connection alive; the stream does, and the temporary catalog
/// holding the staged table lives exactly as long. The only thing that must survive the render is the
/// HANDLE, so a scan can still ISSUE its query — which is what the reference count is for, and it is given
/// back the moment the stream exists.
/// </para>
/// <para>
/// <b>⚠ SINGLE-USE, failing LOUDLY.</b> A publication is claimed by the first scan that opens it; a second
/// says so and names the fix rather than returning zero rows — the silent-short-read failure this repo has
/// paid for twice. Two references want two publications, which are independent.
/// </para>
/// <para>
/// <b>⚠⚠ AN UNSCANNED PUBLICATION HOLDS ITS PIN OPEN, and the eviction cap is what bounds that.</b> A bind
/// that is never executed publishes and never scans — an <c>EXPLAIN</c> of a generated statement is exactly
/// that, a routine path rather than an error path — and nothing in managed code can observe "the caller's
/// statement finished". So an unscanned publication keeps a DuckDB connection and its staged table alive
/// until it becomes the oldest of <see cref="MaxLive"/>. That is the price of laziness, stated plainly: the
/// buffered alternative held the ROWS in managed memory under a row cap instead.
/// ⚠ <see cref="MaxLive"/> also bounds how many publications ONE statement may make, so it must stay well
/// above any plausible number of <c>publish()</c> calls in a single template.
/// </para>
/// <para>
/// ⚠ <b>The DECLARED schema is mandatory, not an optimisation.</b> Without it a table function's BIND opens
/// a stream to learn its columns, which would CLAIM the single handout and leave the scan with nothing.
/// That is why <see cref="Register"/> demands a schema and why the caller pays one cheap probe for it.
/// </para>
/// </remarks>
internal static class PublishedSources
{
    /// <summary>Live publications kept before the oldest is reclaimed.</summary>
    internal const int MaxLive = 32;

    /// <summary>
    /// Rows per exported Arrow batch for a publication's stream (ABI v85). DuckDB's own row-group size, and
    /// the reason a publication may ask for it when <c>host_query</c>'s default cannot: these rows are
    /// SCAN MORSELS, never files.
    /// </summary>
    /// <remarks>
    /// The exported batch IS the morsel of a parallel Arrow scan, so a 2048-row default multiplies mutex
    /// acquisitions, ArrowAppender copies, imports and converter setups by 60. MEASURED on 100M rows of one
    /// BIGINT through a publication, threads=8, interleaved: 2.08-2.13 s at the default against
    /// 0.83-0.85 s here, with `sys` time falling 2.1-3.0 s to 0.4-0.5 s — the allocation churn disappearing.
    /// </remarks>
    internal const long BatchRows = 122880;

    // ⚠ The token is what goes into GENERATED SQL, so it is an opaque unguessable name and never an address.
    // A pointer would be writable, copyable and re-runnable by anyone who read the statement, which is the
    // use-after-free class the temporary-view fix exists to have closed.
    private const string Prefix = "__fabpub_";

    private sealed class Entry
    {
        internal Entry(PinnedHostConnection pin, string sql, Schema schema)
        {
            Pin = pin;
            Sql = sql;
            Schema = schema;
        }

        internal PinnedHostConnection Pin { get; }

        internal string Sql { get; }

        internal Schema Schema { get; }

        /// <summary>1 once a scan has claimed the single handout — or once eviction has claimed it.</summary>
        internal int Claimed;

        /// <summary>Set when the cap reclaimed this publication, so a later scan can say WHY.</summary>
        internal bool Evicted;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    // Publication order, for eviction. Guarded by Order; contention is nil (one entry per publish).
    private static readonly Queue<string> Order = new();

    // ⚠ Reclaimed tokens stay REGISTERED for a while, and that is a diagnostic decision paid for by
    // measurement: unregistering on eviction means the factory is never reached, so the scan fails with the
    // registry's generic "no named source registered as '__fabpub_…'" — which names the token and not the
    // cause. A bounded ring of tombstones lets the recent ones answer properly. A tombstone holds a schema,
    // a SQL string and a closure — no rows and no connection reference.
    private static readonly Queue<string> Tombstones = new();

    /// <summary>
    /// Registers <paramref name="sql"/> — to be run on <paramref name="pin"/> when the scan pulls — under a
    /// fresh token. <paramref name="schema"/> must be what that statement produces; the caller establishes
    /// it, because the bind is answered from the declaration and must not open a stream.
    /// </summary>
    internal static string Register(PinnedHostConnection pin, string sql, Schema schema)
    {
        var token = Prefix + Guid.NewGuid().ToString("N");
        // ⚠ Taken BEFORE registering: from here until the scan opens (or eviction reclaims), THIS is what
        // keeps the pin's handle usable, and the render's own Dispose no longer is.
        pin.AddRef();
        Entries[token] = new Entry(pin, sql, schema);
        Host.RegisterSource(token, () => Open(token), schema);
        lock (Order)
        {
            Order.Enqueue(token);
        }
        Trim();
        return token;
    }

    // The factory, invoked by the SCAN — never by the bind, which the declared schema answers.
    private static IArrowArrayStream Open(string token)
    {
        if (!Entries.TryGetValue(token, out var entry))
        {
            // Past even the tombstone ring. Unreachable in practice — the name is unregistered by then, so
            // the registry answers first — but a race between Trim and a scan lands here.
            throw new InvalidOperationException(
                $"publish: the publication '{token}' is long gone. Publish it in the statement that scans it.");
        }
        if (entry.Evicted)
        {
            throw new InvalidOperationException(
                $"publish: the publication '{token}' was reclaimed — more than {MaxLive} publications have "
                + "been made since, and an unscanned publication is only held that long. Publish it in the "
                + "statement that scans it, rather than keeping a token across statements.");
        }
        if (Interlocked.Exchange(ref entry.Claimed, 1) == 1)
        {
            throw new InvalidOperationException(
                "publish: a publication can be scanned ONCE and this one has been scanned already. Call "
                + "publish() again for a second reference — each call is an independent publication.");
        }

        try
        {
            var stream = entry.Pin.OpenPublication(entry.Sql);
            // ⚠⚠ GIVEN BACK HERE, not when the stream ends — and that is the v84 property rather than a
            // shortcut: the returned stream holds its OWN reference to the underlying connection, so the
            // pin, and the temporary catalog holding the staged table, stay alive for exactly as long as
            // the stream needs them. Everything from here is the consumer's, released by disposing it.
            entry.Pin.Release();
            return stream;
        }
        catch
        {
            entry.Pin.Release();
            throw;
        }
    }

    // Keeps at most MaxLive live publications, reclaiming the oldest. An entry already CLAIMED by a scan
    // holds no connection reference and has nothing to release; an unscanned one gives its pin reference
    // back here, which is what lets the connection close.
    private static void Trim()
    {
        while (true)
        {
            string? evict = null;
            lock (Order)
            {
                if (Order.Count > MaxLive)
                {
                    evict = Order.Dequeue();
                }
            }
            if (evict is null)
            {
                return;
            }
            if (Entries.TryGetValue(evict, out var entry))
            {
                entry.Evicted = true;
                if (Interlocked.Exchange(ref entry.Claimed, 1) == 0)
                {
                    entry.Pin.Release();
                }
            }
            string? forget = null;
            lock (Tombstones)
            {
                Tombstones.Enqueue(evict);
                if (Tombstones.Count > MaxLive)
                {
                    forget = Tombstones.Dequeue();
                }
            }
            if (forget is not null)
            {
                Host.UnregisterSource(forget);
                Entries.TryRemove(forget, out _);
            }
        }
    }
}
