// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.SamplePlugin.Support;

/// <summary>
/// The one thing this fixture assembly provides: a value the sample plugin surfaces as
/// <c>plug_support()</c>, so a gate assertion FAILS if this assembly did not ship beside the plugin.
/// </summary>
public static class SampleSupport
{
    /// <summary>
    /// The marker <c>plug_support()</c> returns.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ A METHOD, AND NOT A <c>const</c>, AND THAT IS THE WHOLE FIXTURE. C# bakes a <c>const</c> into the
    /// CALLER's IL at compile time, so a plugin returning a <c>const</c> from here would render correctly
    /// with this assembly ABSENT — the test would pass while proving nothing, which is the exact failure
    /// mode it exists to detect. A <c>static readonly</c> field would do as well; a method is simply the
    /// hardest form to accidentally turn back into a constant.
    /// </remarks>
    public static string Marker() => "sample support present";
}
