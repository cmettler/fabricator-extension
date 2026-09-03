// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Fluid.Ast;
using Fluid.Utils;
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
        var parser = new FluidParser(new FluidParserOptions { AllowFunctions = true });

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
        parser.RegisterEmptyBlock(FluidHostExec.BlockName, static async (statements, output, encoder, ctx) =>
        {
            using var sql = new StringWriter();
            var completion = Completion.Normal;
            await using var capture = new TextWriterFluidOutput(sql, CaptureBufferSize, leaveOpen: true);
            for (var i = 0; i < statements.Count; i++)
            {
                completion = await statements[i].WriteToAsync(capture, NullEncoder.Default, ctx);
                if (completion != Completion.Normal)
                {
                    break;
                }
            }

            // ⚠ A body that did not finish normally is NOT executed — a `{% break %}` inside the block (of
            // an enclosing {% for %}) leaves a PARTIALLY rendered statement, and running half a statement
            // is a different statement. Propagate the completion instead, which is what the author asked
            // for by breaking.
            if (completion != Completion.Normal)
            {
                return completion;
            }

            await capture.FlushAsync();
            FluidHostExec.ExecuteCaptured(ctx, sql.ToString());
            return Completion.Normal;
        });

        return parser;
    }
    private static readonly ConcurrentDictionary<string, IFluidTemplate> Cache = new();

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
