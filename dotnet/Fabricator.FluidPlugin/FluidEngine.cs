// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
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
    private static readonly FluidParser Parser = new(new FluidParserOptions { AllowFunctions = true });
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
        // ⚠⚠ The FILTER of the same name is registered ONCE, in the shared TemplateOptions (see
        // FluidValueModel.Build) — NOT here. `ctx.Options` IS that shared static, so registering per render
        // would mutate global state on every call and capture whichever `caller` happened to register last,
        // giving another render's function name in an error. The caller travels per context instead:
        ctx.AmbientValues[FluidHostQuery.CallerKey] = caller;
        bind(ctx);
        return parsed.Render(ctx);
    }
}
