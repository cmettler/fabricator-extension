using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Fabric.Api.Core;
using Microsoft.Fabric.Api.Lakehouse;

namespace ArrowNet.Bridge;

/// <summary>
/// OneLake (Microsoft Fabric) lakehouse support for the Delta folder-as-catalog provider. DuckDB's azure
/// extension cannot enumerate a OneLake <c>_delta_log</c> tree with a mid-path wildcard glob (the
/// <c>type must be string, but is null</c> bug, duckdb-azure PR #174), so OneLake roots discover their tables
/// via the <b>Fabric REST API</b> (<see cref="TablesClient.ListTables"/>) instead of <see cref="HostFs.Glob"/>.
/// Local / S3 / plain ADLS roots keep the glob (see <c>DeltaCatalog.DiscoverTables</c>). The data files are
/// still read/written through DuckDB's FileSystem (the opener + a DuckDB azure secret); the Fabric API is used
/// ONLY to list table names. Auth = the azure service-principal secret passed to the ATTACH (carried to the
/// catalog as a credential marker on the connection string, mirroring the DAX provider).
/// </summary>
internal static class FabricLakehouse
{
    private const string OneLakeHost = "onelake."; // onelake.dfs.fabric.microsoft.com / onelake.blob...
    // A credential marker appended to the Delta connection string by DeltaBackend.BuildConnectionString when an
    // azure SP secret is supplied — base64 JSON of the secret fields, extracted by the catalog (see Extract).
    private const string CredMarker = ";ArrowNetDeltaCred=";

    /// <summary>True when <paramref name="path"/> targets a OneLake (Fabric) endpoint.</summary>
    public static bool IsOneLake(string path) =>
        path.IndexOf(OneLakeHost, StringComparison.OrdinalIgnoreCase) >= 0;

    // ---- credential marker (mirrors DaxTokenAuth) ----

    /// <summary>Appends the azure-secret fields as a credential marker to the Delta root connection string, so
    /// the catalog can mint a Fabric REST API token (service principal). No-op for non-azure / empty fields.</summary>
    public static string AppendCredMarker(string root, IReadOnlyDictionary<string, string> fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return root;
        }
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)));
        return root + CredMarker + b64;
    }

    /// <summary>Splits a connection string into the bare root path and an optional Fabric API credential.</summary>
    public static (string Root, TokenCredential? Credential) Extract(string connectionString)
    {
        int idx = connectionString.IndexOf(CredMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return (connectionString, null);
        }
        var root = connectionString.Substring(0, idx);
        var b64 = connectionString.Substring(idx + CredMarker.Length);
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(
                         Encoding.UTF8.GetString(Convert.FromBase64String(b64)))
                     ?? new Dictionary<string, string>();
        return (root, BuildCredential(fields));
    }

    private static TokenCredential BuildCredential(IReadOnlyDictionary<string, string> fields)
    {
        string F(string k) => fields.TryGetValue(k, out var v) ? v ?? string.Empty : string.Empty;
        var tenantId = F("tenant_id");
        var clientId = F("client_id");
        var clientSecret = F("client_secret");
        if (clientSecret.Length > 0 && clientId.Length > 0 && tenantId.Length > 0)
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }
        // No SP fields → fall back to the ambient chain (az login / managed identity / env).
        return new DefaultAzureCredential();
    }

    // ---- table discovery ----

    /// <summary>The resolved shape of a OneLake lakehouse: whether schemas are enabled, the OneLake Tables path,
    /// and the discovered (schema, table) pairs. For a non-schema lakehouse the schema is always
    /// <see cref="DeltaCatalog.MainSchema"/> ("main").</summary>
    internal sealed class OneLakeInfo
    {
        public bool SchemaEnabled { get; init; }
        public string TablesPath { get; init; } = string.Empty; // abfss …/Tables, no trailing slash
        public List<(string Schema, string Table)> Tables { get; init; } = new();
        // The lakehouse's default schema (schema-enabled only) — always surfaced so CREATE works on an
        // otherwise-empty schema. Null/empty for non-schema lakehouses.
        public string? DefaultSchema { get; init; }
    }

    /// <summary>Resolves the OneLake lakehouse at <paramref name="root"/> and discovers its tables. A
    /// <b>schema-enabled</b> lakehouse (Fabric `GetLakehouse.DefaultSchema` set) is enumerated via its SQL
    /// analytics endpoint's INFORMATION_SCHEMA (the Fabric ListTables API returns 400 for it, and OneLake glob
    /// returns nothing) → tables at <c>Tables/&lt;schema&gt;/&lt;table&gt;</c>; a <b>non-schema</b> lakehouse uses
    /// the Fabric `TablesClient.ListTables` API → flat <c>Tables/&lt;table&gt;</c> under DuckDB schema "main".
    /// <paramref name="credential"/> authenticates both the Fabric API and (token) the SQL endpoint.</summary>
    public static OneLakeInfo Resolve(string root, TokenCredential? credential)
    {
        var cred = credential ?? new DefaultAzureCredential();
        var (workspaceSeg, lakehouseSeg) = ParseOneLake(root);
        Guid workspaceId = ResolveWorkspaceId(workspaceSeg, cred);
        Guid lakehouseId = ResolveLakehouseId(workspaceId, lakehouseSeg, cred);

        var lakehouse = new Microsoft.Fabric.Api.Lakehouse.ItemsClient(cred)
                            .GetLakehouse(workspaceId, lakehouseId).Value;
        var props = lakehouse.Properties;
        string tablesPath = (props?.OneLakeTablesPath ?? root).Replace('\\', '/').TrimEnd('/');
        bool schemaEnabled = !string.IsNullOrEmpty(props?.DefaultSchema);

        var tables = new List<(string, string)>();
        if (schemaEnabled)
        {
            // SQL-endpoint discovery: the lakehouse SQL endpoint's database is the lakehouse name.
            var server = props!.SqlEndpointProperties?.ConnectionString;
            if (string.IsNullOrEmpty(server))
            {
                throw new InvalidOperationException(
                    $"delta(onelake): schema-enabled lakehouse '{lakehouse.DisplayName}' has no SQL endpoint yet " +
                    "(still provisioning?) — cannot discover tables.");
            }
            foreach (var (schema, table) in ListSchemaTablesViaSql(server!, lakehouse.DisplayName, cred))
            {
                tables.Add((schema, table));
            }
        }
        else
        {
            foreach (var t in new TablesClient(cred).ListTables(workspaceId, lakehouseId))
            {
                if (!string.IsNullOrEmpty(t.Name))
                {
                    tables.Add((DeltaCatalog.MainSchema, t.Name));
                }
            }
        }
        return new OneLakeInfo
        {
            SchemaEnabled = schemaEnabled,
            TablesPath = tablesPath,
            Tables = tables,
            DefaultSchema = schemaEnabled ? props!.DefaultSchema : null,
        };
    }

    private const string SqlScope = "https://database.windows.net/.default";

    /// <summary>Lists (schema, table) for BASE TABLEs of a Fabric lakehouse/warehouse SQL endpoint via
    /// INFORMATION_SCHEMA, authenticating with an Entra token minted from <paramref name="cred"/>.</summary>
    private static IEnumerable<(string Schema, string Table)> ListSchemaTablesViaSql(
        string server, string database, TokenCredential cred)
    {
        var token = cred.GetToken(new TokenRequestContext(new[] { SqlScope }), default).Token;
        var connString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = true,
            ConnectTimeout = 60,
        }.ConnectionString;

        var result = new List<(string, string)>();
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(connString) { AccessToken = token };
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    /// <summary>Parses <c>abfss://&lt;workspace&gt;@onelake.dfs.fabric.microsoft.com/&lt;lakehouse&gt;/…</c> into the
    /// workspace segment (before <c>@</c>) and the lakehouse segment (first path segment after the host).</summary>
    private static (string Workspace, string Lakehouse) ParseOneLake(string root)
    {
        var path = root.Replace('\\', '/');
        int scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            throw new ArgumentException($"delta(onelake): not an abfss URL: '{root}'");
        }
        var authorityAndPath = path.Substring(scheme + 3);
        int at = authorityAndPath.IndexOf('@');
        int firstSlash = authorityAndPath.IndexOf('/');
        if (at < 0 || firstSlash < 0 || at > firstSlash)
        {
            throw new ArgumentException(
                $"delta(onelake): expected abfss://<workspace>@onelake…/<lakehouse>/Tables, got '{root}'");
        }
        var workspace = authorityAndPath.Substring(0, at);
        var rest = authorityAndPath.Substring(firstSlash + 1); // <lakehouse>/Tables/...
        int seg = rest.IndexOf('/');
        var lakehouse = seg < 0 ? rest : rest.Substring(0, seg);
        if (workspace.Length == 0 || lakehouse.Length == 0)
        {
            throw new ArgumentException($"delta(onelake): could not parse workspace/lakehouse from '{root}'");
        }
        return (workspace, lakehouse);
    }

    private static Guid ResolveWorkspaceId(string segment, TokenCredential cred)
    {
        if (Guid.TryParse(segment, out var id))
        {
            return id;
        }
        var workspaces = new WorkspacesClient(cred);
        foreach (var w in workspaces.ListWorkspaces())
        {
            if (string.Equals(w.DisplayName, segment, StringComparison.OrdinalIgnoreCase))
            {
                return w.Id;
            }
        }
        throw new ArgumentException($"delta(onelake): workspace '{segment}' not found via the Fabric API.");
    }

    private static Guid ResolveLakehouseId(Guid workspaceId, string segment, TokenCredential cred)
    {
        // A lakehouse path segment is "<name>.Lakehouse" or a GUID.
        var name = segment;
        if (name.EndsWith(".Lakehouse", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - ".Lakehouse".Length);
        }
        if (Guid.TryParse(name, out var id))
        {
            return id;
        }
        var items = new Microsoft.Fabric.Api.Core.ItemsClient(cred);
        foreach (var it in items.ListItems(workspaceId, type: "Lakehouse", recursive: null, rootFolderId: null,
                                            continuationToken: null!, cancellationToken: default))
        {
            if (it.Id is { } gid && string.Equals(it.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            {
                return gid;
            }
        }
        throw new ArgumentException(
            $"delta(onelake): lakehouse '{name}' not found in the workspace via the Fabric API.");
    }
}
