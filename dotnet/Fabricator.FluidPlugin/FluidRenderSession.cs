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
/// <c>fluid_replacement_query</c> one bind is one render.
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
    private string? _variableName;
    private Func<RecordBatch?>? _variableRows;
    private bool _variableApplied;

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

    /// <summary>
    /// Declares a DuckDB variable this render's SQL can read as <c>getvariable('&lt;name&gt;')</c>, set from
    /// the single cell of the one-row batch <paramref name="rows"/> returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>DEFERRED, and that is the whole point of the factory.</b> <c>fluid_render</c> is a per-ROW
    /// scalar, so binding eagerly would copy a bag for every row of every template — including the
    /// overwhelming majority that run no SQL at all. Nothing is read and no statement is issued until this
    /// render's connection is opened, which is exactly the condition under which a variable can matter.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>THE FACTORY MUST NOT CLOSE OVER ANYTHING SHORTER-LIVED THAN THIS SESSION.</b> It runs at
    /// pin-open, which is INSIDE the call for <c>fluid_render</c> and <c>fluid_replacement_query</c> — their args are
    /// still alive, so those may slice live Arrow — and LONG AFTER the bind for the three deferred surfaces,
    /// whose execution session is created once their arguments are already freed. Those must close over a
    /// COPY; <see cref="FluidValueModel.CopyBagRow"/> is it.
    /// </para>
    /// <para>
    /// ⚠ The variable is scoped to this render's connection and to nothing else — DuckDB keeps variables in
    /// <c>ClientConfig::user_variables</c>, a per-ClientContext map. MEASURED in both directions: a variable
    /// set here is invisible to the NEXT render, and one set by the CALLER'S session is invisible here. So
    /// the name can collide with nothing the user has, and the value dies with the render.
    /// </para>
    /// <para>
    /// ⚠ The batch belongs to the FACTORY and is never disposed here — <c>fluid_query_lateral</c> hands the
    /// SAME copy to every pipeline thread's session, so a session that disposed it would pull it out from
    /// under the others. Each only borrows it for the one statement, exactly as
    /// <see cref="RegisterRows(RecordBatch)"/> documents.
    /// </para>
    /// </remarks>
    internal void BindVariable(string name, Func<RecordBatch?> rows)
    {
        _variableName = name;
        _variableRows = rows;
        // ⚠⚠ IF THIS SESSION HAS ALREADY OPENED ITS CONNECTION, STAGE IT NOW — Pin() applies a binding only
        // at OPEN, so a caller that ran any statement BEFORE binding would otherwise get a variable that is
        // never set. MEASURED: fluid_scalar staged its input relation before building its render context and
        // read getvariable('params') as NULL — a silent wrong value, not an error, and the kind of implicit
        // ordering contract that bites the next caller rather than the one who wrote it.
        if (_pinned is not null)
        {
            ApplyVariable(_pinned);
        }
    }

    /// <summary>Runs <paramref name="sql"/> on this render's connection; the caller disposes the stream.</summary>
    internal IArrowArrayStream Query(string sql, RecordBatch? parameters = null) => Pin().Query(sql, parameters);

    /// <summary>Runs a non-query statement on this render's connection and returns its affected-row count.</summary>
    internal long ExecuteNonQuery(string sql) => Pin().ExecuteNonQuery(sql);

    /// <summary>
    /// Publishes <paramref name="sql"/> — to be run LATER on THIS render's connection, so it can read the
    /// template's own staged temp tables — as a named Arrow source, returning the token.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ The publication OUTLIVES this session, and it has to: <c>fluid_replacement_query</c> renders during
    /// <c>bind_replace</c>, so by the time the generated SQL is parsed — let alone scanned — <see
    /// cref="Dispose"/> has already run. It works because the connection is REFERENCE-COUNTED: an
    /// unscanned publication holds the handle open, so <see cref="Dispose"/> gives up the render's claim
    /// without closing anything, and the temporary catalog survives to be read. See FluidHostPublish and
    /// docs/fluid-templating.md §18.
    /// </remarks>
    internal string Publish(string sql) => Pin().Publish(sql);

    /// <summary>
    /// Registers <paramref name="rows"/> as a named Arrow source, returning a token scannable as
    /// <c>fabricator_scan('&lt;token&gt;')</c> — including on THIS render's pinned connection, which is how
    /// a batch of rows becomes a temp table the template can read.
    /// </summary>
    /// <remarks>⚠ BORROWED: the caller keeps owning <paramref name="rows"/> and must
    /// <see cref="ReleaseRows"/> before they are freed.</remarks>
    internal string RegisterRows(RecordBatch rows) => _host.RegisterRows(rows);

    /// <summary>Registers an EMPTY source declaring <paramref name="schema"/> — a relation with the right
    /// columns and no rows, which is how a table's shape is created without rendering DuckDB type names.</summary>
    internal string RegisterRows(Schema schema) => _host.RegisterRows(schema);

    /// <summary>Releases a token from <see cref="RegisterRows"/>.</summary>
    internal bool ReleaseRows(string token) => _host.ReleaseRows(token);

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
        if (_pinned is not null)
        {
            return _pinned;
        }
        // ⚠ inheritSession: true, so the template's SQL resolves names and renders timestamps the way the
        // statement that reached us does. Applied ONCE here, at open — see IHostQuery.OpenConnection.
        //
        // ⚠ Assigned BEFORE the variable is staged, so a staging failure still leaves the connection owned
        // by this session and closed by Dispose rather than leaked.
        _pinned = _host.OpenConnection(inheritSession: true);
        ApplyVariable(_pinned);
        return _pinned;
    }

    // Stages the declared variable onto the freshly opened connection.
    //
    // ⚠ Through a named Arrow source rather than rendered SQL, which is what makes it TYPE-EXACT: the value
    // never becomes text, so a DECIMAL keeps its scale, a temporal its unit and zone, and a STRUCT its field
    // types — MEASURED, `SET VARIABLE x = (SELECT …)` reports STRUCT(a INTEGER, d DATE) back unchanged.
    // Rendering it instead would mean a second SQL type ladder, and DuckSql.Literal deliberately REFUSES a
    // LIST or a STRUCT rather than guess at one.
    //
    // ⚠ `_variableApplied` is set BEFORE the attempt, so a failure raises once — to the template author, who
    // is the only one who can fix it — instead of being retried by every later statement of the render.
    //
    // ⚠⚠ THROWING IS SAFE HERE ONLY BECAUSE THE TYPE WAS ALREADY VALIDATED: every caller runs
    // FluidValueModel.CaptureBag FIRST, and that REFUSES any bag ReadCell does not know. So the set of bags
    // reaching this staging is {JSON string} plus {STRUCT, MAP, LIST, scalar} — all standard Arrow, all
    // MEASURED to round-trip (incl. a MAP, a nested STRUCT, a LIST of STRUCT, a BLOB and a TIMESTAMPTZ).
    // Without that ordering this would be a NEW way for an existing working template to fail, since a
    // render pays the staging whether or not it ever reads the variable.
    private void ApplyVariable(IHostConnection pin)
    {
        if (_variableApplied || _variableRows is null || _variableName is null)
        {
            return;
        }
        _variableApplied = true;
        var rows = _variableRows();
        if (rows is null)
        {
            // ⚠ No bag ⇒ no statement. An UNSET DuckDB variable already reads as NULL, so writing one would
            // buy nothing and cost a round trip on every render that passed no params.
            return;
        }
        var token = _host.RegisterRows(rows);
        try
        {
            var name = DuckSql.QuoteIdent(_variableName);
            pin.ExecuteNonQuery($"SET VARIABLE {name} = (SELECT {name} FROM "
                                + $"fabricator_scan({DuckSql.Literal(token)}))");
        }
        finally
        {
            _host.ReleaseRows(token);
        }
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
