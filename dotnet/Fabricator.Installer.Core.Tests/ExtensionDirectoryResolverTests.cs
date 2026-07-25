namespace Fabricator.Installer.Tests;

/// <summary>
/// These expectations were captured empirically from DuckDB 1.5.5 by observing where
/// <c>INSTALL '&lt;local file&gt;'</c> actually lands, not read off the documentation. If DuckDB ever
/// changes the layout, these are the tests that must fail.
/// </summary>
public sealed class ExtensionDirectoryResolverTests
{
    private static string Sep(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void Resolve_Default_IsHomeDotDuckdbExtensionsVersionPlatform()
    {
        // Observed: home_directory='<tmp>' + no extension_directory
        //           -> <tmp>/.duckdb/extensions/v1.5.5/windows_amd64
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            SourceId = "d8cdaa33fd",
            Platform = "windows_amd64",
            HomeDirectory = Sep("/tmp/fakehome"),
        });

        Assert.Equal(Sep("/tmp/fakehome/.duckdb/extensions/v1.5.5/windows_amd64"), resolved);
    }

    /// <summary>
    /// The correction that matters: with a custom <c>extension_directory</c> there is NO
    /// <c>extensions</c> path component. It exists in the default case only because the default base
    /// string is literally <c>~/.duckdb/extensions</c>. Appending one unconditionally would extract
    /// the payload into a directory DuckDB never searches.
    /// </summary>
    [Fact]
    public void Resolve_CustomExtensionDirectory_HasNoExtensionsComponent()
    {
        // Observed: SET extension_directory='<tmp>' -> <tmp>/v1.5.5/windows_amd64
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            SourceId = "d8cdaa33fd",
            Platform = "windows_amd64",
            ExtensionDirectorySetting = Sep("/opt/ducks"),
            HomeDirectory = Sep("/tmp/fakehome"),
        });

        Assert.Equal(Sep("/opt/ducks/v1.5.5/windows_amd64"), resolved);
        Assert.DoesNotContain("extensions", resolved);
    }

    [Fact]
    public void Resolve_FallsBackToTheFirstEntryOfThePluralSetting()
    {
        // Observed: SET extension_directories=['<tmp>'] -> <tmp>/v1.5.5/windows_amd64
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "linux_amd64",
            ExtensionDirectoriesSetting = [Sep("/first"), Sep("/second")],
            HomeDirectory = Sep("/home/u"),
        });

        Assert.Equal(Sep("/first/v1.5.5/linux_amd64"), resolved);
    }

    [Fact]
    public void Resolve_PrefersTheSingularSettingOverTheList()
    {
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "linux_amd64",
            ExtensionDirectorySetting = Sep("/singular"),
            ExtensionDirectoriesSetting = [Sep("/plural")],
            HomeDirectory = Sep("/home/u"),
        });

        Assert.Equal(Sep("/singular/v1.5.5/linux_amd64"), resolved);
    }

    [Fact]
    public void Resolve_IgnoresBlankSettings()
    {
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "linux_amd64",
            ExtensionDirectorySetting = "   ",
            ExtensionDirectoriesSetting = ["", Sep("/second")],
            HomeDirectory = Sep("/home/u"),
        });

        Assert.Equal(Sep("/second/v1.5.5/linux_amd64"), resolved);
    }

    [Fact]
    public void Resolve_ExpandsALeadingTilde()
    {
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "osx_arm64",
            ExtensionDirectorySetting = "~/ducks",
            HomeDirectory = Sep("/Users/me"),
        });

        Assert.Equal(Sep("/Users/me/ducks/v1.5.5/osx_arm64"), resolved);
    }

    /// <summary>DuckDB's ExpandPath is a bare prefix replacement — no separator is required.</summary>
    [Fact]
    public void Resolve_ExpandsATildeWithoutASeparator()
    {
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "osx_arm64",
            ExtensionDirectorySetting = "~ducks",
            HomeDirectory = Sep("/Users/me"),
        });

        Assert.Equal(Sep("/Users/meducks/v1.5.5/osx_arm64"), resolved);
    }

    [Fact]
    public void Resolve_ThrowsWhenATildeCannotBeExpanded()
    {
        var ex = Assert.Throws<InstallerException>(() => ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "linux_amd64",
            HomeDirectory = "",
        }));

        Assert.Contains("home directory", ex.Message);
    }

    [Fact]
    public void Resolve_ThrowsOnAnEmptyPlatform()
    {
        var ex = Assert.Throws<InstallerException>(() => ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "",
            HomeDirectory = Sep("/home/u"),
        }));

        Assert.Contains("empty platform", ex.Message);
    }

    [Theory]
    [InlineData("v1.5.5", true)]
    [InlineData("1.5.5", true)]
    [InlineData("v1.6.0-dev1234", false)]
    public void IsRelease_TreatsOnlyDevTagsAsNonReleases(string version, bool expected) =>
        Assert.Equal(expected, ExtensionDirectoryResolver.IsRelease(version));

    [Theory]
    [InlineData("1.5.5", "v1.5.5")]
    [InlineData("v1.5.5", "v1.5.5")]
    [InlineData("", "")]
    public void NormalizeVersionTag_AddsALeadingV(string input, string expected) =>
        Assert.Equal(expected, ExtensionDirectoryResolver.NormalizeVersionTag(input));

    [Fact]
    public void VersionDirectoryName_UsesTheTagForReleasesAndTheSourceIdForDevBuilds()
    {
        Assert.Equal("v1.5.5", ExtensionDirectoryResolver.VersionDirectoryName("1.5.5", "abc123"));
        Assert.Equal("abc123", ExtensionDirectoryResolver.VersionDirectoryName("v1.6.0-dev99", "abc123"));
    }

    [Fact]
    public void VersionDirectoryName_ThrowsForADevBuildWithNoSourceId()
    {
        var ex = Assert.Throws<InstallerException>(() => ExtensionDirectoryResolver.VersionDirectoryName("v1.6.0-dev99", ""));
        Assert.Contains("no source id", ex.Message);
    }

    [Fact]
    public void Resolve_UsesTheSourceIdForADevBuild()
    {
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.6.0-dev4242",
            SourceId = "0123456789",
            Platform = "linux_amd64",
            ExtensionDirectorySetting = Sep("/opt/ducks"),
        });

        Assert.Equal(Sep("/opt/ducks/0123456789/linux_amd64"), resolved);
    }

    /// <summary>
    /// Windows accepts either separator and normalizes to <c>\</c>; POSIX must leave a backslash
    /// alone, where it is a legal filename character.
    /// </summary>
    [Fact]
    public void Resolve_NormalizesSeparatorsLikeDuckDb()
    {
        string resolved = ExtensionDirectoryResolver.Resolve(new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "windows_amd64",
            ExtensionDirectorySetting = "C:/mixed/path",
            HomeDirectory = Sep("/home/u"),
        });

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\mixed\path\v1.5.5\windows_amd64", resolved);
        }
        else
        {
            Assert.StartsWith("C:/mixed/path", resolved);
        }
    }
}
