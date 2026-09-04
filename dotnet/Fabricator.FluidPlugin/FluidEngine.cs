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
    // ⚠ AllowFunctions is OFF in Fluid by default and is a PARSER-level gate: without it `query('…')` is a
    // PARSE error ("Functions are not allowed"), not a missing-function error at render. It is enabled
    // because slice 3 ships `query`; nothing else in this plugin needs it.
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
        var parser = new FabricatorFluidParser(new FluidParserOptions { AllowFunctions = true });

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

            var (delim, rowDelim, rest) = await FluidHostPrint.SplitOptionsAsync(args, ctx);
            var parameters = await FluidHostQuery.BuildBlockParametersAsync(
                FluidHostQuery.CallerOf(ctx), FluidHostPrint.BlockName, rest, ctx);
            var rows = FluidHostQuery.RunCaptured(ctx, FluidHostPrint.BlockName, sql, parameters);
            await FluidHostPrint.WriteRowsAsync(rows, output, encoder, ctx, delim, rowDelim);
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
    internal static string Render(string caller, string template, Action<TemplateContext> bind)
    {
        var parsed = Cache.GetOrAdd(template, src =>
        {
            if (!Parser.TryParse(src, out var t, out var error))
            {
                throw new ArgumentException($"{caller}: template parse error: {error}");
            }
            return t;
        });
        var ctx = FluidValueModel.NewContext();
        // ⚠ Registered PER CONTEXT, and `caller` is captured so a refusal names the SQL function the user
        // actually called rather than the template machinery.
        ctx.SetValue(FluidHostQuery.FunctionName,
                     new FunctionValue((args, c) => FluidHostQuery.Execute(caller, args, c)));
        // ⚠ Available on BOTH surfaces by user decision. In fluid_query it therefore writes during BINDING,
        // which repeats — see FluidHostExec for the measured 1 -> 2 -> 3.
        ctx.SetValue(FluidHostExec.FunctionName,
                     new FunctionValue((args, c) => FluidHostExec.Execute(caller, args, c)));
        // ⚠⚠ The FILTER of the same name is registered ONCE, in the shared TemplateOptions (see
        // FluidValueModel.Build) — NOT here. `ctx.Options` IS that shared static, so registering per render
        // would mutate global state on every call and capture whichever `caller` happened to register last,
        // giving another render's function name in an error. The caller travels per context instead:
        ctx.AmbientValues[FluidHostQuery.CallerKey] = caller;
        // ⚠⚠ ONE PINNED DuckDB CONNECTION PER RENDER (ABI v84), so this template's exec() and query() see
        // each other — a `CREATE TEMP TABLE` in one and a `SELECT` from it in the other. Created here
        // rather than in either function so the two share it, LAZILY (nothing is opened until the first
        // call), and disposed below so the temporary catalog dies with the render. See FluidRenderSession
        // for why per-render is also what makes it thread-safe.
        using var session = FluidRenderSession.TryCreate();
        if (session is not null)
        {
            ctx.AmbientValues[FluidRenderSession.Key] = session;
        }
        bind(ctx);
        try
        {
            return parsed.Render(ctx);
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
