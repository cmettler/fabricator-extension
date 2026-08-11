using System;
using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// Offline tests for <see cref="OneLakeStagingLocation"/> — the staging root a Fabric Warehouse
/// <c>COPY INTO</c> load writes its intermediate parquet to, in the two spellings the two engines need.
/// </summary>
/// <remarks>
/// <para>The round trip is the point: our writer is given the <c>abfss://</c> form and the engine is given
/// the <c>https://</c> form, and if the two ever name different places the load silently reads an empty
/// folder. Every case here fixes one of them against the other.</para>
/// <para>The REFUSALS are the load-bearing half. Each describes an input that every layer below accepts and
/// then does something quiet or baffling with — a <c>_</c>-prefixed segment because COPY INTO skips such
/// names (a successful load of zero rows), a lakehouse <c>Tables/</c> root because loose parquet there
/// surfaces as a broken managed table, and a NAME-based OneLake root because our writer stages to it fine
/// and the warehouse then fails to read it with an error mentioning neither names nor GUIDs.</para>
/// </remarks>
public class StagingLocationTests
{
    private const string Host = "onelake.dfs.fabric.microsoft.com";

    // ⚠ GUIDs, not display names: a OneLake staging root must name its workspace and item by id, because a
    // warehouse COPY INTO reading a name-based URL fails with "13840 … unsupported URL" (measured live) while
    // our own writer accepts it happily. These are the real ids of the `Test` workspace and the `LH`
    // lakehouse on the validation tenant, so the strings below are the shape that was actually loaded from.
    private const string Ws = "6dede267-a842-49d0-9956-3dcc9f7cecef";
    private const string Item = "3c4e8846-4b8f-4c8d-ada3-0d8233cdbe1e";

    // ---- the two spellings name one place -------------------------------------------------------------

    [Fact]
    public void Abfss_input_yields_both_forms()
    {
        var s = OneLakeStagingLocation.Parse($"abfss://{Ws}@{Host}/{Item}/Files/stage");
        Assert.Equal($"abfss://{Ws}@{Host}/{Item}/Files/stage", s.ClientRoot);
        Assert.Equal($"https://{Host}/{Ws}/{Item}/Files/stage", s.LoadRoot);
    }

    [Fact]
    public void Https_input_yields_both_forms()
    {
        var s = OneLakeStagingLocation.Parse($"https://{Host}/{Ws}/{Item}/Files/stage");
        Assert.Equal($"abfss://{Ws}@{Host}/{Item}/Files/stage", s.ClientRoot);
        Assert.Equal($"https://{Host}/{Ws}/{Item}/Files/stage", s.LoadRoot);
    }

    /// <summary>Either spelling in, the same pair out — so it does not matter which one the user copied.</summary>
    [Fact]
    public void The_two_spellings_are_interchangeable_inputs()
    {
        var fromAbfss = OneLakeStagingLocation.Parse($"abfss://{Ws}@{Host}/{Item}/Files/stage");
        var fromHttps = OneLakeStagingLocation.Parse(fromAbfss.LoadRoot);
        Assert.Equal(fromAbfss, fromHttps);
    }

    [Fact]
    public void Guid_workspace_and_item_survive_unchanged()
    {
        var s = OneLakeStagingLocation.Parse(
            $"abfss://6dede267-a842-49d0-9956-3dcc9f7cecef@{Host}/11112222-3333-4444-5555-666677778888/Files/stg");
        Assert.Equal($"https://{Host}/6dede267-a842-49d0-9956-3dcc9f7cecef/" +
                     "11112222-3333-4444-5555-666677778888/Files/stg", s.LoadRoot);
    }

    [Theory]
    [InlineData("abfss://" + Ws + "@" + Host + "/" + Item + "/Files/stage/")]
    [InlineData("abfss://" + Ws + "@" + Host + "/" + Item + "/Files/stage//")]
    [InlineData("  abfss://" + Ws + "@" + Host + "/" + Item + "/Files/stage  ")]
    [InlineData(@"abfss://" + Ws + "@" + Host + @"\" + Item + @"\Files\stage")]
    public void Trailing_slashes_whitespace_and_backslashes_normalise(string input)
    {
        var s = OneLakeStagingLocation.Parse(input);
        Assert.Equal($"abfss://{Ws}@{Host}/{Item}/Files/stage", s.ClientRoot);
        Assert.Equal($"https://{Host}/{Ws}/{Item}/Files/stage", s.LoadRoot);
    }

    /// <summary>A plain ADLS Gen2 account is a legitimate staging target — nothing here is OneLake-only
    /// except the Tables/ refusal below.</summary>
    [Fact]
    public void A_plain_adls_account_is_accepted()
    {
        var s = OneLakeStagingLocation.Parse("abfss://fs@acct.dfs.core.windows.net/stage/warehouse");
        Assert.Equal("https://acct.dfs.core.windows.net/fs/stage/warehouse", s.LoadRoot);
    }

    // ---- refusals: the inputs that would otherwise fail SILENTLY ---------------------------------------

    [Theory]
    [InlineData("abfss://" + Ws + "@" + Host + "/" + Item + "/Files/_stage")]
    [InlineData("abfss://" + Ws + "@" + Host + "/" + Item + "/Files/.stage")]
    [InlineData("abfss://" + Ws + "@" + Host + "/" + Item + "/_Files/stage")] // ANY segment, not just the last
    public void A_hidden_path_segment_is_refused(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => OneLakeStagingLocation.Parse(input));
        Assert.Contains("COPY INTO ignores such names", ex.Message);
    }

    [Fact]
    public void Staging_into_a_lakehouse_Tables_area_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => OneLakeStagingLocation.Parse($"abfss://{Ws}@{Host}/{Item}/Tables/stage"));
        Assert.Contains("Files/", ex.Message);
    }

    /// <summary>The Tables/ rule is a OneLake convention and must not leak onto a plain storage account,
    /// where a container may legitimately hold a folder of that name.</summary>
    [Fact]
    public void Tables_is_allowed_on_a_non_onelake_account()
    {
        var s = OneLakeStagingLocation.Parse("abfss://fs@acct.dfs.core.windows.net/Tables/stage");
        Assert.Equal("https://acct.dfs.core.windows.net/fs/Tables/stage", s.LoadRoot);
    }

    /// <summary>
    /// ⚠ A OneLake staging root named by DISPLAY NAME is refused — the case that cost a live round trip to
    /// find. Both spellings stage parquet identically; only the warehouse reading it can tell them apart, and
    /// it says <c>13840: Access token couldn't be fetched … unsupported URL or cause of a transient error</c>,
    /// which reads like a permissions or outage problem.
    /// </summary>
    [Theory]
    [InlineData("abfss://Test@" + Host + "/LH.Lakehouse/Files/stage")]         // both names
    [InlineData("abfss://Test@" + Host + "/" + Item + "/Files/stage")]         // workspace named
    [InlineData("abfss://" + Ws + "@" + Host + "/LH.Lakehouse/Files/stage")]   // item named
    [InlineData("https://" + Host + "/Test/LH.Lakehouse/Files/stage")]         // the https spelling too
    [InlineData("abfss://Test@" + Host + "/LH.Lakehouse")]                     // no area segment at all
    public void A_onelake_root_named_by_display_name_is_refused(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => OneLakeStagingLocation.Parse(input));
        Assert.Contains("DISPLAY NAME", ex.Message);
        Assert.Contains("13840", ex.Message);
    }

    /// <summary>Only the CANONICAL 8-4-4-4-12 form counts. <c>Guid.TryParse</c> also accepts braced,
    /// parenthesised and 32-digit spellings — none of which appears in a OneLake URL, so accepting them would
    /// wave through a root the warehouse would then reject.</summary>
    [Theory]
    [InlineData("{6dede267-a842-49d0-9956-3dcc9f7cecef}")]
    [InlineData("6dede267a84249d099563dcc9f7cecef")]
    public void A_non_canonical_guid_spelling_is_refused(string workspace)
    {
        Assert.Throws<ArgumentException>(
            () => OneLakeStagingLocation.Parse($"abfss://{workspace}@{Host}/{Item}/Files/stage"));
    }

    /// <summary>The GUID rule is OneLake's, not ours: a plain ADLS account has display names nowhere in its
    /// URL, so a container called <c>fs</c> must keep working.</summary>
    [Fact]
    public void The_guid_rule_does_not_leak_onto_a_plain_adls_account()
    {
        var s = OneLakeStagingLocation.Parse("abfss://myfs@acct.dfs.core.windows.net/some/named/path");
        Assert.Equal("https://acct.dfs.core.windows.net/myfs/some/named/path", s.LoadRoot);
    }

    // ---- malformed input names what was expected ------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/local/path")]
    [InlineData("s3://bucket/stage")]                       // a real scheme, just not one COPY INTO reads
    [InlineData("onelake://Test/LH.Lakehouse/Files/stage")] // our own shorthand — not a storage URL
    [InlineData("abfss://Test/LH.Lakehouse/Files/stage")]   // no @host
    [InlineData("abfss://Test@" + Host)]                    // no path
    [InlineData("https://" + Host)]                         // no filesystem
    [InlineData("https://" + Host + "/Test")]               // filesystem but no path inside it
    public void Malformed_input_throws(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => OneLakeStagingLocation.Parse(input));
        Assert.Contains("mssql_copy_into_staging", ex.Message);
    }
}
