// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// Capabilities of the connected SQL Server engine, detected once at catalog open from
/// <c>SERVERPROPERTY</c> + the database collation. Drives connection behavior (MARS, isolation)
/// and type mapping (VARCHAR vs NVARCHAR, datetime2 scale, datetimeoffset, native json). Box SQL
/// Server / Azure SQL Database differ from Synapse dedicated pools and Fabric Warehouse / Lakehouse
/// SQL endpoints. See docs/warehouse-support.md.
/// </summary>
internal sealed class ServerProfile
{
    // SERVERPROPERTY('EngineEdition') values. The capability flags below derive from edition + version
    // + collation TOGETHER, never a single brittle number, so refining one rule here is enough.
    // CONFIRMED live against a Fabric Warehouse: EngineEdition 11, ProductMajorVersion 12 (ProductVersion
    // 12.0.x), collation Latin1_General_100_BIN2_UTF8 -> supports_mars/has_nvarchar/has_datetimeoffset
    // all false, max_datetime2_scale 6, UTF-8 + binary + case-sensitive.
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

    /// <summary>
    /// Change data capture exists on this engine — the whole <c>db.cdc.*</c> surface is gated on it.
    /// FALSE for the entire warehouse family: Fabric Warehouse, the Fabric Lakehouse SQL endpoint (both
    /// edition 11) and Synapse dedicated (6) have no <c>cdc</c> schema, no <c>sp_cdc_enable_db</c>, no
    /// capture job, and on Fabric no <c>msdb</c> for job metadata to live in. See docs/mssql-cdc.md §0.1.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠ This MUST stay a capability gate and must never become a try/catch probe.</b> On a
    /// warehouse engine a statement that errors inside an explicit transaction ABORTS the transaction, so a
    /// swallowed "is CDC here?" probe poisons whatever the caller does next — which is exactly how every dbt
    /// table model on Fabric came to die at the swap with error 15225 (two best-effort probes whose failures
    /// were being ignored). docs/warehouse-support.md §6.5: on a warehouse engine, never issue a statement
    /// whose failure you intend to swallow.</para>
    /// <para>Azure SQL Database (edition 5) is on the TRUE side and is the interesting middle case: CDC works
    /// there but there is no SQL Server Agent, so the health surface must degrade rather than report the agent
    /// as stopped. UNMEASURED — no live Azure SQL Database here (§0.1).</para>
    /// </remarks>
    public bool SupportsCdc => !IsWarehouse;

    /// <summary>
    /// Isolation level for the pinned WRITE transaction (BeginWrite). Fabric Warehouse / Lakehouse SQL
    /// endpoint only support SNAPSHOT, so we set it explicitly there; box SQL Server / Azure SQL DB /
    /// Synapse dedicated keep the connection/server default (empty => Unspecified). Synapse dedicated is
    /// intentionally NOT snapshot here — its default is READ UNCOMMITTED and snapshot may be disabled.
    /// </summary>
    public string DefaultWriteIsolation => EngineEdition == EditionFabricOrSynapseServerless ? "snapshot" : string.Empty;

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
    /// The profile as ordered (property, value) text rows — surfaced by the <c>fabricator_server_info(catalog)</c>
    /// diagnostic. Booleans render as "true"/"false".
    /// </summary>
    public IReadOnlyList<(string Property, string Value)> Properties() => new[]
    {
        ("engine_edition", EngineEdition.ToString()),
        ("product_major_version", ProductMajorVersion.ToString()),
        ("product_version", ProductVersion),
        ("collation", Collation),
        ("is_warehouse", Bool(IsWarehouse)),
        ("supports_mars", Bool(SupportsMars)),
        ("has_nvarchar", Bool(HasNVarchar)),
        ("has_datetimeoffset", Bool(HasDatetimeOffset)),
        ("max_datetime2_scale", MaxDateTime2Scale.ToString()),
        ("has_native_json", Bool(HasNativeJson)),
        ("supports_cdc", Bool(SupportsCdc)),
        ("is_utf8_collation", Bool(IsUtf8Collation)),
        ("is_binary_collation", Bool(IsBinaryCollation)),
        ("is_case_sensitive", Bool(IsCaseSensitive)),
        ("default_write_isolation", DefaultWriteIsolation.Length == 0 ? "(default)" : DefaultWriteIsolation),
    };

    private static string Bool(bool value) => value ? "true" : "false";

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
