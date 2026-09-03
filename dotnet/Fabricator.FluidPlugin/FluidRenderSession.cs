// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;
using Fluid;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The host SQL a SINGLE render may run, on ONE pinned DuckDB connection (ABI v84) — so a template's
/// <c>exec()</c> and its <c>query()</c> see each other.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ <b>THE POINT: <c>exec('CREATE TEMP TABLE t …')</c> then <c>query('SELECT … FROM t')</c> in the same
/// template.</b> Before this, every call opened its own connection, so the second statement could not see
/// the first — and for a TEMP table it could never see it, since a temporary catalog belongs to the
/// ClientContext that created it. A per-render pinned connection turns that pair into a working scratch
/// space that needs no name in the shared catalog and no cleanup: <see cref="Dispose"/> destroys the
/// temporary catalog with the connection.
/// </para>
/// <para>
/// ⚠ <b>LAZY, and that is load-bearing rather than an optimisation.</b> <c>fluid_render</c> is a
/// VOLATILE scalar evaluated PER ROW, so a connection opened eagerly would be opened once per row for every
/// template — including the overwhelming majority that run no SQL at all. Nothing is opened until the first
/// <c>query()</c> or <c>exec()</c>.
/// </para>
/// <para>
/// ⚠ <b>ONE PER RENDER is also what makes it thread-safe.</b> A DuckDB connection is single-threaded by
/// contract, and a volatile scalar may be evaluated on several threads at once; each render builds its own
/// <see cref="TemplateContext"/> and therefore its own session, so nothing is shared across threads. Do NOT
/// hoist this to a static or onto the shared <c>TemplateOptions</c> — that is the same trap the <c>query</c>
/// FILTER registration documents.
/// </para>
/// <para>
/// ⚠⚠ <b>A SHARED session would be WRONG, not merely surprising — and the reason is the session
/// capture.</b> <c>IHostQuery.OpenConnection</c> applies the caller's TimeZone and search path ONCE, at
/// open, so a connection outliving its render would hand every later render the FIRST one's session.
/// MEASURED: a process-wide session makes an existing assertion fail — a render under
/// <c>Asia/Kolkata</c> reports the zone the first render happened to see. That is a wrong VALUE, not
/// stale scratch state, which is the sharper reason per-render scoping is not optional.
/// </para>
/// <para>
/// ⚠ <b>The scope is the RENDER, not the statement.</b> Two rows of one <c>fluid_render</c> call are two
/// renders and therefore two connections: a temp table made by one row is invisible to the next, which is
/// the correct reading of "a rendered template" and keeps a per-row scalar from accumulating state. For
/// <c>fluid_query</c> one bind is one render.
/// </para>
/// <para>
/// ⚠ <b>It does not widen what a template may do.</b> Every statement still goes through the same
/// classifier — <c>query()</c> refuses anything that is not a SELECT, <c>exec()</c> refuses SELECTs — and the
/// connection still reads COMMITTED state, so the surrounding DuckDB statement's own snapshot is unaffected.
/// What changed is only that the template's OWN earlier statements are visible to its later ones.
/// </para>
/// </remarks>
internal sealed class FluidRenderSession : IDisposable
{
    /// <summary>The <see cref="TemplateContext.AmbientValues"/> key carrying the session for this render.</summary>
    internal const string Key = "fabricator.session";

    private readonly IHostQuery _host;
    private IHostConnection? _pinned;
    private bool _disposed;

    private FluidRenderSession(IHostQuery host)
    {
        _host = host;
    }

    /// <summary>
    /// The session for this render, or <see langword="null"/> when the host publishes no
    /// <see cref="IHostQuery"/> (outside a fabricator function call) — in which case <c>query()</c> and
    /// <c>exec()</c> raise their own message naming the missing service.
    /// </summary>
    internal static FluidRenderSession? TryCreate()
    {
        var host = FabricatorServices.Get<IHostQuery>();
        return host is null ? null : new FluidRenderSession(host);
    }

    /// <summary>The session attached to <paramref name="ctx"/>, if this render has one.</summary>
    internal static FluidRenderSession? For(TemplateContext ctx) =>
        ctx.AmbientValues.TryGetValue(Key, out var v) ? v as FluidRenderSession : null;

    /// <summary>Runs <paramref name="sql"/> on this render's connection; the caller disposes the stream.</summary>
    internal IArrowArrayStream Query(string sql, RecordBatch? parameters = null) => Pin().Query(sql, parameters);

    /// <summary>Runs a non-query statement on this render's connection and returns its affected-row count.</summary>
    internal long ExecuteNonQuery(string sql) => Pin().ExecuteNonQuery(sql);

    // The render's connection, opened on first use.
    //
    // ⚠⚠ UNCONDITIONAL, AND THERE IS NO FALLBACK TO A FRESH CONNECTION — nor any capability probe left to
    // consult (user, 2026-09-03: "we don't need any fallbacks with CanPinConnection"). An earlier version
    // had both: it asked IHostQuery.CanPinConnection and degraded to per-call connections when false. Wrong
    // twice over — the branch was unreachable (this plugin is a BUILT-IN published beside the bridge, so it
    // cannot meet an older host), and had it ever fired, exec() and query() would QUIETLY STOP SHARING a
    // connection, which is the single guarantee this class exists to provide. A template would then run and
    // mean something different, with nothing failing. One behaviour: it works, or it says why.
    private IHostConnection Pin()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FluidRenderSession));
        }
        // ⚠ inheritSession: true, so the template's SQL resolves names and renders timestamps the way the
        // statement that reached us does. Applied ONCE here, at open — see IHostQuery.OpenConnection.
        return _pinned ??= _host.OpenConnection(inheritSession: true);
    }

    /// <summary>Closes this render's connection, destroying its temporary catalog. Idempotent.</summary>
    public void Dispose()
    {
        _disposed = true;
        var pin = _pinned;
        _pinned = null;
        pin?.Dispose();
    }
}
