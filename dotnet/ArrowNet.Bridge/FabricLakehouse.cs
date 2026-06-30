using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Files.DataLake;
using Microsoft.Fabric.Api.Core;
using Microsoft.Fabric.Api.Lakehouse;

namespace ArrowNet.Bridge;

/// <summary>
/// OneLake (Microsoft Fabric) lakehouse support for the Delta folder-as-catalog provider. DuckDB's azure
/// extension cannot enumerate a OneLake <c>_delta_log</c> tree with a mid-path wildcard glob (the
/// <c>type must be string, but is null</c> bug, duckdb-azure PR #174), so OneLake roots discover their tables —
/// and DROP them — through the <b>ADLS Gen2 / OneLake DFS endpoint directly</b> (the Azure SDK
/// <see cref="DataLakeFileSystemClient"/>, NOT DuckDB's azure extension): <c>GetPaths</c> lists
/// <c>Tables/&lt;table&gt;</c> (flat) or <c>Tables/&lt;schema&gt;/&lt;table&gt;</c> (schema-enabled), and
/// <see cref="DeleteDirectory"/> recursively deletes a table folder (DuckDB's azure FileSystem has no
/// <c>RemoveDirectory</c>). This is immune to the glob bug AND the Fabric <c>ListTables</c> 400 on
/// schema-enabled lakehouses, and free of the SQL-endpoint sync lag. The <b>Fabric REST API</b> is kept only for
/// the schema-enabled flag (<c>GetLakehouse.DefaultSchema</c>) + workspace/lakehouse name→GUID resolution.
/// Local / S3 / plain ADLS roots keep the host-FS glob (see <c>DeltaCatalog.DiscoverTables</c>). Table data is
/// still read/written through DuckDB's FileSystem (the opener + a DuckDB azure secret). Auth = the azure
/// service-principal secret passed to the ATTACH (carried to the catalog as a credential marker on the
/// connection string, mirroring the DAX provider) — the SAME credential serves the Fabric API + the DFS endpoint.
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

    /// <summary>Resolves the OneLake lakehouse at <paramref name="root"/> and discovers its tables via the DFS
    /// endpoint. The <b>schema-enabled</b> flag comes from Fabric `GetLakehouse.DefaultSchema`; the table list
    /// comes from <c>GetPaths</c> on the OneLake DFS endpoint — schema-enabled → list schema dirs under
    /// <c>Tables/</c> then table dirs under each (<c>Tables/&lt;schema&gt;/&lt;table&gt;</c>), flat → table dirs
    /// under <c>Tables/</c> (<c>Tables/&lt;table&gt;</c>) under DuckDB schema "main". <paramref name="credential"/>
    /// authenticates both the Fabric API and the DFS endpoint.</summary>
    public static OneLakeInfo Resolve(string root, TokenCredential? credential)
    {
        var cred = credential ?? new DefaultAzureCredential();
        var (workspaceSeg, lakehouseSeg) = ParseOneLake(root);
        Guid workspaceId = ResolveWorkspaceId(workspaceSeg, cred);
        Guid lakehouseId = ResolveLakehouseId(workspaceId, lakehouseSeg, cred);

        // The schema-enabled flag is authoritative from the lakehouse definition (handles an EMPTY lakehouse,
        // where the DFS structure alone is ambiguous). Table enumeration is then pure-DFS.
        var lakehouse = new Microsoft.Fabric.Api.Lakehouse.ItemsClient(cred)
                            .GetLakehouse(workspaceId, lakehouseId).Value;
        var props = lakehouse.Properties;
        string tablesPath = root.Replace('\\', '/').TrimEnd('/');
        bool schemaEnabled = !string.IsNullOrEmpty(props?.DefaultSchema);

        var (host, fileSystem, tablesUnderFs) = ParseAbfss(tablesPath);
        var fsClient = new DataLakeFileSystemClient(new Uri($"https://{host}/{fileSystem}"), cred);

        var tables = new List<(string, string)>();
        if (schemaEnabled)
        {
            foreach (var schema in ListChildDirectories(fsClient, tablesUnderFs))
            {
                foreach (var table in ListChildDirectories(fsClient, tablesUnderFs + "/" + schema))
                {
                    tables.Add((schema, table));
                }
            }
        }
        else
        {
            foreach (var table in ListChildDirectories(fsClient, tablesUnderFs))
            {
                tables.Add((DeltaCatalog.MainSchema, table));
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

    /// <summary>Lists the immediate child <b>directory</b> leaf-names of <paramref name="pathUnderFs"/> (a path
    /// relative to the filesystem root) via the OneLake DFS endpoint (non-recursive). Returns empty when the
    /// directory does not exist yet (a brand-new lakehouse / empty schema). Uses the <b>async</b> Azure API
    /// bridged once with <c>GetAwaiter().GetResult()</c>: the Azure SDK's SYNC <c>GetPaths</c> uses
    /// <c>HttpClient.Send</c>, whose sync transport hangs under the hostfxr-hosted CLR (every other Bridge IO
    /// path is async for the same reason); the async path uses <c>SendAsync</c> and works.</summary>
    private static IEnumerable<string> ListChildDirectories(DataLakeFileSystemClient fsClient, string pathUnderFs) =>
        ListChildDirectoriesAsync(fsClient, pathUnderFs).GetAwaiter().GetResult();

    private static async System.Threading.Tasks.Task<List<string>> ListChildDirectoriesAsync(
        DataLakeFileSystemClient fsClient, string pathUnderFs)
    {
        var names = new List<string>();
        try
        {
            await foreach (var item in fsClient.GetPathsAsync(path: pathUnderFs, recursive: false)
                               .ConfigureAwait(false))
            {
                if (item.IsDirectory == true && !string.IsNullOrEmpty(item.Name))
                {
                    int slash = item.Name.LastIndexOf('/');
                    names.Add(slash < 0 ? item.Name : item.Name.Substring(slash + 1));
                }
            }
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // The directory doesn't exist yet (no tables / schema not materialized) — treat as empty.
        }
        return names;
    }

    /// <summary>Recursively deletes the OneLake directory at the abfss <paramref name="abfssDir"/> (a table
    /// folder) via the DFS endpoint — DuckDB's azure FileSystem has no RemoveDirectory. Idempotent (no error if
    /// the directory is already gone), so it satisfies DROP TABLE IF EXISTS. Async API (see
    /// <see cref="ListChildDirectories"/> for why sync hangs under the CLR).</summary>
    public static void DeleteDirectory(string abfssDir, TokenCredential? credential)
    {
        var cred = credential ?? new DefaultAzureCredential();
        var (host, fileSystem, pathUnderFs) = ParseAbfss(abfssDir.Replace('\\', '/').TrimEnd('/'));
        var dirClient = new DataLakeDirectoryClient(
            new Uri($"https://{host}/{fileSystem}/{pathUnderFs}"), cred);
        dirClient.DeleteIfExistsAsync().GetAwaiter().GetResult(); // directory delete on DFS is recursive
    }

    /// <summary>Parses <c>abfss://&lt;filesystem&gt;@&lt;host&gt;/&lt;path&gt;</c> into (host, filesystem, path).
    /// For OneLake the filesystem is the workspace and the path is <c>&lt;lakehouse&gt;.Lakehouse/Tables/…</c>.</summary>
    private static (string Host, string FileSystem, string Path) ParseAbfss(string abfss)
    {
        int scheme = abfss.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            throw new ArgumentException($"delta(onelake): not an abfss URL: '{abfss}'");
        }
        var authorityAndPath = abfss.Substring(scheme + 3);
        int at = authorityAndPath.IndexOf('@');
        int firstSlash = authorityAndPath.IndexOf('/');
        if (at < 0 || firstSlash < 0 || at > firstSlash)
        {
            throw new ArgumentException($"delta(onelake): expected abfss://<fs>@<host>/<path>, got '{abfss}'");
        }
        var fileSystem = authorityAndPath.Substring(0, at);
        var host = authorityAndPath.Substring(at + 1, firstSlash - at - 1);
        var path = authorityAndPath.Substring(firstSlash + 1);
        return (host, fileSystem, path);
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
