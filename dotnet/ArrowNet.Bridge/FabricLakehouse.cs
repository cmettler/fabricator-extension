using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
public static class FabricLakehouse
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

    // Delegates to the shared resolver (SP / managed_identity / DefaultAzureCredential) — one credential model
    // across DAX / OneLake. See FabricCredentialResolver.
    private static TokenCredential BuildCredential(IReadOnlyDictionary<string, string> fields)
        => FabricCredentialResolver.Resolve(fields);

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
    internal static OneLakeInfo Resolve(string root, TokenCredential? credential)
    {
        var cred = credential ?? FabricCredentialResolver.AmbientChain();
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

        // Table enumeration via the OneLake Unity Catalog REST API (schemas + tables, paginated) — NOT DFS
        // GetPaths. The UC endpoint lists tables directly (one call per schema, following next_page_token),
        // immune to the duckdb-azure mid-path-glob bug and the DFS-recursion cost. A flat lakehouse's tables
        // are reported by UC under schema "dbo" but map to our "main" (their storage_location has no schema
        // segment, which the flat TablePath already produces). The schema-enabled flag stays authoritative
        // from the lakehouse definition.
        string catalogName = (lakehouse.DisplayName ?? lakehouseSeg) + ".Lakehouse";
        var ucTables = ListTablesViaUnityCatalog(workspaceId, lakehouseId, catalogName, cred);
        var tables = new List<(string, string)>();
        foreach (var (schema, table, _) in ucTables)
        {
            tables.Add((schemaEnabled ? schema : DeltaCatalog.MainSchema, table));
        }
        return new OneLakeInfo
        {
            SchemaEnabled = schemaEnabled,
            TablesPath = tablesPath,
            Tables = tables,
            DefaultSchema = schemaEnabled ? props!.DefaultSchema : null,
        };
    }

    /// <summary>Resolves a OneLake lakehouse for the <b>delta-rs</b> provider: workspace + lakehouse GUIDs,
    /// the schema-enabled flag, and the table list (via the Unity Catalog REST API). delta-rs's object_store
    /// reads OneLake only with a <b>GUID-based</b> abfss path (<c>abfss://&lt;wsGuid&gt;@onelake.dfs.fabric.
    /// microsoft.com/&lt;lhGuid&gt;/Tables/…</c>), so the caller builds table paths from these GUIDs. Builds a
    /// <see cref="ClientSecretCredential"/> from the SP fields (else <see cref="DefaultAzureCredential"/>).</summary>
    /// <summary>A service-principal credential from the SP fields, or <see cref="DefaultAzureCredential"/> when
    /// they're absent (keeps Azure.Identity in the Bridge so the delta-rs provider passes plain strings).</summary>
    public static TokenCredential MintCredential(string? tenantId, string? clientId, string? clientSecret)
        => FabricCredentialResolver.MintCredential(tenantId, clientId, clientSecret);

    /// <summary>DROP a OneLake table folder (recursive DFS delete) using SP fields — for the delta-rs provider,
    /// which holds the SP creds as strings, not a <see cref="TokenCredential"/>.</summary>
    public static void DeleteOneLakeDirectory(string abfssDir, string? tenantId, string? clientId, string? clientSecret) =>
        DeleteDirectory(abfssDir, MintCredential(tenantId, clientId, clientSecret));

    /// <summary>RENAME a OneLake table folder (atomic DFS rename) using SP fields.</summary>
    public static void RenameOneLakeDirectory(string abfssSrc, string abfssDest,
                                              string? tenantId, string? clientId, string? clientSecret) =>
        RenameDirectory(abfssSrc, abfssDest, MintCredential(tenantId, clientId, clientSecret));

    public static (bool SchemaEnabled, Guid WorkspaceId, Guid LakehouseId, List<(string Schema, string Table)> Tables)
        ResolveOneLakeTables(string root, string? tenantId, string? clientId, string? clientSecret)
    {
        TokenCredential cred = MintCredential(tenantId, clientId, clientSecret);
        var (workspaceSeg, lakehouseSeg) = ParseOneLake(root);
        Guid workspaceId = ResolveWorkspaceId(workspaceSeg, cred);
        Guid lakehouseId = ResolveLakehouseId(workspaceId, lakehouseSeg, cred);
        var lakehouse = new Microsoft.Fabric.Api.Lakehouse.ItemsClient(cred)
                            .GetLakehouse(workspaceId, lakehouseId).Value;
        bool schemaEnabled = !string.IsNullOrEmpty(lakehouse.Properties?.DefaultSchema);
        string catalogName = (lakehouse.DisplayName ?? lakehouseSeg) + ".Lakehouse";
        var uc = ListTablesViaUnityCatalog(workspaceId, lakehouseId, catalogName, cred);
        var tables = uc.Select(t => (t.Schema, t.Table)).ToList();
        return (schemaEnabled, workspaceId, lakehouseId, tables);
    }

    /// <summary>Lists a OneLake lakehouse's tables via the OneLake <b>Unity Catalog REST API</b>
    /// (<c>onelake.table.fabric.microsoft.com/delta/&lt;ws&gt;/&lt;lh&gt;/api/2.1/unity-catalog</c>) as
    /// (schema, table, storage_location). Enumerates schemas then tables per schema, <b>following
    /// <c>next_page_token</c></b> on both (Fabric pages internally — a lakehouse with more than a page of
    /// tables would otherwise be silently truncated). Authenticated with the storage-scope token; async HTTP
    /// (sync <c>HttpClient</c> hangs under the hostfxr CLR, like the DFS path). <paramref name="catalogName"/>
    /// = <c>&lt;lakehouse-display-name&gt;.Lakehouse</c>.</summary>
    public static List<(string Schema, string Table, string Location)> ListTablesViaUnityCatalog(
        Guid workspaceId, Guid lakehouseId, string catalogName, TokenCredential credential) =>
        ListTablesViaUnityCatalogAsync(workspaceId, lakehouseId, catalogName, credential).GetAwaiter().GetResult();

    private const string UnityCatalogHost = "onelake.table.fabric.microsoft.com";

    private static async Task<List<(string Schema, string Table, string Location)>> ListTablesViaUnityCatalogAsync(
        Guid workspaceId, Guid lakehouseId, string catalogName, TokenCredential credential)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { FabricCredentialResolver.StorageScope }), default).ConfigureAwait(false);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        string baseUrl = $"https://{UnityCatalogHost}/delta/{workspaceId}/{lakehouseId}/api/2.1/unity-catalog";
        string catalogQuery = "catalog_name=" + Uri.EscapeDataString(catalogName);

        var result = new List<(string, string, string)>();
        var schemas = await UcPagedAsync(http, $"{baseUrl}/schemas?{catalogQuery}", "schemas",
            el => el.GetProperty("name").GetString() ?? string.Empty).ConfigureAwait(false);
        foreach (var schema in schemas)
        {
            string tablesUrl = $"{baseUrl}/tables?{catalogQuery}&schema_name={Uri.EscapeDataString(schema)}";
            var tables = await UcPagedAsync(http, tablesUrl, "tables", el =>
            {
                string name = el.GetProperty("name").GetString() ?? string.Empty;
                string loc = el.TryGetProperty("storage_location", out var l) ? l.GetString() ?? string.Empty : string.Empty;
                return (Name: name, Loc: loc);
            }).ConfigureAwait(false);
            foreach (var (name, loc) in tables)
            {
                result.Add((schema, name, loc));
            }
        }
        return result;
    }

    /// <summary>Reads a Unity Catalog list endpoint fully, following <c>next_page_token</c> (appended as
    /// <c>&amp;page_token=</c>; the base URL already carries the <c>?catalog_name=</c> query).</summary>
    private static async Task<List<T>> UcPagedAsync<T>(HttpClient http, string url, string arrayProperty,
                                                       Func<JsonElement, T> map)
    {
        var items = new List<T>();
        string? pageToken = null;
        do
        {
            string pageUrl = pageToken is null ? url : url + "&page_token=" + Uri.EscapeDataString(pageToken);
            string json = await http.GetStringAsync(pageUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty(arrayProperty, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    items.Add(map(el));
                }
            }
            pageToken = root.TryGetProperty("next_page_token", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(pageToken));
        return items;
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
        var cred = credential ?? FabricCredentialResolver.AmbientChain();
        var (host, fileSystem, pathUnderFs) = ParseAbfss(abfssDir.Replace('\\', '/').TrimEnd('/'));
        var dirClient = new DataLakeDirectoryClient(
            new Uri($"https://{host}/{fileSystem}/{pathUnderFs}"), cred);
        dirClient.DeleteIfExistsAsync().GetAwaiter().GetResult(); // directory delete on DFS is recursive
    }

    /// <summary>Renames (moves) the OneLake directory at abfss <paramref name="abfssSrc"/> to
    /// <paramref name="abfssDest"/> via the DFS endpoint's <b>atomic native rename</b>
    /// (<see cref="DataLakeDirectoryClient.RenameAsync(string, string, Azure.Storage.Files.DataLake.Models.DataLakeRequestConditions, Azure.Storage.Files.DataLake.Models.DataLakeRequestConditions, CancellationToken)"/>).
    /// A Delta table's <c>_delta_log</c> uses table-relative paths, so moving the whole folder preserves the
    /// table. Src and dest are in the same filesystem (workspace). Async API (sync hangs under the CLR — see
    /// <see cref="ListChildDirectories"/>).</summary>
    public static void RenameDirectory(string abfssSrc, string abfssDest, TokenCredential? credential)
    {
        var cred = credential ?? FabricCredentialResolver.AmbientChain();
        var (host, fileSystem, srcPath) = ParseAbfss(abfssSrc.Replace('\\', '/').TrimEnd('/'));
        var (_, _, destPath) = ParseAbfss(abfssDest.Replace('\\', '/').TrimEnd('/'));
        var dirClient = new DataLakeDirectoryClient(
            new Uri($"https://{host}/{fileSystem}/{srcPath}"), cred);
        // destinationPath = the new path WITHIN the same filesystem, WITHOUT the filesystem prefix. OneLake
        // validates that the leading segment is the item ("<name>.Lakehouse"); prefixing the workspace
        // (filesystem) makes it the leading segment and OneLake rejects it ("item type extension is missing").
        dirClient.RenameAsync(destPath).GetAwaiter().GetResult();
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
