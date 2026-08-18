using System.Text;
using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// The plugin archive's contract: what <c>fabricator-plugin.json</c> must say, and which entries a given
/// platform installs.
/// </summary>
/// <remarks>
/// These are the rules an end-to-end suite structurally cannot reach. It can install ONE archive, built on
/// the machine running it, for the platform running it — so the cases that decide whether the layout rule is
/// a rule at all (another platform's directory; an archive carrying nothing for this one; a manifest naming
/// a path that escapes) have no fixture there and would go untested.
/// </remarks>
public class PluginPackageTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private const string ValidManifest =
        """{"formatVersion":1,"name":"demo","version":"1.2.0","entryAssembly":"Demo.dll","abstractionsVersion":"1.0.0"}""";

    // ---------------------------------------------------------------- manifest

    [Fact]
    public void Valid_manifest_round_trips()
    {
        Assert.True(PluginPackage.TryParseManifest(Utf8(ValidManifest), out var m, out var error));
        Assert.Null(error);
        Assert.NotNull(m);
        Assert.Equal("demo", m!.Name);
        Assert.Equal("1.2.0", m.Version);
        Assert.Equal("Demo.dll", m.EntryAssembly);
        Assert.Equal("1.0.0", m.AbstractionsVersion);
    }

    /// <summary>A manifest is a file people edit by hand, and the editors that write a BOM are the common
    /// ones. Without the strip, JsonDocument reports "'0xEF' is an invalid start of a value" — a message
    /// about a byte, for a file that is otherwise perfectly correct.</summary>
    [Fact]
    public void Utf8_bom_is_tolerated()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(ValidManifest)).ToArray();
        Assert.True(PluginPackage.TryParseManifest(withBom, out var m, out _));
        Assert.Equal("demo", m!.Name);
    }

    /// <summary>An UNKNOWN format version is refused, not ignored. A later schema may reuse these field names
    /// for something else, so reading it optimistically would install the wrong thing rather than fail.</summary>
    [Fact]
    public void Unknown_format_version_is_refused_and_names_both_numbers()
    {
        Assert.False(PluginPackage.TryParseManifest(
            Utf8("""{"formatVersion":2,"name":"d","version":"1","entryAssembly":"D.dll"}"""), out _, out var error));
        Assert.Contains("2", error);
        Assert.Contains("1", error);
    }

    [Fact]
    public void Missing_format_version_is_refused()
    {
        Assert.False(PluginPackage.TryParseManifest(
            Utf8("""{"name":"d","version":"1","entryAssembly":"D.dll"}"""), out _, out var error));
        Assert.Contains("formatVersion", error);
    }

    [Fact]
    public void Non_object_manifest_is_refused()
    {
        Assert.False(PluginPackage.TryParseManifest(Utf8("[1,2,3]"), out _, out var error));
        Assert.Contains("object", error);
    }

    [Fact]
    public void Malformed_json_is_refused_with_the_parser_reason()
    {
        Assert.False(PluginPackage.TryParseManifest(Utf8("{not json"), out _, out var error));
        Assert.Contains("not valid JSON", error);
    }

    [Theory]
    [InlineData("""{"formatVersion":1,"version":"1","entryAssembly":"D.dll"}""", "name")]
    [InlineData("""{"formatVersion":1,"name":"d","entryAssembly":"D.dll"}""", "version")]
    [InlineData("""{"formatVersion":1,"name":"d","version":"1"}""", "entryAssembly")]
    public void Missing_required_field_is_refused_and_names_it(string json, string field)
    {
        Assert.False(PluginPackage.TryParseManifest(Utf8(json), out _, out var error));
        Assert.Contains(field, error);
    }

    /// <summary>name/version become DIRECTORY NAMES, so anything that could escape or that one platform
    /// refuses is rejected on every platform — otherwise an archive's install location would depend on the
    /// OS installing it.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("")]
    public void Unsafe_name_is_refused(string name)
    {
        string json = $$"""{"formatVersion":1,"name":{{System.Text.Json.JsonSerializer.Serialize(name)}},"version":"1","entryAssembly":"D.dll"}""";
        Assert.False(PluginPackage.TryParseManifest(Utf8(json), out _, out _));
    }

    /// <summary>entryAssembly is a RELATIVE PATH (a plugin may keep its entry in a subdirectory), validated
    /// with the same guard the extraction uses — so what may be named is exactly what may be written.</summary>
    [Fact]
    public void Entry_assembly_may_be_nested()
    {
        Assert.True(PluginPackage.TryParseManifest(
            Utf8("""{"formatVersion":1,"name":"d","version":"1","entryAssembly":"lib/D.dll"}"""),
            out var m, out _));
        Assert.Equal("lib/D.dll", m!.EntryAssembly);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("c:/drive.dll")]
    public void Entry_assembly_that_escapes_is_refused(string entry)
    {
        string json = $$"""{"formatVersion":1,"name":"d","version":"1","entryAssembly":{{System.Text.Json.JsonSerializer.Serialize(entry)}}}""";
        Assert.False(PluginPackage.TryParseManifest(Utf8(json), out _, out var error));
        Assert.Contains("entryAssembly", error);
    }

    /// <summary>abstractionsVersion is OPTIONAL and never gated on — nothing versions that assembly today, so
    /// a comparison would be an untestable flag. Absent must therefore parse, not fail.</summary>
    [Fact]
    public void Abstractions_version_is_optional()
    {
        Assert.True(PluginPackage.TryParseManifest(
            Utf8("""{"formatVersion":1,"name":"d","version":"1","entryAssembly":"D.dll"}"""), out var m, out _));
        Assert.Equal("", m!.AbstractionsVersion);
    }

    // ---------------------------------------------------------------- layout

    [Fact]
    public void Any_only_archive_installs_everything_under_any()
    {
        var files = PluginPackage.SelectFiles(
            new[] { "fabricator-plugin.json", "any/", "any/D.dll", "any/lib/Dep.dll" }, "windows_amd64", out var error);
        Assert.Null(error);
        Assert.Equal(new[] { "D.dll", "lib/Dep.dll" }, files.Select(f => f.Relative).ToArray());
        Assert.All(files, f => Assert.False(f.Platform));
    }

    [Fact]
    public void Platform_only_archive_installs_that_platform()
    {
        var files = PluginPackage.SelectFiles(
            new[] { "fabricator-plugin.json", "windows_amd64/D.dll" }, "windows_amd64", out var error);
        Assert.Null(error);
        Assert.Equal(new[] { "D.dll" }, files.Select(f => f.Relative).ToArray());
        Assert.True(files[0].Platform);
    }

    /// <summary>THE MERGE RULE. The platform copy WINS — asserted on the chosen ENTRY, not just on the count,
    /// because a version that returned both and relied on extraction order would pass a count check and then
    /// report a file total that is not the number of files it wrote.</summary>
    [Fact]
    public void Platform_overlays_any()
    {
        var files = PluginPackage.SelectFiles(
            new[] { "any/D.dll", "any/Shared.dll", "linux_amd64/D.dll" }, "linux_amd64", out var error);
        Assert.Null(error);
        Assert.Equal(2, files.Count);
        var d = files.Single(f => f.Relative == "D.dll");
        Assert.Equal("linux_amd64/D.dll", d.Entry);
        Assert.True(d.Platform);
        Assert.False(files.Single(f => f.Relative == "Shared.dll").Platform);
    }

    /// <summary>Order of appearance must not decide the winner: an archive whose platform entry comes FIRST
    /// merges the same way as one whose any/ entry does.</summary>
    [Fact]
    public void Platform_overlays_any_regardless_of_archive_order()
    {
        var files = PluginPackage.SelectFiles(
            new[] { "linux_amd64/D.dll", "any/D.dll" }, "linux_amd64", out _);
        Assert.Equal("linux_amd64/D.dll", Assert.Single(files).Entry);
    }

    /// <summary>The case the whole "no inference" decision exists for: another platform's payload is never
    /// installed, however alone it is in the archive.</summary>
    [Fact]
    public void Another_platforms_directory_is_never_installed()
    {
        var files = PluginPackage.SelectFiles(
            new[] { "fabricator-plugin.json", "linux_amd64/D.so", "osx_arm64/D.dylib" }, "windows_amd64", out var error);
        Assert.Empty(files);
        Assert.NotNull(error);
        Assert.Contains("windows_amd64", error);
        Assert.Contains("linux_amd64", error);
        Assert.Contains("osx_arm64", error);
    }

    /// <summary>A FLAT archive is refused rather than guessed at. Accepting it would require recognising a
    /// platform directory by name, under which an archive shipping only linux_amd64/ would look flat on
    /// Windows and its Linux binaries would be installed — a wrong answer, not a missing feature.</summary>
    [Fact]
    public void Flat_archive_is_refused()
    {
        var files = PluginPackage.SelectFiles(new[] { "fabricator-plugin.json", "D.dll" }, "windows_amd64", out var error);
        Assert.Empty(files);
        Assert.NotNull(error);
        Assert.Contains("no directories at all", error);
    }

    [Fact]
    public void Directory_entries_and_root_files_are_not_installed()
    {
        var files = PluginPackage.SelectFiles(
            new[] { "fabricator-plugin.json", "README.md", "any/", "any/D.dll" }, "windows_amd64", out _);
        Assert.Equal(new[] { "D.dll" }, files.Select(f => f.Relative).ToArray());
    }

    /// <summary>An entry that escapes is DROPPED here and REFUSED by the extractor. Dropping it silently at
    /// this layer would let a crafted archive install its safe half and say nothing about the rest, so the
    /// loud refusal has to stay downstream — this only asserts it is not SELECTED.</summary>
    [Fact]
    public void Escaping_entry_is_not_selected()
    {
        var files = PluginPackage.SelectFiles(new[] { "any/../../evil.dll", "any/D.dll" }, "windows_amd64", out _);
        Assert.Equal(new[] { "D.dll" }, files.Select(f => f.Relative).ToArray());
    }

    [Fact]
    public void Selection_is_ordered_by_destination()
    {
        var files = PluginPackage.SelectFiles(
            new[] { "any/z.dll", "any/a.dll", "any/m/n.dll" }, "windows_amd64", out _);
        Assert.Equal(new[] { "a.dll", "m/n.dll", "z.dll" }, files.Select(f => f.Relative).ToArray());
    }

    // ---------------------------------------------------------------- destination

    [Fact]
    public void Destination_is_root_name_version()
    {
        PluginPackage.TryParseManifest(Utf8(ValidManifest), out var m, out _);
        string dest = PluginPackage.DestinationFor(Path.Combine("R", "oot"), m!);
        Assert.Equal(Path.Combine("R", "oot", "demo", "1.2.0"), dest);
    }
}
