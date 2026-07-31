using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Read-only Fabric introspection: workspaces, items, lakehouses, warehouses, connections, and notebook
/// inspection. All pure reads; optional arguments are DuckDB NAMED parameters.
/// </summary>
/// <remarks>
/// <para>These exist because the WRITE functions need identifiers a user otherwise has to hunt for in the
/// portal: an external shortcut target requires a pre-provisioned cloud connection's GUID
/// (<c>fabric_connections</c>), a T-SQL ATTACH needs the endpoint connection string
/// (<c>fabric_lakehouses</c>/<c>fabric_warehouses</c>), and a cross-workspace shortcut needs the target's name
/// or id (<c>fabric_workspaces</c>/<c>fabric_items</c>).</para>
/// <para>Item CRUD, definition WRITES, git and the tenant-admin surface are deliberately absent — see
/// docs/fabric-api-functions.md §10 for the verdict on every area of the API.</para>
/// </remarks>
internal static class FabricInspectFunctions
{
    internal static void Register(List<ICatalogTableFunction> tables, FabricApiClient api)
    {
        tables.Add(new FabricWorkspacesFunction(api));
        tables.Add(new FabricItemsFunction(api));
        tables.Add(new FabricLakehousesFunction(api));
        tables.Add(new FabricWarehousesFunction(api));
        tables.Add(new FabricConnectionsFunction(api));
        tables.Add(new FabricNotebookParametersFunction(api));
    }
}

/// <summary>
/// Base for a zero-or-one-argument read: subclasses declare columns and fill builders row by row. Keeps each
/// function to its projection, since the Arrow plumbing is identical for all of them.
/// </summary>
internal abstract class FabricRowsFunction : ICatalogTableFunction
{
    protected FabricRowsFunction(FabricApiClient api) => Api = api;

    protected FabricApiClient Api { get; }

    public string SchemaName => CatalogFunctionSet.AllSchemas;

    public abstract string Name { get; }

    public virtual Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    public virtual Schema NamedParameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    protected abstract Schema Columns { get; }

    /// <summary>
    /// Appends rows to <paramref name="row"/> (write each column by index, then <c>EndRow()</c>).
    /// <paramref name="args"/> holds every declared argument as a string, in declared order
    /// (<c>Parameters</c> ++ <c>NamedParameters</c>); an omitted named argument is null.
    /// </summary>
    protected abstract void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct);

    /// <summary>
    /// Extracts the arguments HERE, while the batch is valid, rather than holding it.
    /// </summary>
    /// <remarks>
    /// The args batch is imported from a stream the ABI handler disposes when <c>table_bind</c> returns, so a
    /// binding that kept a reference would be reading freed Arrow buffers at execution time.
    /// </remarks>
    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        int n = Parameters.FieldsList.Count + NamedParameters.FieldsList.Count;
        var values = new string?[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = FabricArgs.Str(args, i, 0);
        }
        return new Binding(this, values);
    }

    private sealed class Binding : FabricTableBinding
    {
        private readonly FabricRowsFunction _fn;
        private readonly string?[] _args;

        internal Binding(FabricRowsFunction fn, string?[] args)
        {
            _fn = fn;
            _args = args;
        }

        public override Schema OutputSchema => _fn.Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            var row = new FabricRowBuilder(_fn.Columns);
            _fn.Fill(row, _args, ct);
            return One(row.Build());
        }
    }
}

/// <summary><c>fabric_workspaces()</c> — every workspace this identity can see.</summary>
internal sealed class FabricWorkspacesFunction : FabricRowsFunction
{
    internal FabricWorkspacesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_workspaces";

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("type"),
        FabricApiFunctions.Str("capacity_id"),
        FabricApiFunctions.Str("description"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        foreach (var w in FabricApiClient.WrapList("workspaces", () => Api.Client.Core.Workspaces.ListWorkspaces(cancellationToken: ct)))
        {
            row.Str(0, w.Id.ToString());
            row.Str(1, w.DisplayName);
            row.Str(2, w.Type.ToString());
            row.Str(3, w.CapacityId?.ToString());
            row.Str(4, w.Description);
            row.EndRow();
        }
    }
}

/// <summary><c>fabric_items([item_type := …])</c> — items in this catalog's workspace.</summary>
internal sealed class FabricItemsFunction : FabricRowsFunction
{
    internal FabricItemsFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_items";

    /// <summary>
    /// <c>fabric_items(item_type := 'Notebook', workspace := 'OtherWS')</c> — both optional; unset lists every
    /// type in the ATTACH's own workspace.
    /// </summary>
    public override Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("item_type"),
        FabricApiFunctions.Str("workspace"),
    }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("type"),
        FabricApiFunctions.Str("description"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[1]);
        foreach (var i in FabricApiClient.WrapList("items",
                     () => Api.Client.Core.Items.ListItems(ws, type: FabricShortcutPath.NullIfBlank(args[0]), cancellationToken: ct)))
        {
            row.Str(0, i.Id?.ToString());
            row.Str(1, i.DisplayName);
            row.Str(2, i.Type.ToString());
            row.Str(3, i.Description);
            row.EndRow();
        }
    }
}

/// <summary>
/// <c>fabric_lakehouses()</c> — the workspace's lakehouses WITH their SQL analytics endpoint details.
/// </summary>
/// <remarks>
/// <c>sql_endpoint_connection_string</c> is the value a T-SQL <c>ATTACH</c> needs, so this is the bridge
/// between a Delta attach and a SQL-endpoint attach in the same script.
/// </remarks>
internal sealed class FabricLakehousesFunction : FabricRowsFunction
{
    internal FabricLakehousesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_lakehouses";

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("default_schema"),
        FabricApiFunctions.Str("onelake_tables_path"),
        FabricApiFunctions.Str("onelake_files_path"),
        FabricApiFunctions.Str("sql_endpoint_id"),
        FabricApiFunctions.Str("sql_endpoint_status"),
        FabricApiFunctions.Str("sql_endpoint_connection_string"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[0]);
        foreach (var lh in FabricApiClient.WrapList("lakehouses",
                     () => Api.Client.Lakehouse.Items.ListLakehouses(ws, cancellationToken: ct)))
        {
            var p = lh.Properties;
            var ep = p?.SqlEndpointProperties;
            row.Str(0, lh.Id?.ToString());
            row.Str(1, lh.DisplayName);
            // Present only on a schema-enabled lakehouse; NULL is the meaningful answer for a flat one.
            row.Str(2, p?.DefaultSchema);
            row.Str(3, p?.OneLakeTablesPath);
            row.Str(4, p?.OneLakeFilesPath);
            row.Str(5, ep?.Id);
            row.Str(6, ep?.ProvisioningStatus.ToString());
            row.Str(7, ep?.ConnectionString);
            row.EndRow();
        }
    }
}

/// <summary><c>fabric_warehouses()</c> — the workspace's warehouses + their T-SQL connection strings.</summary>
internal sealed class FabricWarehousesFunction : FabricRowsFunction
{
    internal FabricWarehousesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_warehouses";

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("connection_string"),
        FabricApiFunctions.Str("collation_type"),
        FabricApiFunctions.Str("description"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[0]);
        foreach (var wh in FabricApiClient.WrapList("warehouses",
                     () => Api.Client.Warehouse.Items.ListWarehouses(ws, cancellationToken: ct)))
        {
            row.Str(0, wh.Id?.ToString());
            row.Str(1, wh.DisplayName);
            row.Str(2, wh.Properties?.ConnectionString);
            row.Str(3, wh.Properties?.CollationType?.ToString());
            row.Str(4, wh.Description);
            row.EndRow();
        }
    }
}

/// <summary>
/// <c>fabric_connections()</c> — the cloud connections this identity can see, with the GUID an external
/// shortcut target needs.
/// </summary>
/// <remarks>
/// LIST only, deliberately. Connection CRUD carries credentials, and a SQL function that stores secrets puts
/// them in query text and logs (docs §10, exclusion rule 1).
/// </remarks>
internal sealed class FabricConnectionsFunction : FabricRowsFunction
{
    internal FabricConnectionsFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_connections";

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("connection_type"),
        FabricApiFunctions.Str("path"),
        FabricApiFunctions.Str("privacy_level"),
        FabricApiFunctions.Str("credential_type"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        foreach (var conn in FabricApiClient.WrapList("connections",
                     () => Api.Client.Core.Connections.ListConnections(cancellationToken: ct)))
        {
            var d = conn.ConnectionDetails;
            row.Str(0, conn.Id.ToString());
            row.Str(1, conn.DisplayName);
            row.Str(2, d?.Type);
            row.Str(3, d?.Path);
            row.Str(4, conn.PrivacyLevel?.ToString());
            row.Str(5, conn.CredentialDetails?.CredentialType?.ToString());
            row.EndRow();
        }
    }
}

/// <summary>
/// <c>fabric_notebook_parameters(notebook)</c> — the (name, default) pairs declared in a notebook's
/// <c>parameters</c>-tagged cell, so a parameterized run can be written against what the notebook accepts.
/// </summary>
/// <remarks>
/// <para><b>Heuristic by nature, and it says so in its output.</b> Fabric follows the papermill convention: the
/// override cell is injected AFTER the cell tagged <c>parameters</c>, and that cell is ordinary Python — so
/// there is no declaration to read, only assignments to parse. This reads simple top-level
/// <c>name = literal</c> lines and reports anything else as a row with a NULL default rather than guessing.
/// Zero rows means the notebook has no tagged cell (which is legal, and means it takes no parameters).</para>
/// <para><b>Not cheap</b>: <c>GetNotebookDefinition</c> is a long-running operation — ~20 s measured. Never
/// call it per row.</para>
/// </remarks>
internal sealed class FabricNotebookParametersFunction : FabricRowsFunction
{
    internal FabricNotebookParametersFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_notebook_parameters";

    public override Schema Parameters { get; } = new Schema(new[] { FabricApiFunctions.Str("notebook") }, null);

    /// <summary><c>workspace := 'OtherWS'</c> — read a notebook in another workspace.</summary>
    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("default_value"),
        FabricApiFunctions.Str("inferred_type"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var arg = args[0];
        if (string.IsNullOrWhiteSpace(arg))
        {
            throw new NotSupportedException(
                "fabric_notebook_parameters: pass the notebook name or id (list them with fabric_items(item_type := 'Notebook')).");
        }
        var ws = Api.ResolveWorkspace(args[1]);
        var nb = Api.ResolveItem(arg, "Notebook", ws);
        var response = FabricApiClient.Wrap("notebook_definition",
            () => Api.Client.Notebook.Items.GetNotebookDefinition(ws, nb, format: "ipynb", cancellationToken: ct).Value);
        // Note the type: the Notebook service has its OWN definition-part model, not Core's ItemDefinitionPart.
        var parts = response.Definition?.Parts
                    ?? (IList<Microsoft.Fabric.Api.Notebook.Models.NotebookDefinitionPart>)
                        System.Array.Empty<Microsoft.Fabric.Api.Notebook.Models.NotebookDefinitionPart>();
        foreach (var part in parts)
        {
            if (part.Payload is null || !(part.Path ?? string.Empty).EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(part.Payload));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var cell in cells.EnumerateArray())
            {
                if (!IsParametersCell(cell))
                {
                    continue;
                }
                foreach (var (name, value, type) in ParseAssignments(SourceOf(cell)))
                {
                    row.Str(0, name);
                    row.Str(1, value);
                    row.Str(2, type);
                    row.EndRow();
                }
            }
        }
    }

    private static bool IsParametersCell(JsonElement cell) =>
        cell.TryGetProperty("metadata", out var md)
        && md.TryGetProperty("tags", out var tags)
        && tags.ValueKind == JsonValueKind.Array
        && ContainsParameters(tags);

    private static bool ContainsParameters(JsonElement tags)
    {
        foreach (var t in tags.EnumerateArray())
        {
            if (string.Equals(t.GetString(), "parameters", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // A notebook cell's `source` is either a string or an array of lines (both legal in nbformat).
    private static string SourceOf(JsonElement cell)
    {
        if (!cell.TryGetProperty("source", out var src))
        {
            return string.Empty;
        }
        if (src.ValueKind == JsonValueKind.String)
        {
            return src.GetString() ?? string.Empty;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var line in src.EnumerateArray())
        {
            sb.Append(line.GetString());
        }
        return sb.ToString();
    }

    private static IEnumerable<(string Name, string? Value, string Type)> ParseAssignments(string source)
    {
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            // Only TOP-LEVEL simple assignments: an indented line is inside a block, and a line with no '='
            // (or a comparison/augmented form) is not a parameter declaration.
            if (line.Length == 0 || raw.StartsWith(" ") || raw.StartsWith("\t") || line.StartsWith("#"))
            {
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
            {
                continue;
            }
            if (line[eq - 1] is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' || line[eq + 1] == '=')
            {
                continue;
            }
            var name = line[..eq].Trim();
            // A type annotation (`n: int = 5`) is fine — take the identifier before the colon.
            int colon = name.IndexOf(':');
            if (colon > 0)
            {
                name = name[..colon].Trim();
            }
            if (name.Length == 0 || !IsIdentifier(name))
            {
                continue;
            }
            var value = line[(eq + 1)..].Trim();
            int comment = value.IndexOf('#');
            if (comment >= 0)
            {
                value = value[..comment].Trim();
            }
            yield return (name, value.Length == 0 ? null : value, InferType(value));
        }
    }

    private static bool IsIdentifier(string s)
    {
        if (!char.IsLetter(s[0]) && s[0] != '_')
        {
            return false;
        }
        foreach (var ch in s)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The Fabric parameter type a literal would map to. Reported rather than assumed — a caller passing
    /// <c>fabric_run_notebook</c> a JSON object still decides the type itself.
    /// </summary>
    private static string InferType(string value)
    {
        if (value.Length == 0) { return "unknown"; }
        if (value is "True" or "False") { return "bool"; }
        if (value.StartsWith('\'') || value.StartsWith('"')) { return "string"; }
        if (long.TryParse(value, out _)) { return "int"; }
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _)) { return "float"; }
        return "unknown";
    }
}
