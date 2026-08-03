using System;
using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// Offline tests for <see cref="FabricSqlEndpointHost"/> — the derivation of a Fabric workspace id and item
/// name from a SQL connection string.
/// </summary>
/// <remarks>
/// <para>This is the piece of the Fabric binding that most deserves offline tests, because it rests on an
/// <b>undocumented</b> encoding observed on ONE tenant. Live validation proves it works today; only these tests
/// pin what happens when it does not — and the required behaviour there is to return NULL, never a guess. A
/// wrong workspace id would point REST calls at a different workspace the identity may well have access to, so
/// every malformed-input case below is a real safety assertion rather than input hygiene.</para>
/// <para>The anchor case is the MEASURED one: host
/// <c>dr2gzgsxhu2evij6vtiwxf2bby-m7ro23kcvdietgkwhxgj67hm54</c> ⇒ workspace
/// <c>6dede267-a842-49d0-9956-3dcc9f7cecef</c>, confirmed against the live tenant (all three lakehouses and a
/// warehouse in that workspace share that host, and their own endpoint ids match neither segment).</para>
/// </remarks>
public class SqlEndpointHostTests
{
    private const string MeasuredHost =
        "dr2gzgsxhu2evij6vtiwxf2bby-m7ro23kcvdietgkwhxgj67hm54.datawarehouse.fabric.microsoft.com";

    private static readonly Guid MeasuredWorkspace = new("6dede267-a842-49d0-9956-3dcc9f7cecef");

    // ---- the workspace decode -------------------------------------------------------------------------

    [Fact]
    public void Decodes_the_measured_host_to_the_measured_workspace_id()
    {
        Assert.Equal(MeasuredWorkspace, FabricSqlEndpointHost.WorkspaceIdFromHost(MeasuredHost));
    }

    [Fact]
    public void Reads_the_workspace_out_of_a_full_connection_string()
    {
        var cs = $"Server={MeasuredHost};Database=LH;Encrypt=true";
        var host = FabricSqlEndpointHost.ServerFromConnectionString(cs);
        Assert.Equal(MeasuredWorkspace, FabricSqlEndpointHost.WorkspaceIdFromHost(host));
    }

    [Theory]
    [InlineData("tcp:" + MeasuredHost)]
    [InlineData(MeasuredHost + ",1433")]
    [InlineData("tcp:" + MeasuredHost + ",1433")]
    public void Tolerates_the_protocol_prefix_and_port_forms_SqlClient_accepts(string server)
    {
        var host = FabricSqlEndpointHost.ServerFromConnectionString($"Server={server};Database=LH");
        Assert.Equal(MeasuredWorkspace, FabricSqlEndpointHost.WorkspaceIdFromHost(host));
    }

    [Fact]
    public void Is_case_insensitive_because_dns_is()
    {
        Assert.Equal(MeasuredWorkspace, FabricSqlEndpointHost.WorkspaceIdFromHost(MeasuredHost.ToUpperInvariant()));
    }

    // ---- everything that must yield NULL rather than a guess ------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A non-Fabric host: a plain SQL Server or Azure SQL must never be read as a workspace.
    [InlineData("localhost")]
    [InlineData("myserver.database.windows.net")]
    // Fabric host but only ONE base32 label — no workspace segment to take.
    [InlineData("dr2gzgsxhu2evij6vtiwxf2bby.datawarehouse.fabric.microsoft.com")]
    // Three segments: the shape changed, so the second is no longer known to be the workspace.
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbbbbbbbbbbbb-cccccccccccccccccccccccccc.datawarehouse.fabric.microsoft.com")]
    // Right shape, wrong LENGTH — 25 and 27 characters cannot be a 16-byte GUID.
    [InlineData("dr2gzgsxhu2evij6vtiwxf2bby-m7ro23kcvdietgkwhxgj67hm5.datawarehouse.fabric.microsoft.com")]
    [InlineData("dr2gzgsxhu2evij6vtiwxf2bby-m7ro23kcvdietgkwhxgj67hm544.datawarehouse.fabric.microsoft.com")]
    public void Refuses_to_guess(string? host)
    {
        Assert.Null(FabricSqlEndpointHost.WorkspaceIdFromHost(host));
    }

    [Theory]
    // 0, 1, 8 and 9 are OUTSIDE the RFC-4648 base32 alphabet. A segment containing one is not base32, and
    // silently mapping it to some value would fabricate a workspace id.
    [InlineData('0')]
    [InlineData('1')]
    [InlineData('8')]
    [InlineData('9')]
    [InlineData('_')]
    [InlineData('+')]
    public void Rejects_characters_outside_the_base32_alphabet(char bad)
    {
        var segment = new string('a', 25) + bad;
        Assert.Null(FabricSqlEndpointHost.DecodeBase32Guid(segment));
    }

    [Fact]
    public void Rejects_a_segment_of_the_wrong_length_outright()
    {
        Assert.Null(FabricSqlEndpointHost.DecodeBase32Guid(""));
        Assert.Null(FabricSqlEndpointHost.DecodeBase32Guid(new string('a', 25)));
        Assert.Null(FabricSqlEndpointHost.DecodeBase32Guid(new string('a', 32)));
    }

    /// <summary>
    /// 26 base32 characters carry 130 bits while a GUID is 128, so the trailing 2 bits are padding. This pins
    /// that they are DISCARDED rather than producing a 17th byte (which would throw in the Guid constructor).
    /// </summary>
    [Fact]
    public void Discards_the_two_padding_bits_rather_than_overflowing()
    {
        // 'a' is 0, '7' is 31 — an all-ones tail exercises the bits that must be dropped.
        var decoded = FabricSqlEndpointHost.DecodeBase32Guid(new string('a', 25) + "7");
        Assert.NotNull(decoded);
    }

    // ---- the item, which needs no decoding at all ----------------------------------------------------

    [Theory]
    [InlineData("Server=h;Database=LH", "LH")]
    [InlineData("Server=h;database=lh2", "lh2")]
    [InlineData("Server=h;Initial Catalog=MyWarehouse", "MyWarehouse")]
    [InlineData("Server=h;InitialCatalog=MyWarehouse", "MyWarehouse")]
    [InlineData("Database=OnlyKey", "OnlyKey")]
    [InlineData("Server=h;Database=LH;Encrypt=true", "LH")]
    public void Reads_the_item_from_the_database_keyword(string cs, string expected)
    {
        Assert.Equal(expected, FabricSqlEndpointHost.DatabaseFromConnectionString(cs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Server=h;Encrypt=true")]
    [InlineData("Server=h;Database=")]
    public void Returns_null_when_no_database_is_named(string? cs)
    {
        Assert.Null(FabricSqlEndpointHost.DatabaseFromConnectionString(cs));
    }

    /// <summary>
    /// A quoted value may contain a semicolon — SqlClient allows it, and splitting naively on ';' would
    /// truncate the name (or, worse, read a fragment of a PASSWORD as the item).
    /// </summary>
    [Theory]
    [InlineData("Server=h;Database='odd;name';Encrypt=true", "odd;name")]
    [InlineData("Server=h;Database=\"odd;name\"", "odd;name")]
    [InlineData("Password='pw;ord';Database=LH", "LH")]
    public void Respects_quoting_when_splitting_the_connection_string(string cs, string expected)
    {
        Assert.Equal(expected, FabricSqlEndpointHost.DatabaseFromConnectionString(cs));
    }

    [Fact]
    public void Un_doubles_an_escaped_quote_inside_a_quoted_value()
    {
        Assert.Equal("it's", FabricSqlEndpointHost.DatabaseFromConnectionString("Database='it''s'"));
    }

    [Fact]
    public void Reads_the_server_under_each_alias_SqlClient_accepts()
    {
        foreach (var key in new[] { "Server", "Data Source", "DataSource", "Addr", "Address", "Network Address" })
        {
            Assert.Equal("h.fabric.microsoft.com",
                         FabricSqlEndpointHost.ServerFromConnectionString($"{key}=h.fabric.microsoft.com;Database=LH"));
        }
    }
}
