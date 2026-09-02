// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Reflection;

namespace Fabricator.Bridge;

/// <summary>
/// The version of the CONTRACT — <c>Fabricator.Abstractions</c> — that is actually running.
/// </summary>
/// <remarks>
/// <para>
/// It reads its own assembly's <see cref="AssemblyInformationalVersionAttribute"/>, which the build stamps
/// from the repo's single <c>VERSION</c> file. ⚠ <b>Deliberately NOT
/// <c>Assembly.GetName().Version</c></b>: the assembly VERSION is pinned at <c>1.0.0.0</c> on purpose, so
/// that a plugin's reference keeps binding to whichever copy the host already loaded (see
/// <c>dotnet/Directory.Build.props</c>). The number that MOVES is this one, and it is DIAGNOSTIC — nothing
/// resolves, loads or refuses on it.
/// </para>
/// <para>
/// ⚠ A plugin calling this at runtime gets the HOST's version, never the one it compiled against — the
/// contract assembly is loaded once, in the bridge's load context. That asymmetry is the point: comparing
/// the two is what detects skew, and the "built against" half has to come from somewhere the compiler
/// recorded it, which is the plugin manifest's <c>abstractionsVersion</c>.
/// </para>
/// </remarks>
public static class FabricatorVersion
{
    /// <summary>
    /// The running contract version, e.g. <c>"0.0.13"</c>. Never null; <c>""</c> only if the assembly was
    /// built without the attribute, which no build here produces.
    /// </summary>
    /// <remarks>
    /// ⚠ The build appends the commit sha (<c>0.0.13+3e74a07…</c>) — the SDK does that by default and it is
    /// worth keeping, since it makes an informal build identifiable — so this TRIMS at the <c>+</c>. A
    /// caller comparing versions must compare what a plugin's manifest can also record, and a manifest
    /// written by a different checkout would carry a different sha for the same contract.
    /// </remarks>
    public static string Contract { get; } = Read();

    private static string Read()
    {
        var raw = typeof(FabricatorVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        int plus = raw.IndexOf('+');
        return plus >= 0 ? raw.Substring(0, plus) : raw;
    }
}
