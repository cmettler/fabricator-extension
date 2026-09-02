// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;

namespace Fabricator.Bridge;

/// <summary>
/// Establishes the three per-call ambients — the host-FS opener, the provider-settings session and the DuckDB
/// transaction id — for the duration of ONE ABI crossing, and puts back whatever was there before.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ THE RESTORE IS THE WHOLE POINT, AND IT IS WHY THIS EXISTS RATHER THAN A BARE
/// <c>set_active_opener</c>.</b> That entry ASSIGNS the ambients and never puts them back, which is correct
/// for a crossing that binds or scans its statement's OWN source — the value it leaves behind is the value
/// the rest of that statement wants. It is NOT correct for a crossing that can happen ANYWHERE, and a global
/// SCALAR is exactly that: it is evaluated wherever it is called, including inside a nested host query that
/// an OUTER operation is running while that operation holds the ambient. Overwriting there leaves the outer
/// operation resolving a <c>ClientContext</c> that is gone — MEASURED as a SIGSEGV at
/// <c>OPTIMIZE main.c1</c> when the scalar bind was given a bare <c>FabricatorSetActiveTxn</c> (ABI v80), and
/// the reason the scalar path shipped with NO ambients at all.
/// </para>
/// <para>
/// <b>⚠ It has to be MANAGED-side.</b> The ambients are <c>AsyncLocal</c>s, so the host can only overwrite
/// them — it cannot read one back to restore it. Handing the crossing its caller's context and letting the
/// managed handler save/restore is what makes re-entrancy safe by construction: every crossing puts back what
/// it found, so nesting composes to any depth.
/// </para>
/// <para>
/// <b>⚠⚠ WITHOUT IT THE SCALAR PATH INHERITED WHATEVER THE LAST BINDER LEFT — non-deterministically, and
/// that is strictly worse than having nothing.</b> MEASURED with a one-statement discriminator: a plain
/// <c>SET fluid_template_root</c> is invisible to <c>fabricator_render</c>, and putting a single
/// <c>SELECT * FROM fluid_query('SELECT 1 AS x')</c> between the two makes it visible — because sqlgen's
/// <c>bind_replace</c> sets the ambients on the binder's thread and never clears them, and the scalar's
/// crossing then reads a session that is not its own. The leaked OPENER is the sharper half: it is a raw
/// <c>ClientContext *</c> whose connection may already be gone, so a global scalar doing host-FS IO could
/// dereference a dangling pointer — a case the fs_* null guard cannot catch, because the pointer is not null.
/// </para>
/// <para>
/// ⚠ A struct, and disposed by <c>using</c> in a <c>try</c> that the handler already has: the restore must
/// run even when the crossing throws, or a failed call would leave the ambients pointing at its context.
/// </para>
/// </remarks>
internal readonly struct CallScope : IDisposable
{
    private readonly nint _opener;
    private readonly long _session;
    private readonly long _txn;

    /// <param name="opener">The caller's <c>ClientContext</c> handle, or 0 when the host has none to give.</param>
    /// <param name="session">The provider-settings session key for that context (0 = global layer only).</param>
    /// <param name="txn">The caller's DuckDB transaction id (0 = none / autocommit).</param>
    public CallScope(nint opener, long session, long txn)
    {
        _opener = AmbientOpener.Current;
        _session = ProviderSettingsStore.CurrentSession;
        _txn = AmbientTransaction.Current;
        AmbientOpener.Current = opener;
        ProviderSettingsStore.CurrentSession = session;
        AmbientTransaction.Current = txn;
    }

    public void Dispose()
    {
        AmbientOpener.Current = _opener;
        ProviderSettingsStore.CurrentSession = _session;
        AmbientTransaction.Current = _txn;
    }
}
