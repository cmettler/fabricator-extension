// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Fabricator.Bridge;
using Fluid;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The Fluid <c>exec(sql)</c> function: runs a WRITE (DDL or DML) on the hosting DuckDB through
/// <see cref="IHostQuery"/> and hands the template the affected-row count. The write-side twin of
/// <see cref="FluidHostQuery"/>, and deliberately its mirror image — <c>query()</c> refuses everything that
/// is not a SELECT, <c>exec()</c> refuses everything that is.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ IT IS AVAILABLE ON BOTH SURFACES — USER DECISION (2026-09-02) — AND IN <c>fluid_query</c> IT WRITES
/// DURING BINDING, WHICH MULTIPLIES.</b> A <c>fluid_query</c> template is rendered during
/// <c>bind_replace</c>, and a bind REPEATS and happens WITHOUT execution. MEASURED, one audit table through
/// three steps that execute nothing the caller wrote: <c>EXPLAIN</c> of a never-run statement gives
/// <b>1</b>; merely defining a VIEW over it gives <b>2</b>; ONE <c>SELECT</c> from that view gives <b>3</b>.
/// <b>So a writing template behind a view writes ON EVERY USE</b> — which is the consequence to know,
/// because it works in testing, where the statement runs once.
/// </para>
/// <para>
/// ⚠ <b>An earlier build REFUSED this in <c>fluid_query</c> behind a fail-closed opt-in, and that mechanism
/// was DELETED rather than left defaulted-on</b> — with both surfaces permitting exec it would have been
/// vestigial machinery that reads as a restriction while restricting nothing. What justified removing it
/// beyond the decision: the refusal never made bind-time writes impossible, only inconvenient.
/// <c>query()</c> is permitted at bind BY DESIGN, any SELECT may CONTAIN a writing function, and
/// <c>SELECT fabricator_host_exec('INSERT …')</c> IS a SELECT — measured to write at bind time before
/// <c>exec()</c> existed at all (docs/fluid-templating.md §11.1a). To restore a restriction, the design is
/// recorded in §11.1: a per-render permission ambient, fail-closed, set by the surface.
/// </para>
/// <para>
/// ⚠ <b>For DDL, prefer <c>fluid_render</c> anyway</b> — not because <c>fluid_query</c> refuses, but
/// because a bind you did not ask for is a write you did not ask for.
/// </para>
/// <para>
/// ⚠ <b>It grants no authority a template did not already have.</b> Anyone who can call
/// <c>fluid_render</c> can already call <c>fabricator_host_exec</c> or <c>fabricator_exec</c>; this is
/// the same capability reached from inside a template. The precedent is <c>fabricator_http_request</c>,
/// which is ungated for the same reason (docs/http-transport.md).
/// </para>
/// <para>
/// ⚠ <b>PER-ROW EVALUATION IS REAL AND NOTHING PREVENTS IT.</b> <c>fluid_render</c> is a scalar, so
/// rendering a template containing <c>exec()</c> over three rows performs the write THREE times. Volatility
/// keeps it out of PLAN time; it does not make it run once. Same rule as <c>fabricator_host_exec</c>'s
/// scalar form, and the same advice: for DDL, prefer a statement whose cardinality you chose.
/// </para>
/// </remarks>
internal static class FluidHostExec
{
    /// <summary>The name a template calls it by, as BOTH a function and a filter.</summary>
    internal const string FunctionName = "exec";

    private static string CallerOf(TemplateContext ctx) =>
        ctx.AmbientValues.TryGetValue(FluidHostQuery.CallerKey, out var v) && v is string s ? s : FunctionName;

    /// <summary>The FUNCTION form — <c>exec(sql)</c>, no parameters.</summary>
    internal static FluidValue Execute(string caller, FunctionArguments args, TemplateContext ctx)
    {
        if (args.Count < 1)
        {
            throw new ArgumentException($"{caller}: {FunctionName}() takes one argument, the SQL to run.");
        }
        // ⚠ Named arguments cannot reach here — Fluid's grammar puts them on FILTERS only (measured; see
        // FluidHostQuery.Execute). The parameterised form is the filter below.
        return Run(caller, args.At(0).ToStringValue(ctx), null, FluidRenderSession.For(ctx));
    }

    /// <summary>
    /// The FILTER form — <c>sql | exec: id: 5</c> — whose NAMED arguments become the statement's <c>$id</c>
    /// parameters, bound as VALUES rather than spliced into the SQL.
    /// </summary>
    internal static ValueTask<FluidValue> Filter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var caller = CallerOf(ctx);
        return new(Run(caller, input.ToStringValue(ctx),
                       FluidHostQuery.BuildParameters(caller, FunctionName, args, ctx),
                       FluidRenderSession.For(ctx)));
    }

    // ⚠ `session` is THIS RENDER's pinned connection (ABI v84), the same one query() uses — which is the
    // whole point: a TEMP table created here is readable by a query() later in the same template. It
    // REPLACED an unused TemplateContext parameter, so no call site gained an argument.
    private static FluidValue Run(string caller, string? sql, RecordBatch? parameters,
                                  FluidRenderSession? session)
    {
        // ⚠ MEASURED for query() and true here for the same reason: an empty string reports NO error from
        // the classifier (it parses to zero statements), so the guard must come BEFORE it.
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException($"{caller}: {FunctionName}() was given an empty statement.");
        }

        var run = session
            ?? throw new InvalidOperationException(
                $"{caller}: {FunctionName}() needs the IHostQuery service, which is not published here. "
                + "It is available only from inside a fabricator function call.");

        RefuseIfSelect(caller, sql, run);

        // ⚠⚠ TWO PATHS, and the split is not arbitrary. Without parameters this goes through
        // IHostQuery.ExecuteNonQuery — the member built for exactly this and, until now, called by nothing
        // in tree. WITH parameters it cannot: the host's parameterised route is `Prepare`, which takes ONE
        // statement, and ExecuteNonQuery has no parameter overload; so the count is read here by the SAME
        // rule ExecuteNonQuery uses (first batch, column 0, Int64). The gate asserts the two AGREE on one
        // statement, because a rule written twice is a rule that can drift.
        long affected;
        if (parameters is null)
        {
            affected = run.ExecuteNonQuery(sql);
        }
        else
        {
            using (parameters)
            {
                using var stream = run.Query(sql, parameters);
                affected = CountOf(stream);
            }
        }
        return NumberValue.Create(affected);
    }

    private static long CountOf(Apache.Arrow.Ipc.IArrowArrayStream stream)
    {
        var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
        if (batch is null || batch.ColumnCount == 0 || batch.Length == 0)
        {
            return 0;
        }
        using (batch)
        {
            return batch.Column(0) is Int64Array c && c.GetValue(0) is long v ? v : 0;
        }
    }

    /// <summary>
    /// Refuses a SELECT, using <see cref="FluidHostQuery.Classify"/> — the same mechanism <c>query()</c>
    /// uses, with the opposite verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>This is not tidiness — without it <c>exec()</c> would report a plausible WRONG NUMBER.</b>
    /// <c>Host.ExecuteNonQuery</c> cannot ask DuckDB for a statement's <c>CHANGED_ROWS</c> classification
    /// (that lives C++-side), so it INFERS the count from the first column when that column is an Int64.
    /// MEASURED with the refusal removed: <c>exec('SELECT count(*) FROM range(99)')</c> renders <b>99</b> as
    /// though 99 rows had been affected. Refusing by name sends the author to <c>query()</c> instead of
    /// handing them a number that looks right.
    /// </para>
    /// <para>
    /// ⚠ <b>The trap is NARROWER than "any SELECT", and saying so matters because the narrow version is the
    /// LIKELY one.</b> Measured: <c>SELECT 42</c> renders <b>0</b> (an INT32 literal, so the Int64 test
    /// fails) and so does <c>SELECT 'x'</c> — while <c>SELECT 42::BIGINT</c> renders 42 and
    /// <c>count(*)</c> renders its count. An aggregate count is exactly what someone would reach for, so
    /// the shapes that misreport are the ones a user is most likely to write.
    /// </para>
    /// <para>
    /// ⚠ MEASURED that the shapes exec exists for pass: <c>INSERT</c>, <c>DELETE … $id</c> (placeholders
    /// tolerated), and <c>CREATE TABLE …; INSERT …</c> — several statements in one call, which is precisely
    /// what the no-parameter path buys over the parameterised one.
    /// </para>
    /// </remarks>
    /// <summary>The Liquid tag name of the BLOCK form: <c>{% exec %}…{% endexec %}</c>.</summary>
    internal const string BlockName = "exec";

    /// <summary>
    /// The BLOCK form's body, already rendered to text: classify it and run it, emitting NOTHING.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ It runs the SAME classifier and the SAME connection as the function form — one mechanism, so the
    /// two spellings cannot drift on what counts as a write or on which connection they use. What differs
    /// is only where the SQL comes from (a rendered body rather than a string argument) and that the count is
    /// DISCARDED, because a block renders nothing. Use the function form when you want the number.
    /// </para>
    /// </remarks>
    internal static void ExecuteCaptured(TemplateContext ctx, string sql, RecordBatch? parameters)
    {
        var caller = CallerOf(ctx);
        // ⚠ A whitespace-only body is refused BEFORE the classifier, for the reason the function form
        // documents: json_serialize_sql('') reports NO error (it parses to zero statements), so the
        // classifier alone would wave it through and we would "execute" nothing while reporting success.
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException(
                $"{caller}: {{% {BlockName} %}} block is empty — it rendered no SQL.");
        }

        // ⚠ Straight to Run, which owns the two execution paths (ExecuteNonQuery without parameters,
        // Query + a local count with them) and the SELECT refusal. The count it returns is DISCARDED —
        // a block renders nothing — but routing through Run is what keeps the block from acquiring its
        // own copy of the parameterised/unparameterised split.
        Run(caller, sql, parameters, FluidRenderSession.For(ctx));
    }

    private static void RefuseIfSelect(string caller, string sql, FluidRenderSession run)
    {
        var (isSelect, msg) = FluidHostQuery.Classify(caller, FunctionName, sql, run);
        if (!isSelect)
        {
            // ⚠ A SYNTAX ERROR also lands here and is surfaced rather than run. `msg` distinguishes the two
            // causes ("Only SELECT statements can be serialized to json!" vs `syntax error at or near …`),
            // and letting a statement DuckDB cannot parse through to execution would only fail again, later
            // and with less context.
            if (msg is not null && msg.Contains("syntax error", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{caller}: {FunctionName}() was given a statement DuckDB cannot parse: {msg}");
            }
            return;
        }

        throw new InvalidOperationException(
            $"{caller}: {FunctionName}() runs WRITES only, and this statement is a SELECT. Use "
            + $"{FluidHostQuery.FunctionName}() to read — {FunctionName}() would report its first column as "
            + "an affected-row count, which is a number that looks right and is not one.");
    }
}
