using System;
using Microsoft.Data.SqlClient;

namespace ArrowNet.SqlServer;

/// <summary>
/// Capabilities of the connected SQL Server engine, detected once at catalog open from
/// <c>SERVERPROPERTY</c> + the database collation. Drives connection behavior (MARS, isolation)
/// and type mapping (VARCHAR vs NVARCHAR, datetime2 scale, datetimeoffset, native json). Box SQL
/// Server / Azure SQL Database differ from Synapse dedicated pools and Fabric Warehouse / Lakehouse
/// SQL endpoints. See docs/warehouse-support.md.
/// </summary>
internal sealed class ServerProfile
{
    // SERVERPROPERTY('EngineEdition') values. NOTE: confirm Fabric's value empirically — the
    // capability flags below derive from edition + version + collation TOGETHER, never a single
    // brittle number, so refining one rule here is enough.
    public const int EditionAzureSqlDatabase = 5;
    public const int EditionSynapseDedicated = 6;            // Azure Synapse Analytics — dedicated SQL pool
    public const int EditionFabricOrSynapseServerless = 11;  // Fabric Warehouse + Lakehouse SQL endpoint / Synapse serverless

    public int EngineEdition { get; }
    public int ProductMajorVersion { get; }
    public string ProductVersion { get; }
    public string Collation { get; }

    // ---- derived capabilities (call sites read intent, not raw numbers) ----

    /// <summary>MARS — unsupported on Synapse (dedicated + serverless) and Fabric.</summary>
    public bool SupportsMars { get; }
    /// <summary>NVARCHAR/NCHAR exist (false on Fabric Warehouse / Lakehouse — VARCHAR is UTF-8 there).</summary>
    public bool HasNVarchar { get; }
    /// <summary>DATETIMEOFFSET exists (false on Fabric).</summary>
    public bool HasDatetimeOffset { get; }
    /// <summary>Max datetime2 / time fractional-seconds scale: 6 on Fabric, 7 elsewhere.</summary>
    public int MaxDateTime2Scale { get; }
    /// <summary>Native <c>json</c> type — SQL Server 2025 (major 17+) or Azure SQL Database.</summary>
    public bool HasNativeJson { get; }
    /// <summary>Database collation is UTF-8 (name ends in _UTF8) → VARCHAR holds full Unicode.</summary>
    public bool IsUtf8Collation { get; }
    /// <summary>Database collation is binary (_BIN/_BIN2) → byte-order sort matches DuckDB.</summary>
    public bool IsBinaryCollation { get; }
    /// <summary>Database collation is case-sensitive (_CS, or binary).</summary>
    public bool IsCaseSensitive { get; }

    /// <summary>True for Synapse (dedicated/serverless) + Fabric — the "warehouse" family.</summary>
    public bool IsWarehouse => EngineEdition is EditionSynapseDedicated or EditionFabricOrSynapseServerless;

    private ServerProfile(int engineEdition, int productMajorVersion, string? productVersion, string? collation)
    {
        EngineEdition = engineEdition;
        ProductMajorVersion = productMajorVersion;
        ProductVersion = productVersion ?? string.Empty;
        Collation = collation ?? string.Empty;

        // Fabric Warehouse / Lakehouse SQL endpoint (the type-restricted family). Synapse dedicated
        // (6) keeps box-like types (nvarchar, datetimeoffset, datetime2(7)) but, like Fabric, lacks MARS.
        bool fabric = engineEdition == EditionFabricOrSynapseServerless;
        SupportsMars = engineEdition is not (EditionSynapseDedicated or EditionFabricOrSynapseServerless);
        HasNVarchar = !fabric;
        HasDatetimeOffset = !fabric;
        MaxDateTime2Scale = fabric ? 6 : 7;
        HasNativeJson = !IsWarehouse && (productMajorVersion >= 17 || engineEdition == EditionAzureSqlDatabase);

        // Collation name shape: <base>_<flags...>[_UTF8] — e.g. SQL_Latin1_General_CP1_CI_AS,
        // Latin1_General_100_BIN2_UTF8, Latin1_General_100_CI_AS_KS_WS_SC_UTF8.
        string c = Collation;
        IsUtf8Collation = c.EndsWith("_UTF8", StringComparison.OrdinalIgnoreCase);
        IsBinaryCollation = c.Contains("_BIN", StringComparison.OrdinalIgnoreCase);   // matches _BIN and _BIN2
        IsCaseSensitive = IsBinaryCollation || c.Contains("_CS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Construct from raw values (used by <see cref="Detect"/> and directly in tests).</summary>
    public static ServerProfile FromValues(int engineEdition, int productMajorVersion, string? productVersion,
                                            string? collation)
        => new(engineEdition, productMajorVersion, productVersion, collation);

    /// <summary>
    /// Detect the profile over an already-open connection (one round-trip). SERVERPROPERTY values are
    /// universally available; a null/unclassifiable EngineEdition falls back to box-like (Enterprise = 3)
    /// so we never wrongly disable MARS on a server we couldn't classify. ProductMajorVersion is missing
    /// on older engines (&lt; 2017) — fall back to the leading component of ProductVersion.
    /// </summary>
    public static ServerProfile Detect(SqlConnection openConnection)
    {
        using var cmd = openConnection.CreateCommand();
        cmd.CommandText =
            "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int), " +
            "CAST(SERVERPROPERTY('ProductMajorVersion') AS int), " +
            "CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), " +
            "CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return FromValues(3, 0, string.Empty, string.Empty);
        }
        int engineEdition = reader.IsDBNull(0) ? 3 : reader.GetInt32(0);
        string productVersion = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        int productMajor = reader.IsDBNull(1) ? ParseLeadingInt(productVersion) : reader.GetInt32(1);
        string collation = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        return FromValues(engineEdition, productMajor, productVersion, collation);
    }

    // "16.0.4135.4" -> 16; 0 on anything unparseable.
    private static int ParseLeadingInt(string version)
    {
        int dot = version.IndexOf('.');
        string head = dot >= 0 ? version.Substring(0, dot) : version;
        return int.TryParse(head, out int v) ? v : 0;
    }
}
