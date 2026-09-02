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
/// <b>⚠⚠ IT IS REFUSED IN <c>fluid_query</c>, AND THAT IS THE WHOLE SAFETY DESIGN.</b> A
/// <c>fluid_query</c> template is rendered during <c>bind_replace</c> — while DuckDB is BINDING — and a bind
/// REPEATS and happens WITHOUT execution. MEASURED (docs/fluid-templating.md §8.3): a bind-time write fires
/// on <c>EXPLAIN</c> of a statement that never runs (audit count 1), again on merely defining a VIEW over it
/// (2), and again on every USE of that view (3). A template that writes therefore belongs on a surface that
/// renders at EXECUTE time, which is <c>fabricator_render</c>.
/// </para>
/// <para>
/// <b>⚠ THE PERMISSION IS FAIL-CLOSED AND OPT-IN, which is the direction that matters.</b> A render must
/// ASK for exec (<see cref="FluidEngine.Render"/>'s <c>allowExec</c>, default <see langword="false"/>), so a
/// surface added later gets the SAFE answer by forgetting rather than the dangerous one. Keying on the
/// caller's NAME would have inverted that: an unrecognised name would read as "not fluid_query" and be
/// allowed.
/// </para>
/// <para>
/// ⚠ <b>The function is REGISTERED even where it is refused</b>, deliberately: a refusal that explains the
/// bind-time hazard and names the alternative is worth far more than an unknown-identifier error, which
/// would send the author hunting for a typo.
/// </para>
/// <para>
/// ⚠ <b>It grants no authority a template did not already have.</b> Anyone who can call
/// <c>fabricator_render</c> can already call <c>fabricator_host_exec</c> or <c>fabricator_exec</c>; this is
/// the same capability reached from inside a template. The precedent is <c>fabricator_http_request</c>,
/// which is ungated for the same reason (docs/http-transport.md).
/// </para>
/// <para>
/// ⚠ <b>PER-ROW EVALUATION IS REAL AND NOTHING PREVENTS IT.</b> <c>fabricator_render</c> is a scalar, so
/// rendering a template containing <c>exec()</c> over three rows performs the write THREE times. Volatility
/// keeps it out of PLAN time; it does not make it run once. Same rule as <c>fabricator_host_exec</c>'s
/// scalar form, and the same advice: for DDL, prefer a statement whose cardinality you chose.
/// </para>
/// </remarks>
internal static class FluidHostExec
{
    /// <summary>The name a template calls it by, as BOTH a function and a filter.</summary>
    internal const string FunctionName = "exec";

    /// <summary>
    /// The <see cref="TemplateContext.AmbientValues"/> key carrying whether this render may write.
    /// </summary>
    /// <remarks>
    /// ⚠ It is an AMBIENT rather than a captured variable because the FILTER form is registered ONCE on the
    /// shared <see cref="TemplateOptions"/> and cannot capture anything per render — the same reason
    /// <see cref="FluidHostQuery.CallerKey"/> exists.
    /// </remarks>
    internal const string AllowKey = "fabricator.allow_exec";

    /// <summary>Whether this render opted in. ABSENT means NO — the fail-closed direction.</summary>
    private static bool Allowed(TemplateContext ctx) =>
        ctx.AmbientValues.TryGetValue(AllowKey, out var v) && v is bool b && b;

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
        return Run(caller, args.At(0).ToStringValue(ctx), null, ctx);
    }

    /// <summary>
    /// The FILTER form — <c>sql | exec: id: 5</c> — whose NAMED arguments become the statement's <c>$id</c>
    /// parameters, bound as VALUES rather than spliced into the SQL.
    /// </summary>
    internal static ValueTask<FluidValue> Filter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var caller = CallerOf(ctx);
        return new(Run(caller, input.ToStringValue(ctx),
                       FluidHostQuery.BuildParameters(caller, FunctionName, args, ctx), ctx));
    }

    private static FluidValue Run(string caller, string? sql, RecordBatch? parameters, TemplateContext ctx)
    {
        if (!Allowed(ctx))
        {
            throw new InvalidOperationException(
                $"{caller}: {FunctionName}() is refused here. A {caller} template is rendered while DuckDB is "
                + "BINDING, and a bind repeats and happens without execution — so a write would fire on "
                + "EXPLAIN of a statement that never runs, and on merely defining a view over it. Use "
                + $"fabricator_render(...) for a template that writes, or {FluidHostQuery.FunctionName}() "
                + "for a SELECT.");
        }
        // ⚠ MEASURED for query() and true here for the same reason: an empty string reports NO error from
        // the classifier (it parses to zero statements), so the guard must come BEFORE it.
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException($"{caller}: {FunctionName}() was given an empty statement.");
        }

        var host = FabricatorServices.Get<IHostQuery>()
            ?? throw new InvalidOperationException(
                $"{caller}: {FunctionName}() needs the IHostQuery service, which is not published here. "
                + "It is available only from inside a fabricator function call.");

        RefuseIfSelect(caller, sql, host);

        // ⚠⚠ TWO PATHS, and the split is not arbitrary. Without parameters this goes through
        // IHostQuery.ExecuteNonQuery — the member built for exactly this and, until now, called by nothing
        // in tree. WITH parameters it cannot: the host's parameterised route is `Prepare`, which takes ONE
        // statement, and ExecuteNonQuery has no parameter overload; so the count is read here by the SAME
        // rule ExecuteNonQuery uses (first batch, column 0, Int64). The gate asserts the two AGREE on one
        // statement, because a rule written twice is a rule that can drift.
        long affected;
        if (parameters is null)
        {
            affected = host.ExecuteNonQuery(sql);
        }
        else
        {
            using (parameters)
            {
                using var stream = host.Query(sql, parameters);
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
    private static void RefuseIfSelect(string caller, string sql, IHostQuery host)
    {
        var (isSelect, msg) = FluidHostQuery.Classify(caller, FunctionName, sql, host);
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
