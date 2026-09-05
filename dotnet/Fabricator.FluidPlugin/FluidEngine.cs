// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Fluid.Ast;
using Fluid.Utils;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;
using System.Linq;
using Fluid;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The parser and the parsed-template cache, shared by every function in this plugin.
/// <para>Parse-once / render-many: a template is usually a constant literal across a batch (and, for
/// <c>fluid_query</c>, across every re-bind of a view or prepared statement), so the parsed, thread-safe
/// <see cref="IFluidTemplate"/> is cached by template text.</para>
/// </summary>
internal static class FluidEngine
{
    // ⚠ BOTH options below are OFF in Fluid by default and BOTH are PARSER-level gates, so a template using
    // either fails at PARSE with Fluid naming the option — not at render with a missing function or a
    // mis-evaluated condition. They are set HERE, where the parser is built, because templates are cached
    // by TEXT: one parsed before the option was set would stay cached, rejected, for the process's life.
    //
    // ⚠ Built by a METHOD rather than an object initializer so the custom tag is registered BEFORE anything
    // can be parsed: templates are cached by text, so a template parsed before registration would be cached
    // with `{% exec %}` unrecognised and stay that way for the process's life.
    private static readonly FluidParser Parser = CreateParser();

    // The buffer TextWriterFluidOutput allocates while capturing a block body. 16 KB is what Fluid's own
    // {% capture %} source generator uses; a longer body simply flushes through it.
    private const int CaptureBufferSize = 16 * 1024;

    private static FluidParser CreateParser()
    {
        var parser = new FabricatorFluidParser(new FluidParserOptions
        {
            AllowFunctions = true,
            // ⚠ Fluid refuses `{% if (a or b) and c %}` without this, naming the option in the parse error.
            // Liquid has no operator precedence — it evaluates strictly right to left — so grouping is the
            // ONLY way to express a mixed and/or condition, and a template that generates SQL is exactly
            // where such a condition turns up.
            AllowParentheses = true,
        });

        // ⚠⚠ THE {% exec %} BLOCK: render the body to a SEPARATE output, run the captured text as SQL, and
        // write NOTHING to the caller's output. It is what makes a real statement writable — multi-line,
        // with Liquid inside it, and no quote-escaping — where the exec("…") function needs the whole
        // statement as one escaped string argument.
        //
        // ⚠ NullEncoder.Default, EXPLICITLY, rather than the ambient `encoder`. The body is SQL and must
        // never be HTML-escaped — an encoder would turn `'` into `&#39;` and corrupt every literal. Today
        // the ambient encoder IS NullEncoder (Render(template, context) passes NullEncoder.Default), so
        // this changes nothing now; it is written explicitly so that stays true if a caller ever renders
        // through an encoding overload. Fluid's own {% capture %} passes the ambient encoder through, which
        // is right for HTML and wrong here.
        //
        // ⚠⚠ THE CAPTURE OUTPUT BUFFERS (CaptureBufferSize), so the body must be FLUSHED before it is
        // read — otherwise a statement shorter than the buffer has not reached the StringWriter at all and
        // we would "execute" the empty string. The text is therefore read INSIDE the scope, immediately
        // after the flush, which is also what Fluid's own {% capture %} source generator does.
        // ⚠ MEASURED: reading AFTER the `await using` instead works too, because DisposeAsync flushes —
        // and a mutant that dropped the explicit flush SURVIVED in that arrangement. That is exactly why
        // it is written this way: the dependency is real, and here it is explicit and local rather than
        // resting on disposal order, so removing the flush now fails loudly.
        // ⚠ A PARSER block rather than an EMPTY one, so the tag can carry OPTIONAL NAMED ARGUMENTS that
        // become BOUND parameters: `{% exec a: 1, b: 2 %}INSERT … VALUES ($a, $b){% endexec %}`. The
        // grammar is Fluid's OWN `ArgumentsList` (`name: value`, comma-separated) rather than one invented
        // here, so it reads like every other named-argument site in Liquid; `ZeroOrOne` is what makes it
        // optional, since ArgumentsList is `Separated(Comma, …)` and matches at least one.
        // ⚠⚠ THE IDENTIFIER IS OPTIONAL, AND THE NEGATIVE LOOKAHEAD IS WHAT MAKES THAT POSSIBLE.
        // `{% exec retcode %}` binds the affected-row count to `retcode`; `{% exec %}` and
        // `{% exec x: 7 %}` must keep working unchanged. Without the lookahead they cannot coexist: on
        // `{% exec x: 7 %}` a bare optional Ident matches `x`, ZeroOrOne then SUCCEEDS having consumed it,
        // and the `: 7` that follows is a parse error — ZeroOrOne does not retry its empty branch once the
        // sequence fails downstream. `Not(Terms.Char(':'))` consumes nothing and fails the identifier
        // branch exactly when the token is really the first NAMED ARGUMENT, so the two spellings separate
        // cleanly and `{% exec retcode x: 7 %}` reads like `{% query t x: 7 %}` — no comma, same shape.
        parser.RegisterParserBlock(
            FluidHostExec.BlockName,
            ZeroOrOne(parser.Ident.AndSkip(Not(Terms.Char(':')))).And(ZeroOrOne(parser.NamedArguments)),
            static async (head, statements, output, encoder, ctx) =>
        {
            var (completion, sql) = await CaptureBodyAsync(statements, ctx);
            if (completion != Completion.Normal)
            {
                return completion;
            }

            var parameters = await FluidHostQuery.BuildBlockParametersAsync(
                FluidHostQuery.CallerOf(ctx), FluidHostExec.BlockName, head.Item2, ctx);
            var affected = FluidHostExec.ExecuteCaptured(ctx, sql, parameters);
            // ⚠ Bound ONLY when a name was given, and bound as the VALUE ExecuteCaptured returns — the
            // same one the exec() FUNCTION yields, so the three spellings cannot report different numbers.
            // Without a name it is discarded, which is what `{% exec %}` has always done: a block renders
            // nothing and most callers want nothing back.
            if (!string.IsNullOrEmpty(head.Item1))
            {
                ctx.SetValue(head.Item1, affected);
            }
            return Completion.Normal;
        });

        // ⚠⚠ THE {% print %} BLOCK: {% query %} with the destination changed — the rows are RENDERED
        // instead of bound to a name. It routes through the SAME FluidHostQuery.RunCaptured, so the
        // classifier (SELECT only), the per-render pinned connection, the row cap and the value model are
        // one mechanism rather than a second copy free to drift.
        //
        // ⚠⚠ `delim` and `rowdelim` are RESERVED ARGUMENT NAMES, so a statement wanting a parameter called
        // either cannot get one. Accepted because it fails LOUDLY: DuckDB names the parameter it was not
        // given. ⚠ The request's `delim := " "` spelling is not expressible — Fluid's grammar is
        // `name: value` — and inventing one would be a grammar only this plugin speaks, which is the same
        // reason ArgumentsList was reused for the other two blocks.
        //
        // ⚠ No IDENTIFIER, unlike {% query name %} and {% exec name %}: there is nothing to bind, so the
        // header is arguments-only and needs none of the lookahead the exec block does.
        parser.RegisterParserBlock(FluidHostPrint.BlockName, ZeroOrOne(parser.NamedArguments),
                                   static async (args, statements, output, encoder, ctx) =>
        {
            var (completion, sql) = await CaptureBodyAsync(statements, ctx);
            if (completion != Completion.Normal)
            {
                return completion;
            }

            var (delim, rowDelim, sqlLiteral, rest) = await FluidHostPrint.SplitOptionsAsync(args, ctx);
            var parameters = await FluidHostQuery.BuildBlockParametersAsync(
                FluidHostQuery.CallerOf(ctx), FluidHostPrint.BlockName, rest, ctx);
            var rows = FluidHostQuery.RunCaptured(ctx, FluidHostPrint.BlockName, sql, parameters);
            await FluidHostPrint.WriteRowsAsync(rows, output, encoder, ctx, delim, rowDelim, sqlLiteral);
            return Completion.Normal;
        });

        // ⚠⚠ THE {% query name %} BLOCK: the same capture, but the captured text is RUN AS A QUERY and its
        // ROW SET is bound to `name` — not a rendered string. It is `{% capture %}`'s shape (an IDENTIFIER
        // block) because that is Liquid's own precedent for "run this block and bind the result to a name",
        // and because the value is what the caller wanted: `{{ result[0].a }}` works exactly as it does
        // after the query() FUNCTION, since both go through FluidHostQuery.Run and get the same
        // ArrayValue of indexable rows.
        //
        // ⚠ `{% assign result = query %}…{% endquery %}` is NOT expressible in Liquid and this is the
        // nearest thing: `assign` parses `identifier = EXPRESSION` and terminates at `%}`, so a block body
        // can never be its operand. `{% query result %}` reads the same way and is one tag rather than two.
        // ⚠ The identifier is followed by OPTIONAL NAMED ARGUMENTS, which become BOUND parameters:
        // `{% query t region: 'eu' %}SELECT … WHERE region = $region{% endquery %}`. Composed from Fluid's
        // own `Identifier` and `ArgumentsList`, both `protected` on FluidParser — hence the subclass below.
        parser.RegisterParserBlock(FluidHostQuery.BlockName,
                                   parser.Ident.And(ZeroOrOne(parser.NamedArguments)),
                                   static async (head, statements, output, encoder, ctx) =>
        {
            var (completion, sql) = await CaptureBodyAsync(statements, ctx);
            if (completion != Completion.Normal)
            {
                return completion;
            }

            var parameters = await FluidHostQuery.BuildBlockParametersAsync(
                FluidHostQuery.CallerOf(ctx), FluidHostQuery.BlockName, head.Item2, ctx);
            // ⚠ SetValue, and NOTHING is written to `output` — the block contributes no text, exactly like
            // {% capture %}. The rows are held for the render, so a template may iterate them repeatedly.
            ctx.SetValue(head.Item1, FluidHostQuery.RunCaptured(ctx, FluidHostQuery.BlockName, sql, parameters));
            return Completion.Normal;
        });

        // ⚠⚠ {% ret %} — Scriban's early exit, which Liquid does not have and Fluid cannot express through
        // its Completion type (see FluidEarlyReturn for the measurement that settles it). It is an EMPTY
        // tag: our templates have no functions, so there is nothing for a return VALUE to mean.
        //
        // ⚠ It throws and does NOT flush. The catch site owns the output and flushes there, where the flush
        // is necessary; a flush here as well would be the redundant one — which is the shape that let an
        // earlier {% exec %} mutant survive.
        parser.RegisterEmptyTag(RetTagName, static (output, encoder, ctx) => throw new FluidEarlyReturn());

        return parser;
    }
    private static readonly ConcurrentDictionary<string, IFluidTemplate> Cache = new();

    /// <summary>
    /// Renders a custom block's body to TEXT — the SQL both {% exec %} and {% query %} run.
    /// </summary>
    /// <returns>The body's completion, and the captured text (empty unless the completion is Normal).</returns>
    /// <remarks>
    /// <para>
    /// ⚠⚠ ONE COPY, and that is the point rather than tidiness: the two subtleties below were each got
    /// wrong once, and a second copy is where they come back.
    /// </para>
    /// <para>
    /// ⚠⚠ THE CAPTURE OUTPUT BUFFERS, so the body must be FLUSHED before it is read — otherwise a
    /// statement shorter than CaptureBufferSize has not reached the StringWriter at all and the caller
    /// "executes" the empty string. The text is therefore read INSIDE the scope, right after the flush,
    /// which is what Fluid's own {% capture %} source generator does. MEASURED: reading AFTER the
    /// `await using` works too, because DisposeAsync flushes — and a mutant that dropped the explicit
    /// flush SURVIVED in that arrangement. Written this way so removing the flush fails loudly.
    /// </para>
    /// <para>
    /// ⚠ NullEncoder.Default EXPLICITLY, not the ambient encoder: the body is SQL and must never be
    /// HTML-escaped, since an encoder turns <c>'</c> into <c>&amp;#39;</c> and corrupts every literal.
    /// Today the ambient encoder IS NullEncoder, so this changes nothing now; it is explicit so that stays
    /// true if a caller ever renders through an encoding overload. Fluid's own {% capture %} passes the
    /// ambient encoder through, which is right for HTML and wrong here.
    /// </para>
    /// <para>
    /// ⚠ A body that did not finish normally yields its completion and NO text, so the caller runs nothing:
    /// a `{% break %}` inside the block (of an enclosing {% for %}) leaves a PARTIALLY rendered statement,
    /// and half a statement is a different statement.
    /// </para>
    /// </remarks>
    private static async ValueTask<(Completion Completion, string Sql)> CaptureBodyAsync(
        IReadOnlyList<Statement> statements, TemplateContext ctx)
    {
        using var sql = new StringWriter();
        var completion = Completion.Normal;
        await using var capture = new TextWriterFluidOutput(sql, CaptureBufferSize, leaveOpen: true);
        for (var i = 0; i < statements.Count; i++)
        {
            completion = await statements[i].WriteToAsync(capture, NullEncoder.Default, ctx);
            if (completion != Completion.Normal)
            {
                return (completion, string.Empty);
            }
        }

        await capture.FlushAsync();
        return (Completion.Normal, sql.ToString());
    }

    /// <summary>Parses (or reuses) <paramref name="template"/> and renders it over a fresh context.</summary>
    /// <param name="caller">The SQL function name, so a parse error names the function the user called.</param>
    /// <param name="publishRefusal">Null = <c>publish()</c> is allowed; otherwise the message it throws.
    /// ⚠ REQUIRED, with no default, DELIBERATELY: a policy that defaults to "allowed" makes a surface added
    /// later inherit the permissive answer silently, which is exactly the trap docs/fluid-templating.md §11.1
    /// records about deriving it from the caller's NAME. Every surface states its own.</param>
    internal static string Render(string caller, string template, Action<TemplateContext> bind,
                                  string? publishRefusal)
    {
        // ⚠⚠ ONE PINNED DuckDB CONNECTION PER RENDER (ABI v84), so this template's exec() and query() see
        // each other — a `CREATE TEMP TABLE` in one and a `SELECT` from it in the other. Created here
        // rather than in either function so the two share it, LAZILY (nothing is opened until the first
        // call), and disposed with the render so the temporary catalog dies with it. See FluidRenderSession
        // for why per-render is also what makes it thread-safe.
        using var session = FluidRenderSession.TryCreate();
        var ctx = NewRenderContext(caller, publishRefusal, session, bind);
        return RenderOn(caller, template, ctx);
    }

    /// <summary>
    /// Builds the context a render runs over: the three host functions, the caller key, the publish policy
    /// and the session.
    /// </summary>
    /// <remarks>
    /// ⚠ Separated from <see cref="Render"/> for <c>fluid_query_batch</c>, which renders MANY times over one
    /// context and one session, so the per-context setup — the params bag, the three host functions — happens
    /// once. Every other surface wants exactly one render per context and uses <see cref="Render"/>.
    /// <para>⚠ Sharing a context does NOT share Liquid VARIABLES between renders: Fluid renders into a child
    /// scope and pops it, so a <c>{% assign %}</c> does not survive (MEASURED, after this comment first
    /// claimed it did). What carries between renders is the SESSION's SQL state — a temp table.</para>
    /// </remarks>
    internal static TemplateContext NewRenderContext(string caller, string? publishRefusal,
                                                     FluidRenderSession? session, Action<TemplateContext> bind)
    {
        var ctx = FluidValueModel.NewContext();
        // ⚠ Registered PER CONTEXT, and `caller` is captured so a refusal names the SQL function the user
        // actually called rather than the template machinery.
        ctx.SetValue(FluidHostQuery.FunctionName,
                     new FunctionValue((args, c) => FluidHostQuery.Execute(caller, args, c)));
        // ⚠ Available on BOTH surfaces by user decision. In fluid_query it therefore writes during BINDING,
        // which repeats — see FluidHostExec for the measured 1 -> 2 -> 3.
        ctx.SetValue(FluidHostExec.FunctionName,
                     new FunctionValue((args, c) => FluidHostExec.Execute(caller, args, c)));
        // ⚠ publish() RENDERS SQL (a fabricator_scan call), so it is only meaningful where the render IS a
        // statement — i.e. in fluid_query. Registered on both surfaces anyway: branching on the caller name
        // is what the exec() decision rejected, and in fluid_render the result is merely inert text.
        ctx.SetValue(FluidHostPublish.FunctionName,
                     new FunctionValue((args, c) => FluidHostPublish.Execute(caller, args, c)));
        // ⚠⚠ The FILTER of the same name is registered ONCE, in the shared TemplateOptions (see
        // FluidValueModel.Build) — NOT here. `ctx.Options` IS that shared static, so registering per render
        // would mutate global state on every call and capture whichever `caller` happened to register last,
        // giving another render's function name in an error. The caller travels per context instead:
        ctx.AmbientValues[FluidHostQuery.CallerKey] = caller;
        if (publishRefusal is not null)
        {
            ctx.AmbientValues[FluidHostPublish.RefusalKey] = publishRefusal;
        }
        if (session is not null)
        {
            ctx.AmbientValues[FluidRenderSession.Key] = session;
        }
        bind(ctx);
        return ctx;
    }

    /// <summary>Parses (or reuses) <paramref name="template"/> and renders it over an EXISTING context.</summary>
    internal static string RenderOn(string caller, string template, TemplateContext ctx)
    {
        var parsed = Cache.GetOrAdd(template, src =>
        {
            if (!Parser.TryParse(src, out var t, out var error))
            {
                throw new ArgumentException($"{caller}: template parse error: {error}");
            }
            return t;
        });
        try
        {
            // ⚠ Sync-over-async, blocking ONCE at the wrapper — the convention this codebase adopted for
            // every sync ABI surface over an async core.
            return RenderToStringAsync(parsed, ctx).AsTask().GetAwaiter().GetResult();
        }
        catch (FileNotFoundException ex)
        {
            // ⚠ Fluid raises this carrying ONLY the include's argument, which reads as though the template
            // engine itself were missing a file. Naming the function and the root is what turns it into
            // something an author can act on.
            // ⚠ BOTH KEYS, because Fluid probes twice: `{% include 'a' %}` asks the provider for `a` and
            // then for `a.liquid`, i.e. two different subpaths, while its exception names only the first.
            // Looking under both is what puts the whole probe pair in the message.
            // ⚠ Claiming ABSENCE here is legitimate, unlike almost everywhere else in this repo: the read
            // goes through read_blob, which returns ZERO ROWS for a file that is not there and THROWS for
            // anything else — so a credential or transport failure never arrives here at all, it arrives as
            // its own error. Absence is established by the engine, not inferred from a failure.
            throw new FileNotFoundException(
                $"{caller}: template include '{ex.Message}' was not found; tried "
                + string.Join(", ", HostTemplateFileProvider.TriedFor(ctx, ex.Message)
                                       .Concat(HostTemplateFileProvider.TriedFor(ctx, ex.Message + ".liquid"))),
                ex);
        }
    }

    /// <summary>The template variable naming the schema-probe render — see <c>fluid_query_batch</c>.</summary>
    internal const string IsBindVariable = "is_bind";

    /// <summary>The tag that ends a render early — Scriban's <c>ret</c>, which Liquid has no equivalent of.</summary>
    internal const string RetTagName = "ret";

    /// <summary>
    /// Renders <paramref name="parsed"/> to a string, stopping at a <c>{% ret %}</c> and keeping whatever
    /// had been written by then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>THIS EXISTS SO WE OWN THE OUTPUT.</b> Fluid's own <c>Render(ctx)</c> extension builds the
    /// <c>StringWriter</c> and the <see cref="TextWriterFluidOutput"/> INSIDE itself, so a
    /// <see cref="FluidEarlyReturn"/> caught around it would leave the text where nothing can reach it.
    /// Everything else here — the child scope, the encoder, the buffer size — is that extension's
    /// behaviour reproduced, because this replaces it rather than wrapping it.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>THE CHILD SCOPE IS NOT DECORATION.</b> It is what makes a <c>{% assign %}</c> NOT survive a
    /// render, which <c>fluid_query_batch</c> measures (three groups, a per-group counter reading 1, 1, 1)
    /// and its gate pins. Dropping <c>EnterChildScope</c>/<c>ReleaseScope</c> here would silently start
    /// carrying Liquid state between the renders that share one context.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>THE FLUSH BELONGS TO THE <c>{% ret %}</c> PATH SPECIFICALLY, which is sharper than "the output
    /// buffers".</b> <c>FluidTemplate.RenderAsync</c> ends with a flush of its own, so an ordinary render
    /// needs nothing from us — the EXCEPTION is what skips it, leaving whatever the tag had written sitting
    /// in the pooled buffer. MEASURED: a mutant that drops this flush passes 563 assertions and dies at the
    /// FIRST <c>{% ret %}</c> one, which is what shows the flush serves that path and only that path.
    /// ⚠ Reading AFTER the <c>await using</c> would work too, because <c>DisposeAsync</c> flushes — and
    /// that is exactly what made an earlier mutant of the <c>{% exec %}</c> flush SURVIVE. Written this way
    /// the dependency is explicit and local, and removing it fails loudly.
    /// </para>
    /// <para>
    /// ⚠ Note what does NOT flush: the tag itself. The design note this was built from asserted that
    /// <c>{% ret %}</c> "must flush before throwing, because the catch site cannot" — FALSE at this pin,
    /// for the same disposal behaviour recorded above. One flush, here, where it is necessary.
    /// </para>
    /// </remarks>
    private static async ValueTask<string> RenderToStringAsync(IFluidTemplate parsed, TemplateContext ctx)
    {
        // A template is evaluated in a child scope so the caller's context stays immutable.
        ctx.EnterChildScope();
        try
        {
            using var text = new StringWriter();
            var bufferSize = ctx.Options?.OutputBufferSize ?? 0;
            if (bufferSize <= 0)
            {
                bufferSize = CaptureBufferSize;
            }
            await using var output = new TextWriterFluidOutput(
                text, bufferSize, ctx.CancellationToken, leaveOpen: true);
            try
            {
                // ⚠ NullEncoder.Default, explicitly and for the same reason the {% exec %} capture gives:
                // what a template here renders is SQL or plain text, never HTML, and an encoder would turn
                // every `'` into `&#39;`.
                await parsed.RenderAsync(output, NullEncoder.Default, ctx);
            }
            catch (FluidEarlyReturn)
            {
                // {% ret %}: everything written before it stands, everything after it never ran.
            }
            await output.FlushAsync();
            return text.ToString();
        }
        finally
        {
            ctx.ReleaseScope();
        }
    }
}

/// <summary>
/// Thrown by <c>{% ret %}</c> and caught by <see cref="FluidEngine.RenderToStringAsync"/> — never seen by a
/// caller, and carrying no state.
/// </summary>
/// <remarks>
/// ⚠⚠ <b>AN EXCEPTION IS THE ONLY MECHANISM THAT WORKS, and the alternative was measured rather than
/// assumed.</b> Liquid's <c>Completion</c> has three values — Normal, Break, Continue — and
/// <c>FluidTemplate.RenderAsync</c> AWAITS each statement's completion and never inspects it. MEASURED at
/// this pin: <c>A{% break %}B</c> renders <b>AB</b>, and so does <c>A{% if go %}{% break %}{% endif %}B</c>;
/// only a <c>{% for %}</c> consumes a Break at all. So a completion-based <c>{% ret %}</c> would be silently
/// ignored at the top level, which is precisely where it is wanted.
/// <para>⚠ The other candidate — an output wrapper that discards writes after the tag fires — was rejected
/// on the merits: it produces the same TEXT while every statement after the tag still RUNS, so an
/// <c>{% exec %}</c> or a <c>{% query %}</c> below a <c>{% ret %}</c> would still hit the database and a
/// <c>{% for %}</c> would still spin. "Stop" has to mean stop.</para>
/// <para>⚠ Nothing in Fluid swallows it on the way out: the pinned source has exactly two
/// <c>catch (Exception)</c> sites, both in <c>FilterExpression</c>, and both re-fault the task rather than
/// absorbing it — and a tag is a statement, so it is not evaluated inside a filter anyway.</para>
/// </remarks>
internal sealed class FluidEarlyReturn : Exception
{
    internal FluidEarlyReturn()
        : base("{% ret %} ended the render (internal signal; it should never reach a caller).")
    {
    }
}

/// <summary>
/// <see cref="FluidParser"/> with the two grammar pieces a custom BLOCK needs exposed.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>Identifier</c> and <c>ArgumentsList</c> are <c>protected readonly</c> on <see cref="FluidParser"/>,
/// so a subclass is the only way to compose them into a custom block's header. That is the documented
/// approach (deanebarker.net/tech/fluid/parser-tags-blocks) — ⚠ though that article is out of date against
/// our pinned 3.0.0-beta.7 in two ways: the block registration is <c>RegisterParserBlock</c>, not
/// <c>RegisterTagBlock</c>, and <c>ArgumentsList</c> is an <c>IReadOnlyList</c>, not a <c>List</c>.
/// </para>
/// <para>
/// ⚠ Re-using Fluid's OWN <c>ArgumentsList</c> rather than inventing a grammar is deliberate: named
/// arguments then look the same in a block as everywhere else in Liquid (<c>name: value</c>, comma
/// separated), and the parsing of a value is Fluid's, not ours. <c>LogicalExpression</c> is also exposed
/// and would allow a bespoke separator-free form; that would be a grammar only this plugin speaks.
/// </para>
/// </remarks>
internal sealed class FabricatorFluidParser : FluidParser
{
    internal FabricatorFluidParser(FluidParserOptions options) : base(options)
    {
    }

    /// <summary>Fluid's identifier parser — the <c>t</c> in <c>{% query t %}</c>.</summary>
    internal Parser<string> Ident => Identifier;

    /// <summary>Fluid's named-argument list — <c>a: 1, b: 2</c>. Matches at least one; wrap in ZeroOrOne.</summary>
    internal Parser<IReadOnlyList<FilterArgument>> NamedArguments => ArgumentsList;
}
