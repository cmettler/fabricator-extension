// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Fabricator.Installer;

namespace Fabricator.Bridge;

/// <summary>
/// What a plugin archive declares about itself: <c>fabricator-plugin.json</c> at the archive root.
/// </summary>
/// <param name="FormatVersion">Manifest schema version. Unknown values are REFUSED rather than ignored —
/// a future format may mean something different by the same field names.</param>
/// <param name="Name">Plugin identity, and the first directory level it installs into. A path SEGMENT.</param>
/// <param name="Version">Plugin version, and the second directory level. A path SEGMENT, so installs of
/// different versions coexist and an upgrade never has to overwrite a file that may be locked.</param>
/// <param name="EntryAssembly">The assembly declaring the plugin's <c>IProvider</c> / global functions,
/// relative to the merged install directory. Its presence after the merge is checked at install, because an
/// archive that installs cleanly and contains no plugin is exactly the silent failure this whole surface
/// exists to remove.</param>
/// <param name="AbstractionsVersion">The <c>Fabricator.Abstractions</c> version the plugin was built
/// against. RECORDED AND REPORTED, NOT ENFORCED — see the remarks on <see cref="PluginPackage"/>.</param>
internal sealed record PluginManifest(
    int FormatVersion,
    string Name,
    string Version,
    string EntryAssembly,
    string AbstractionsVersion);

/// <summary>One archive entry selected for installation.</summary>
/// <param name="Entry">The entry's full name inside the archive.</param>
/// <param name="Relative">Where it lands under the install directory (forward slashes).</param>
/// <param name="Platform">True when it came from the platform directory rather than <c>any/</c>.</param>
internal readonly record struct PluginFileSelection(string Entry, string Relative, bool Platform);

/// <summary>
/// The decidable half of installing a plugin: what the manifest says, and which archive entries this
/// platform takes. Deliberately free of file I/O so tier 0 can link it — the layout rule and the refusals
/// are the parts worth pinning offline, and each of them is one line to get wrong.
/// </summary>
/// <remarks>
/// <para><b>THE LAYOUT IS FIXED AND NOT INFERRED.</b> An archive carries <c>any/</c> (platform-independent)
/// and/or <c>&lt;duckdb platform&gt;/</c> (e.g. <c>windows_amd64/</c>), and the install is their MERGE with
/// the platform directory overlaying <c>any/</c>. Nothing else is taken. A "flat" archive — DLLs at the root
/// — is REFUSED rather than guessed at, because the alternative is a rule that has to recognise a platform
/// directory by its NAME: an archive shipping only <c>linux_amd64/</c> would then look flat on Windows and
/// its Linux binaries would be installed. Guessing there is a wrong ANSWER, so the archive states it.</para>
/// <para>⚠ <see cref="PluginManifest.AbstractionsVersion"/> is REPORTED, NOT GATED ON — and the reason
/// changed on 2026-09-02 without the conclusion changing. It used to be that nothing here versioned
/// <c>Fabricator.Abstractions</c> (every assembly was 1.0.0.0), so a comparison would have passed always or
/// failed always — an untestable flag. There IS a version now (<see cref="FabricatorVersion.Contract"/>,
/// from the repo's single <c>VERSION</c> file), and <c>PluginInstall</c> compares the two and appends a note
/// to the install row's <c>detail</c>. It still does not REFUSE, because refusing would be a stricter rule
/// than the runtime's own: nothing binds on the number — the plugin's reference resolves to whichever copy
/// the host already loaded, matched by SIMPLE NAME — so a skewed plugin loads and fails only on a member it
/// uses that moved. Most releases move the version and change nothing a given plugin touches.</para>
/// </remarks>
internal static class PluginPackage
{
    /// <summary>
    /// A note for the install row's <c>detail</c> when a plugin was built against a DIFFERENT contract
    /// version than the one running. Empty otherwise — silence is the normal case, and the common one.
    /// </summary>
    /// <param name="declared">The manifest's <c>abstractionsVersion</c>; <c>""</c> when the archive
    /// declared none.</param>
    /// <param name="running">The host's own contract version (<c>FabricatorVersion.Contract</c>). Passed
    /// in rather than read here, so this stays a PURE function with no assembly to interrogate — which is
    /// what lets tier 0 pin all three outcomes offline, where an install test can only ever produce the
    /// "equal" one.</param>
    /// <remarks>
    /// <para><b>⚠ It REPORTS; it does not gate, and that is a decision rather than an unfinished one.</b>
    /// Nothing in the runtime binds on this number: a plugin's <c>Fabricator.Abstractions</c> reference is
    /// satisfied by the copy already loaded in the bridge's load context (the scan skips a plugin-dir copy
    /// by SIMPLE NAME, and <c>InstallPluginResolver</c> probes by simple name too), so a skewed plugin
    /// loads and then fails at the first member it uses that moved. Refusing the install would be a
    /// STRICTER rule than the runtime's own, and it would refuse plugins that work — the contract version
    /// moves on every release, most of which change nothing a given plugin touches.</para>
    /// <para>⚠ An EMPTY declared version says NOTHING rather than "unknown": the field is optional and
    /// every archive built before there was a version to record has none.</para>
    /// <para>⚠ The comparison is ORDINAL, not semantic. A scheme this repo does not yet have (a prerelease
    /// suffix, a build sha) would make two equivalent contracts compare unequal and produce a note where
    /// none is warranted — the safe direction for a message that changes nothing, and the reason to keep
    /// it out of any decision.</para>
    /// </remarks>
    internal static string ContractSkew(string declared, string running)
    {
        if (string.IsNullOrWhiteSpace(declared) || string.IsNullOrWhiteSpace(running)
            || string.Equals(declared, running, StringComparison.Ordinal))
        {
            return string.Empty;
        }
        return $"; built against Fabricator.Abstractions {declared}, this host has {running} "
               + "(reported, not enforced - the plugin loads, and fails only on a member it uses that moved)";
    }

    /// <summary>The manifest's name, matched EXACTLY at the archive root (zip entry names are case-sensitive
    /// on every platform, so accepting a case variant here would make an archive install on one OS and not
    /// another).</summary>
    public const string ManifestFileName = "fabricator-plugin.json";

    /// <summary>The platform-independent directory.</summary>
    public const string AnyDirectory = "any";

    /// <summary>The only manifest schema this build understands.</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>
    /// Parses and VALIDATES a manifest. Returns false with a user-facing <paramref name="error"/> rather
    /// than throwing, so the caller can prefix it with the archive path.
    /// </summary>
    public static bool TryParseManifest(ReadOnlySpan<byte> utf8, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        // ⚠ STRIP A UTF-8 BOM. JsonDocument.Parse does not skip one and reports it as
        // "'0xEF' is an invalid start of a value" — a message that says nothing about the real problem, on a
        // file people will edit by hand in an editor that writes one by default.
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException ex)
        {
            error = $"{ManifestFileName} is not valid JSON: {ex.Message}";
            return false;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"{ManifestFileName} must be a JSON object.";
                return false;
            }
            if (!doc.RootElement.TryGetProperty("formatVersion", out var fv) ||
                fv.ValueKind != JsonValueKind.Number || !fv.TryGetInt32(out int formatVersion))
            {
                error = $"{ManifestFileName} has no numeric 'formatVersion'.";
                return false;
            }
            if (formatVersion != SupportedFormatVersion)
            {
                error = $"{ManifestFileName} declares formatVersion {formatVersion}; this build understands " +
                        $"{SupportedFormatVersion}.";
                return false;
            }
            if (!TryReadSegment(doc.RootElement, "name", out string name, out error) ||
                !TryReadSegment(doc.RootElement, "version", out string version, out error))
            {
                return false;
            }
            if (!TryReadString(doc.RootElement, "entryAssembly", out string entry, out error))
            {
                return false;
            }
            // A relative path rather than a bare file name: a plugin may legitimately keep its entry in a
            // subdirectory. Validated with the SAME guard the extraction uses, so what may be named here is
            // exactly what may be written.
            if (!ArchivePath.TryNormalize(entry, out string entryNormalized, out string? entryError))
            {
                error = $"{ManifestFileName} has an unusable 'entryAssembly': {entryError}";
                return false;
            }
            string abstractions = doc.RootElement.TryGetProperty("abstractionsVersion", out var av) &&
                                  av.ValueKind == JsonValueKind.String
                ? av.GetString() ?? string.Empty
                : string.Empty;
            manifest = new PluginManifest(formatVersion, name, version, entryNormalized, abstractions);
            error = null;
            return true;
        }
    }

    private static bool TryReadString(JsonElement root, string property, out string value, out string? error)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
        {
            error = $"{ManifestFileName} has no string '{property}'.";
            return false;
        }
        value = el.GetString() ?? string.Empty;
        if (value.Length == 0)
        {
            error = $"{ManifestFileName} has an empty '{property}'.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryReadSegment(JsonElement root, string property, out string value, out string? error)
    {
        if (!TryReadString(root, property, out value, out error))
        {
            return false;
        }
        if (!IsSafeSegment(value))
        {
            error = $"{ManifestFileName}'s '{property}' ('{value}') is not usable as a directory name.";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// Whether a manifest value may be used as ONE directory name. Rejected on every platform, not just the
    /// one that would choke: an archive must install to the same place everywhere or its layout stops being
    /// a property of the archive.
    /// </summary>
    public static bool IsSafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            return false;
        }
        foreach (char c in value)
        {
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c))
            {
                return false;
            }
        }
        // Windows refuses these; a name that installs on Linux and not on Windows is worse than one refused
        // on both, because the archive then has a platform-dependent identity.
        return value[^1] is not ('.' or ' ');
    }

    /// <summary>
    /// The archive entries this platform installs: <c>any/</c> merged with <c>&lt;platform&gt;/</c>, the
    /// platform winning on a collision. Ordered by destination so the result is deterministic.
    /// </summary>
    /// <param name="entryNames">Every entry name in the archive (directory entries included; they are
    /// skipped).</param>
    /// <param name="platform">DuckDB's platform string, e.g. <c>windows_amd64</c>.</param>
    /// <param name="error">Set when the archive carries neither directory; names what it does carry.</param>
    public static IReadOnlyList<PluginFileSelection> SelectFiles(
        IEnumerable<string> entryNames, string platform, out string? error)
    {
        var selected = new Dictionary<string, PluginFileSelection>(StringComparer.Ordinal);
        var topLevel = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in entryNames)
        {
            if (string.IsNullOrEmpty(raw) || raw.EndsWith('/') || raw.EndsWith('\\'))
            {
                continue; // a directory entry contributes nothing to install
            }
            if (!ArchivePath.TryNormalize(raw, out string normalized, out _))
            {
                // Left to the extractor to REFUSE loudly. Skipping here would let a crafted archive install
                // its safe half and say nothing about the rest.
                continue;
            }
            int slash = normalized.IndexOf('/');
            if (slash <= 0)
            {
                continue; // a root-level file (the manifest, a README): never installed
            }
            string head = normalized[..slash];
            topLevel.Add(head);
            bool isPlatform = string.Equals(head, platform, StringComparison.Ordinal);
            if (!isPlatform && !string.Equals(head, AnyDirectory, StringComparison.Ordinal))
            {
                continue; // another platform's payload
            }
            string relative = normalized[(slash + 1)..];
            if (relative.Length == 0)
            {
                continue;
            }
            // Platform overlays any. Written as an explicit test rather than relying on extraction order, so
            // the COUNT the install reports is the number of files it wrote.
            if (selected.TryGetValue(relative, out var existing) && existing.Platform && !isPlatform)
            {
                continue;
            }
            selected[relative] = new PluginFileSelection(raw, relative, isPlatform);
        }
        if (selected.Count == 0)
        {
            string found = topLevel.Count == 0
                ? "it has no directories at all"
                : "it has: " + string.Join(", ", topLevel);
            error = $"the archive carries nothing for this platform: expected a '{AnyDirectory}/' and/or " +
                    $"'{platform}/' directory, but {found}.";
            return Array.Empty<PluginFileSelection>();
        }
        error = null;
        return new ReadOnlyCollection<PluginFileSelection>(
            selected.Values.OrderBy(s => s.Relative, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Where a plugin installs: <c>&lt;root&gt;/&lt;name&gt;/&lt;version&gt;</c>. Version-stamped because an
    /// assembly cannot be replaced in place — <c>LoadFromAssemblyPath</c> maps the file, which locks it on
    /// Windows, and the bridge's load context is not collectible — so an upgrade must write BESIDE the old
    /// one and take effect at next start.
    /// </summary>
    public static string DestinationFor(string root, PluginManifest manifest) =>
        System.IO.Path.Combine(root, manifest.Name, manifest.Version);
}
