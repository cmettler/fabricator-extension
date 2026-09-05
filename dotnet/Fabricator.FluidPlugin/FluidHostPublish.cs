// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Fabricator.Bridge;
using Fluid;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The Fluid <c>publish(name)</c> function: hands a table the template STAGED on its own pinned connection
/// to the SQL the template is generating, by rendering the scan that reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pairing it exists for</b> — the intermediate is named once, computed once, and becomes the
/// statement's relation:
/// </para>
/// <code>
/// {% exec lo: 1, hi: 5 %}
///   CREATE TEMP TABLE _result AS SELECT i AS n, i * i AS sq FROM range($lo, $hi) t(i)
/// {% endexec %}
/// SELECT * FROM {{ publish('_result') }}
/// </code>
/// <para>
/// <b>⚠⚠ NOTHING ELSE CAN CARRY THAT RELATION, AND THE TWO OBVIOUS ROUTES ARE MEASURED SHUT.</b> A TEMP
/// table belongs to the ClientContext that made it, so the caller cannot see it at all; and a REAL table
/// created during <c>bind_replace</c> is invisible to the statement being bound, because that statement's
/// catalog snapshot predates the commit — measured, and the error names the table it just created
/// (docs/fluid-templating.md §11.1b). ⚠ An ATTACHed catalog the transaction has not yet touched appears to
/// work and is a TIMING ARTEFACT of DuckDB's lazy per-catalog transaction start — one earlier read of that
/// catalog makes the identical statement fail (§11.1b-i). A named Arrow source sidesteps all of it: it is
/// read by a TABLE FUNCTION, which asks the caller's catalog nothing.
/// </para>
/// <para>
/// <b>⚠ IT RENDERS SQL, so it must be interpolated RAW</b> — <c>{{ publish('t') }}</c>, never
/// <c>{{ publish('t') | sql }}</c>, which would quote the whole scan into a string literal. That is the
/// same rule <c>fluid_query</c> already documents for every <c>{{ }}</c>: interpolation is raw because a
/// template must be able to emit fragments, and DATA goes through the <c>sql</c> filters.
/// </para>
/// <para>
/// <b>⚠ SINGLE-USE, failing LOUDLY.</b> One publication is scanned once; a second scan of the same token
/// says so and tells you to publish again, rather than returning zero rows. So
/// <c>{% assign p = publish('t') %}</c> used twice is an ERROR by design — call <c>publish</c> per
/// reference; each is an independent statement, run when its own scan pulls.
/// </para>
/// <para>
/// <b>⚠ LAZY: the staged relation STREAMS at scan time and there is no row cap</b> — the publication
/// registers a statement, not rows, and the scan's own disposal releases it. ⚠ The cost is on the other
/// side: a publication that is never scanned (an <c>EXPLAIN</c> renders the template without running it)
/// holds the render's connection — and the staged table with it — until an eviction cap reclaims it. So
/// publish in the statement that scans it.
/// </para>
/// <para>
/// ⚠ For a SINGLE query a <c>WITH … AS MATERIALIZED</c> CTE inside the generated SQL is still the better
/// answer — it needs no publication at all and keeps the whole relation inside one DuckDB plan. Gated as
/// the alternative, so the choice is not folklore. <c>publish</c> is for a relation computed in SEVERAL
/// steps, which a CTE cannot express.
/// </para>
/// <para>
/// ⚠ <b>Available on BOTH surfaces, and pointless on one of them.</b> <c>fluid_render</c> produces TEXT that
/// nobody binds, so a publication made there is never scanned and is eventually evicted — and being a
/// PER-ROW scalar, it would publish once per row. Not refused: the recorded lesson from <c>exec()</c> is
/// that a mechanism which reads as a restriction while restricting nothing is worse than a documented
/// footgun, and the caller name is the wrong thing to branch on.
/// </para>
/// <para>
/// ⚠ <b>ONE identifier, quoted.</b> The name is passed through <see cref="DuckSql.QuoteIdent"/>, so
/// <c>publish('my table')</c> works and injection is not expressible — but a DOTTED name is one identifier,
/// not a qualified one, and fails as "table does not exist". Stage into a plain name; the general form is
/// the follow-on <c>publish</c>-a-query surface recorded in docs/fluid-templating.md §18.
/// </para>
/// </remarks>
internal static class FluidHostPublish
{
    /// <summary>The name a template calls it by.</summary>
    internal const string FunctionName = "publish";

    /// <summary>The host table function a publication is scanned through.</summary>
    private const string ScanFunction = "fabricator_scan";

    /// <summary>
    /// <see cref="TemplateContext.AmbientValues"/> key carrying a surface's refusal message. Present ⇒
    /// <c>publish()</c> throws it; absent ⇒ allowed.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ It exists for a surface that runs the rendered statement ITSELF, on the render's own pinned
    /// connection — where a publication is not merely pointless but DEADLOCKS. MEASURED: the scan invokes
    /// the publication's factory, which opens a second query on the connection already executing the first,
    /// and the process HANGS rather than raising the one-live-result refusal. A hang is worse than an
    /// error, which is why this is refused rather than documented.
    /// </remarks>
    internal const string RefusalKey = "fabricator.publish_refused";

    internal static FluidValue Execute(string caller, FunctionArguments args, TemplateContext ctx)
    {
        if (ctx.AmbientValues.TryGetValue(RefusalKey, out var refusal) && refusal is string reason)
        {
            throw new InvalidOperationException($"{caller}: {reason}");
        }
        if (args.Count < 1)
        {
            throw new ArgumentException(
                $"{caller}: {FunctionName}() takes one argument, the name of a table this template staged "
                + "(for example with {% exec %}CREATE TEMP TABLE …{% endexec %}).");
        }
        var name = args.At(0).ToStringValue(ctx);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"{caller}: {FunctionName}() was given an empty table name.");
        }

        var session = FluidRenderSession.For(ctx)
            ?? throw new InvalidOperationException(
                $"{caller}: {FunctionName}() needs the hosting DuckDB, which is not available here.");

        // ⚠ The SELECT is OURS, built from a quoted identifier — so the published relation is exactly the
        // staged table and there is nothing for a caller to inject. The host registers it and returns a token.
        var token = session.Publish($"SELECT * FROM {DuckSql.QuoteIdent(name)}");
        return new StringValue($"{ScanFunction}({DuckSql.Literal(token)})");
    }
}
