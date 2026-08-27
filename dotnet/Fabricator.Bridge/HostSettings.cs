// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;

namespace Fabricator.Bridge;

/// <summary>
/// Settings declared by the HOST rather than by a provider — the same gap <see cref="HostGlobalFunctions"/>
/// fills for functions, and for the same reason: a switch that governs the plugin machinery cannot belong to
/// a provider, because it has to exist when no provider has loaded.
/// </summary>
/// <remarks>
/// They cross the ABI through the ordinary <c>list_settings</c> stream under the pseudo-provider name
/// <see cref="Provider"/>, so the host registers them as DuckDB extension options and pushes values back into
/// <see cref="ProviderSettingsStore"/> exactly like a provider's — no C++ change, no new ABI entry. The
/// provider column is opaque to the host (it round-trips the string it was given), which is what makes that
/// work.
/// </remarks>
internal static class HostSettings
{
    /// <summary>The pseudo-provider these are filed under in <see cref="ProviderSettingsStore"/>.</summary>
    public const string Provider = "fabricator";

    /// <summary>The DuckDB setting name gating <c>fabricator_install_plugin()</c> and
    /// <c>fabricator_uninstall_plugin()</c>.</summary>
    /// <remarks>
    /// ⚠ The name says INSTALL and the switch also gates UNINSTALL. One opt-in for "SQL may manage the
    /// plugin root" is the right granularity — a caller who may add executable code has no reason to be
    /// denied removing it — but the name is narrower than the meaning. Left as-is deliberately rather than
    /// renamed in the same breath as adding the second consumer; the description carries the real scope.
    /// </remarks>
    public const string AllowPluginInstallName = "fabricator_allow_plugin_install";

    /// <summary>The DuckDB setting that flips a correlated LATERAL call between the BATCHED operator (one
    /// managed call per input chunk) and DuckDB's own row-by-row driver (one per outer row).</summary>
    /// <remarks>
    /// It is a testing instrument as much as an escape hatch. The batched path is installed by a purely
    /// post-binding rewrite, so both paths share ONE bind — which makes the row-by-row path a REFERENCE
    /// ORACLE: run the same query with this off and on and the results must be identical (modulo row order,
    /// which no lateral plan promises). See catalog/fabricator_lateral.hpp.
    /// </remarks>
    public const string BatchedLateralName = "fabricator_batched_lateral";

    public static IEnumerable<ProviderSetting> Settings { get; } = new[]
    {
        new ProviderSetting(
            AllowPluginInstallName,
            ProviderSettingType.Bool,
            Default: false,
            Description: "Allow fabricator_install_plugin() and fabricator_uninstall_plugin() to manage a " +
                         "plugin root. Off by default: an installed plugin is loaded into this process and " +
                         "runs with the extension's full privileges."),
        new ProviderSetting(
            BatchedLateralName,
            ProviderSettingType.Bool,
            Default: true,
            Description: "Batch a correlated LATERAL call over a whole input chunk (one provider call per " +
                         "~2048 outer rows) instead of DuckDB's row-by-row driver. On by default; turn it " +
                         "off to fall back to the stock path, which is also the reference oracle for it."),
    };

    /// <summary>
    /// Whether installing is permitted right now. Defaults to FALSE on an unset/unparseable value rather
    /// than to the declared default, so a store that never received the registration seeding still refuses.
    /// </summary>
    public static bool AllowPluginInstall =>
        ProviderSettingsStore.Instance.GetBool(Provider, AllowPluginInstallName) ?? false;
}
