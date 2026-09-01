// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Fabricator.Bridge;
using Xunit;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// Plugin root resolution and candidate discovery. Two properties here are structurally untestable from SQL,
/// which is why they are pinned offline: the DEFAULT root lives under the real user's home, so no hermetic
/// suite may create it or assert on its contents; and the override precedence can only be observed with the
/// variable UNSET, while <c>verify_plugin</c> must set it.
/// </summary>
public class PluginPathsTests
{
    private static string Home => Path.Combine(Path.GetTempPath(), "fab-home-" + Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- default root

    [Fact]
    public void Default_root_is_under_the_user_home()
    {
        var home = Home;
        var roots = PluginPaths.ResolveRoots(env: null, userHome: home);
        Assert.Single(roots);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(home, ".duckdb", "fabricator", "plugins")),
            roots[0]);
    }

    [Fact]
    public void Default_root_is_not_inside_the_managed_directory()
    {
        // The measured reason (2026-08-18): several projects publish into the managed dir and `dotnet publish`
        // deletes files its own previous publish wrote whose closure no longer contains them — that silently
        // removed five SqlClient DLLs from a populated payload. A plugin under the managed dir would be wiped
        // by an ordinary publish, with no error. This pins the SHAPE of the default so a later "tidy it next
        // to the extension" cannot pass unnoticed.
        var roots = PluginPaths.ResolveRoots(env: null, userHome: Home);
        Assert.DoesNotContain("extension", roots[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".duckdb", roots[0], StringComparison.Ordinal);
    }

    [Fact]
    public void No_home_and_no_override_yields_no_roots()
    {
        // A legitimate answer, not an error: the scan then reports nothing rather than inventing a root.
        Assert.Empty(PluginPaths.ResolveRoots(env: null, userHome: null));
        Assert.Empty(PluginPaths.ResolveRoots(env: "   ", userHome: "   "));
    }

    // ---------------------------------------------------------------- bundled root (the distribution)

    [Fact]
    public void Bundled_root_sits_under_the_managed_directory_and_is_searched_LAST()
    {
        // The single-file distribution's payload is the core loadable plus the MANAGED directory, so a
        // shipped plugin can only live inside it.
        //
        // ⚠⚠ LAST is load-bearing, and the FIRST version of this test asserted the opposite for a reason
        // that was simply wrong. The plugin scan registers with `refuseCollisions: true`, so it is
        // FIRST-ROOT-WINS: a duplicate provider name met in a later root is reported `rejected`, never
        // overwritten. (The justification originally written here — that `BackendRegistry.Add` is
        // `map[name] = backend`, last-wins — describes the BUILT-IN registration path, not the plugin one.)
        // Under bundled-first the shipped copy won and a user's install was REJECTED, which is the opposite
        // of what a user installing a newer copy expects. MEASURED with two roots holding one plugin: the
        // first loads, the second is rejected with a collision message naming both.
        var home = Home;
        var managed = Path.Combine(Path.GetTempPath(), "fab-managed-" + Guid.NewGuid().ToString("N"));
        var roots = PluginPaths.ResolveRoots(env: null, userHome: home, managedDir: managed);

        Assert.Equal(2, roots.Count);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(home, ".duckdb", "fabricator", "plugins")),
            roots[0]);
        Assert.Equal(Path.GetFullPath(Path.Combine(managed, "plugins")), roots[1]);
    }

    [Fact]
    public void Override_replaces_the_bundled_root_too()
    {
        // ⚠ THE FOOTGUN, PINNED DELIBERATELY. FABRICATOR_PLUGIN_DIR replaces EVERYTHING, bundled included —
        // so a user who sets it to add their own plugin silently loses the shipped ones. That is the
        // intended behaviour and not an oversight: the hermetic tier points the variable at an empty
        // directory precisely so its plugin set is provably independent of machine state, and a bundled root
        // that survived the override would make a tier's result depend on whether anyone had run a pack into
        // that build tree. If this test ever fails, hermeticity has been traded away — decide that on
        // purpose.
        var only = Path.Combine(Path.GetTempPath(), "fab-only-" + Guid.NewGuid().ToString("N"));
        var roots = PluginPaths.ResolveRoots(env: only, userHome: Home,
                                             managedDir: Path.GetTempPath());
        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(only), roots[0]);
    }

    [Fact]
    public void No_managed_directory_yields_no_bundled_root()
    {
        // A host that will not say where it loaded us from costs the BUNDLED root, never the user's — which
        // is why ManagedDirectory() swallows and returns null rather than throwing.
        var home = Home;
        Assert.Single(PluginPaths.ResolveRoots(env: null, userHome: home, managedDir: null));
        Assert.Single(PluginPaths.ResolveRoots(env: null, userHome: home, managedDir: "   "));
    }

    [Fact]
    public void Bundled_root_alone_is_enough_when_there_is_no_home()
    {
        // The distribution's shape on a machine with no resolvable user profile: the artifact's own plugins
        // still load. Without this the two "no home" cases would be indistinguishable.
        var managed = Path.Combine(Path.GetTempPath(), "fab-managed-" + Guid.NewGuid().ToString("N"));
        var roots = PluginPaths.ResolveRoots(env: null, userHome: null, managedDir: managed);
        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(Path.Combine(managed, "plugins")), roots[0]);
    }

    // ---------------------------------------------------------------- override precedence

    [Fact]
    public void Override_replaces_the_default_rather_than_extending_it()
    {
        // THE load-bearing assertion of this file. A rig that narrows the search must actually get a narrow
        // search, or it is not testing what it claims — and the default root would otherwise be searched
        // invisibly, on a developer machine where it may well contain something.
        var home = Home;
        var only = Path.Combine(Path.GetTempPath(), "fab-only");
        var roots = PluginPaths.ResolveRoots(env: only, userHome: home);
        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(only), roots[0]);
        Assert.DoesNotContain(roots, r => r.Contains(".duckdb", StringComparison.Ordinal));
    }

    [Fact]
    public void Override_is_a_comma_list_in_order_and_is_deduplicated()
    {
        var a = Path.Combine(Path.GetTempPath(), "fab-a");
        var b = Path.Combine(Path.GetTempPath(), "fab-b");
        var roots = PluginPaths.ResolveRoots(env: $" {a} , {b} , {a} ", userHome: null);
        Assert.Equal(new[] { Path.GetFullPath(a), Path.GetFullPath(b) }, roots);
    }

    [Fact]
    public void Override_paths_are_absolute()
    {
        // Relative roots would resolve against the PROCESS working directory, which for an embedded extension
        // is the host application's and is not something a user can reason about.
        var roots = PluginPaths.ResolveRoots(env: "relative/plugins", userHome: null);
        Assert.Single(roots);
        Assert.True(Path.IsPathRooted(roots[0]));
    }

    // ---------------------------------------------------------------- candidate discovery

    [Fact]
    public void Candidates_are_found_recursively()
    {
        // The regression this exists for: the scan searched only a root's TOP LEVEL, so a plugin laid out the
        // way an installer writes one (<root>/<name>/<version>/<platform>/) was never seen at all.
        var root = Directory.CreateDirectory(Home).FullName;
        try
        {
            var deep = Path.Combine(root, "sampleplugin", "1.2.3", "windows_amd64");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "Plug.dll"), "x");
            File.WriteAllText(Path.Combine(root, "Top.dll"), "x");

            var found = PluginPaths.EnumerateCandidates(root).Select(Path.GetFileName).ToArray();
            Assert.Contains("Plug.dll", found);
            Assert.Contains("Top.dll", found);
            Assert.Equal(2, found.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Candidates_are_dll_only_and_ordered()
    {
        // Ordering is not cosmetic: the FIRST provider registered under a name wins, and Directory
        // enumeration order is filesystem-dependent, so an unordered scan makes which plugin wins a
        // property of the disk rather than of the configuration.
        var root = Directory.CreateDirectory(Home).FullName;
        try
        {
            foreach (var n in new[] { "b.dll", "a.dll", "notes.txt", "native.so" })
            {
                File.WriteAllText(Path.Combine(root, n), "x");
            }
            var found = PluginPaths.EnumerateCandidates(root).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "a.dll", "b.dll" }, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_missing_root_yields_no_candidates_rather_than_throwing()
    {
        // The scan classifies a missing root itself (status root_missing); enumeration must not also throw,
        // or one unreadable root would abort the whole scan and take every other plugin with it.
        Assert.Empty(PluginPaths.EnumerateCandidates(Path.Combine(Path.GetTempPath(), "fab-absent-" + Guid.NewGuid().ToString("N"))));
    }

    /// <summary>
    /// A path segment beginning with '.' is not searched. LOAD-BEARING for the installer, not cosmetic: it
    /// stages an extraction under &lt;root&gt;/.staging/&lt;guid&gt; and parks a replaced version under
    /// &lt;root&gt;/.trash/&lt;guid&gt;, both INSIDE the root so the publishing Directory.Move stays on one
    /// volume and is therefore atomic. Without this rule a scan running concurrently in another process could
    /// load a half-extracted plugin, or reload one that has just been replaced.
    /// </summary>
    [Fact]
    public void Dotted_segments_below_the_root_are_not_candidates()
    {
        string root = Path.Combine(Path.GetTempPath(), "fab-hidden-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".staging", "abc"));
            Directory.CreateDirectory(Path.Combine(root, ".trash", "def"));
            Directory.CreateDirectory(Path.Combine(root, "demo", "1.0.0"));
            File.WriteAllText(Path.Combine(root, ".staging", "abc", "half.dll"), "x");
            File.WriteAllText(Path.Combine(root, ".trash", "def", "old.dll"), "x");
            File.WriteAllText(Path.Combine(root, "demo", "1.0.0", "live.dll"), "x");
            var found = PluginPaths.EnumerateCandidates(root).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "live.dll" }, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The ROOT ITSELF may be dotted and must still be searched — the DEFAULT root is
    /// <c>~/.duckdb/fabricator/plugins</c>, so a rule applied to the whole path rather than to the part below
    /// the root would disable plugin discovery out of the box.
    /// </summary>
    [Fact]
    public void A_dotted_root_is_still_searched()
    {
        string parent = Path.Combine(Path.GetTempPath(), "fab-dotroot-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, ".duckdb", "fabricator", "plugins");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "p.dll"), "x");
            Assert.Equal(new[] { "p.dll" }, PluginPaths.EnumerateCandidates(root).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    // ---------------------------------------------------------------- the report store

    [Fact]
    public void Report_round_trips_and_is_a_snapshot()
    {
        var entries = new[]
        {
            new PluginScanEntry("/r", string.Empty, PluginScanStatus.Root, string.Empty, "1 candidate"),
            new PluginScanEntry("/r", "/r/p.dll", PluginScanStatus.Loaded, "acme", "1 provider(s)"),
        };
        PluginPaths.SetReport(entries);
        var read = PluginPaths.Report();
        Assert.Equal(2, read.Count);
        Assert.Equal(PluginScanStatus.Loaded, read[1].Status);
        Assert.Equal("acme", read[1].Provider);

        // A snapshot, not a live view: the reader must not be able to mutate what the scan recorded.
        PluginPaths.SetReport(Array.Empty<PluginScanEntry>());
        Assert.Equal(2, read.Count);
        Assert.Empty(PluginPaths.Report());
    }
}
