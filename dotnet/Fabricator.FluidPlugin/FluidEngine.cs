// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Fluid;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The parser and the parsed-template cache, shared by every function in this plugin.
/// <para>Parse-once / render-many: a template is usually a constant literal across a batch (and, for
/// <c>fluid_query</c>, across every re-bind of a view or prepared statement), so the parsed, thread-safe
/// <see cref="IFluidTemplate"/> is cached by template text.</para>
/// </summary>
internal static class FluidEngine
{
    private static readonly FluidParser Parser = new();
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
        bind(ctx);
        return parsed.Render(ctx);
    }
}
