using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Fabricator.Bridge;
using Microsoft.Extensions.Logging;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// <see cref="IBackend"/> backed by Microsoft.Data.SqlClient. Discovered and
/// instantiated reflectively by <see cref="BackendRegistry"/>.
/// </summary>
public sealed class SqlServerBackend : IBackend
{
    public const string ProviderName = "sqlserver";
    public string Name => ProviderName;
    public IEnumerable<string> Aliases => new[] { "mssql" };

    // Provider-declared settings (registered by the host as DuckDB extension options at load; values pushed
    // back into ProviderSettingsStore). Parity with the former C++ RegisterCompatSettings: most are accepted
    // no-ops for native-extension SET compatibility; mssql_ctas_text_type / mssql_isolation_level /
    // mssql_exec_invalidate_cache are honored. See docs/settings-architecture.md.
    public IEnumerable<ProviderSetting> Settings
    {
        get
        {
            const string compat = "fabricator compatibility setting";
            ProviderSetting Bool(string n, string d = compat, object? def = null) => new(n, ProviderSettingType.Bool, def, d);
            ProviderSetting Long(string n, string d = compat, object? def = null, long? min = null) =>
                new(n, ProviderSettingType.Long, def, d, min);
            ProviderSetting Str(string n, string d = compat) => new(n, ProviderSettingType.Varchar, null, d);
            return new[]
            {
                Bool("mssql_connection_cache"), Bool("mssql_order_pushdown"), Bool("mssql_copy_tablock"),
                Bool("mssql_ctas_use_bcp"), Bool("mssql_convert_varchar_max"),
                Long("mssql_connection_limit"), Long("mssql_connection_timeout"), Long("mssql_acquire_timeout"),
                Long("mssql_attach_validation_timeout"), Long("mssql_catalog_cache_ttl"), Long("mssql_copy_flush_rows"),
                Long("mssql_idle_timeout"), Long("mssql_min_connections"),
                Long("mssql_command_timeout",
                     "fabricator: SqlCommand.CommandTimeout in seconds (0 = infinite, default) applied to scans / " +
                     "DML / bulk — a hung SQL round-trip aborts (per-round-trip, so a long-but-progressing scan is " +
                     "fine); overrides the per-catalog command_timeout ATTACH option", 0L, 0),
                Str("mssql_ctas_text_type"),
                Str("mssql_isolation_level", "fabricator: SQL transaction isolation level for table-in-out sessions"),
                // OPT-IN, and it must stay that way: it holds a SQL Server connection AND an open transaction
                // for the whole DuckDB transaction's life (pool pressure under dbt --threads N, and under
                // SNAPSHOT a tempdb version store that grows for that duration). Never infer it.
                Str("mssql_read_isolation",
                    "fabricator: OPT-IN, unset by default. SQL isolation level for READS inside a DuckDB " +
                    "transaction: routes ordinary scans onto that transaction's pinned connection, opened at " +
                    "this level, so successive statements share ONE view. Use 'snapshot' — it is the only " +
                    "level that delivers it ('repeatable read' still permits the phantoms a count(*) sees, " +
                    "and 'serializable' works by blocking writers); on box it needs ALLOW_SNAPSHOT_ISOLATION. " +
                    "Unset = reads take a pooled connection and share no view. Overrides the per-catalog " +
                    "read_isolation ATTACH option"),
                // OPT-IN, and the location IS the switch — there is no defensible default for "where may
                // this extension write temporary files". Never inferred from the engine: COPY INTO carries
                // seconds of fixed cost, so it is the wrong choice for a small INSERT, and the row count that
                // would decide is not known until the stream has already been consumed.
                Str("mssql_copy_into_staging",
                    "fabricator: OPT-IN, unset by default. A storage location this extension may write " +
                    "temporary parquet to (abfss://<fs>@<host>/<path> or the equivalent https://<host>/<fs>/" +
                    "<path>). Set it on a Fabric Warehouse / Synapse attach to load INSERT/CTAS/COPY data " +
                    "with a staged COPY INTO — DuckDB writes the parquet in parallel and the warehouse " +
                    "ingests the folder in one statement — instead of streaming rows over TDS with " +
                    "SqlBulkCopy. Unset = SqlBulkCopy. Ignored on engines with no COPY INTO. Overrides the " +
                    "per-catalog copy_into_staging ATTACH option"),
                Str("mssql_mars", "fabricator: MARS mode — auto (default, per engine) | true | false"),
                // BOOLEAN, not the auto|true|false tri-state mssql_mars uses, and deliberately: there is no
                // per-engine variation to express here. The default is always "materialise the scans the
                // planner marked", so an `auto` state would be identical to `true` and the tri-state would
                // carry a distinction that does not exist.
                Bool("mssql_materialize",
                     "fabricator: buffer a scan that reads the SAME catalog a statement writes to, before the " +
                     "write starts (default true). Required on MARS engines — without it INSERT INTO t SELECT " +
                     "... FROM t fails at scale with 595; it is also what gives read-your-writes on Fabric. " +
                     "Set false to keep streaming instead: needs ALLOW_SNAPSHOT_ISOLATION on the database, and " +
                     "the scan then reads a committed snapshot (no read-your-writes). Overrides the per-catalog " +
                     "materialize ATTACH option"),
                Str("mssql_default_table_type",
                    "fabricator: created-table storage — '' (rowstore, default) | 'clustered columnstore' " +
                    "(CCI, box/Azure SQL; Fabric/Synapse tables are columnstore already so it is a no-op there)"),
                Str("mssql_cluster_by",
                    "fabricator: comma-separated columns for a Fabric Warehouse / Synapse WITH (CLUSTER BY (cols)) " +
                    "layout on created tables (fallback for a native SORTED BY clause; no-op on box SQL Server)"),
                Bool("mssql_add_identity",
                    "fabricator: auto-add a BIGINT IDENTITY surrogate key (<table>_id) to created tables; overrides " +
                    "the per-catalog add_identity ATTACH option (SET false to skip for fact tables)"),
                Long("mssql_insert_batch_size", "fabricator: max rows per INSERT statement", 2000L, 1),
                Long("mssql_insert_max_rows_per_statement", "fabricator: hard cap on rows per statement", 2000L, 1),
                Long("mssql_insert_max_sql_bytes", "fabricator: max SQL statement size in bytes", 8388608L, 1),
                Long("mssql_default_varchar_length",
                     "fabricator: default VARCHAR/NVARCHAR length for created text columns (unset => MAX)", null, 1),
                Bool("mssql_insert_use_returning_output", "fabricator: use OUTPUT INSERTED for RETURNING", true),
                Bool("mssql_exec_invalidate_cache",
                     "fabricator: invalidate the catalog cache after DDL run via fabricator_exec()", false),
            };
        }
    }

    // The DuckDB secret type + its CREATE SECRET fields, declared here so the host registers them generically
    // (the C++ core names no field). Names mirror the C++ mssql secret for cross-compat. password / access_token
    // are redacted. port is INTEGER, use_encrypt / catalog are BOOLEAN, the rest VARCHAR. Connection-string
    // assembly + validation live in BuildConnectionString. See docs/provider-extensibility.md §2.
    public string SecretType => "mssql";

    public IEnumerable<SecretField> SecretFields => new[]
    {
        new SecretField("host"),
        new SecretField("port", SecretFieldType.Integer),
        new SecretField("database"),
        new SecretField("user"),
        new SecretField("password", Redact: true),
        new SecretField("use_encrypt", SecretFieldType.Boolean),
        new SecretField("authentication"),
        new SecretField("access_token", Redact: true),
        new SecretField("azure_tenant_id"),
        new SecretField("catalog", SecretFieldType.Boolean),
        new SecretField("azure_secret"),
        new SecretField("schema_filter"),
        new SecretField("table_filter"),
        new SecretField("authenticator"),
        new SecretField("application_name"),
    };

    // Connection-free GLOBAL functions registered at extension load (no ATTACH). Provider-agnostic utilities
    // (e.g. the fabricator_render template engine) ride along on the always-present default provider.
    public IEnumerable<IScalarFunction> GlobalScalarFunctions => CustomFunctions.GlobalScalar;
    public IEnumerable<IInOutFunction> GlobalInOutFunctions => CustomFunctions.GlobalInOut;
    public IEnumerable<ICollectorTableFunction> GlobalCollectorFunctions => CustomFunctions.GlobalCollector;
    public IEnumerable<ITableFunction> GlobalTableFunctions => CustomFunctions.GlobalTable;
    public IEnumerable<IAggregateFunction> GlobalAggregateFunctions => CustomFunctions.GlobalAggregate;
    public IEnumerable<ISqlTableFunction> GlobalSqlTableFunctions => CustomFunctions.GlobalSqlTable;
    public IEnumerable<MacroDefinition> GlobalMacros => CustomFunctions.GlobalMacros;

    public IEnumerable<CatalogMacroDefinition> CatalogMacros => CustomFunctions.CatalogMacros;

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        new SqlServerCatalog(connectionString, optionsJson);

    /// <summary>
    /// Assembles a Microsoft.Data.SqlClient connection string from a secret's fields. All SqlClient
    /// connstr / Azure-auth formatting lives here (the C++ host has none). For token auth the token rides
    /// a trailing marker that <see cref="SqlServerCatalog"/> strips and applies via
    /// <c>SqlConnection.AccessToken</c>.
    /// </summary>
    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString)
    {
        // The Fabric REST credential rides a trailing marker, appended LAST because the access-token marker is
        // defined as "everything after it is the token" — so this one is stripped FIRST in the catalog ctor.
        // See SqlServerFabricCredential for why only a renewable principal is carried.
        return SqlServerFabricCredential.Append(
            BuildProviderConnectionString(secretType, fields, baseConnString), secretType, fields);
    }

    private string BuildProviderConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                                 string baseConnString)
    {
        // Dispatch by the DuckDB secret type the fields came from. Our own secret is a full connstr; a foreign
        // azure secret supplies only Entra auth, merged onto the ATTACH target. See docs/provider-extensibility.md §2.
        if (string.IsNullOrEmpty(secretType) || secretType.Equals("mssql", StringComparison.OrdinalIgnoreCase))
        {
            return BuildMssqlConnectionString(fields);
        }
        if (secretType.Equals("azure", StringComparison.OrdinalIgnoreCase))
        {
            return BuildAzureEntraConnectionString(fields, baseConnString);
        }
        throw new ArgumentException(
            $"fabricator: a '{secretType}' secret can't be used by the fabricator provider — use a fabricator secret, " +
            "an azure service-principal/managed-identity secret, or authentication='Active Directory Default'");
    }

    // Reuses an azure secret for Entra auth: the secret supplies the credential (service_principal =
    // client_id + client_secret, or managed_identity = client_id/none), the ATTACH target supplies
    // Server/Database. credential_chain has no token usable for SQL (storage-scoped + fetched lazily) — a
    // clear error points to authentication='Active Directory Default', which makes SqlClient run the same
    // chain scoped for SQL. See docs/provider-extensibility.md §2.
    private string BuildAzureEntraConnectionString(IReadOnlyDictionary<string, string> fields, string baseConnString)
    {
        string F(string key) => fields.TryGetValue(key, out var v) ? v ?? "" : "";
        if (string.IsNullOrWhiteSpace(baseConnString))
        {
            throw new ArgumentException(
                "fabricator: an azure secret supplies only auth — give the server/database in the ATTACH target, " +
                "e.g. ATTACH 'Server=...;Database=...' AS d (TYPE fabricator, SECRET <azure_secret>)");
        }
        // Normalize a mssql:// URI base to a SqlClient connstr so the auth can be attached onto it.
        var baseCs = baseConnString.StartsWith("mssql://", StringComparison.OrdinalIgnoreCase)
            ? SqlServerCatalog.ParseMssqlUri(baseConnString)
            : baseConnString;
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(baseCs);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"fabricator: invalid ATTACH target for an azure secret: {ex.Message}");
        }
        if (string.IsNullOrEmpty(builder.DataSource))
        {
            throw new ArgumentException("fabricator: the ATTACH target for an azure secret must include a Server");
        }

        // An azure access_token secret (PROVIDER access_token — the common Fabric-notebook pattern) carries a
        // ready-minted token: applied via SqlConnection.AccessToken like the native access_token field. The
        // token must be SQL-audience (https://database.windows.net/) — a storage-scoped token from the same
        // pattern is rejected by the server (18456). NOT auto-refreshed (see the fabricator access_token note).
        var azAccessToken = F("access_token");
        if (azAccessToken.Length > 0)
        {
            if (!builder.ContainsKey("Encrypt"))
            {
                builder["Encrypt"] = true;
            }
            builder["TrustServerCertificate"] = true;
            return builder.ConnectionString + SqlServerCatalog.AccessTokenKeyword + azAccessToken;
        }

        var azProvider = F("provider").ToLowerInvariant();
        var clientId = F("client_id");
        var clientSecret = F("client_secret");
        if (azProvider == "credential_chain" ||
            (clientId.Length == 0 && clientSecret.Length == 0))
        {
            throw new ArgumentException(
                "fabricator: this azure secret has no reusable SQL credential (credential_chain tokens are " +
                "storage-scoped and fetched lazily). Use authentication='Active Directory Default' on an " +
                "fabricator secret/connstr instead — SqlClient runs the same credential chain, scoped for SQL.");
        }
        if (clientSecret.Length > 0)
        {
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;
            builder.UserID = clientId; // the SP application (client) id
            builder.Password = clientSecret;
        }
        else
        {
            // managed identity: user-assigned when a client id is present, else system-assigned.
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity;
            if (clientId.Length > 0)
            {
                builder.UserID = clientId;
            }
        }
        if (!builder.ContainsKey("Encrypt"))
        {
            builder["Encrypt"] = true; // TLS on by default, like the fabricator path
        }
        builder["TrustServerCertificate"] = true;
        return builder.ConnectionString;
    }

    private string BuildMssqlConnectionString(IReadOnlyDictionary<string, string> fields)
    {
        string Field(string key) => fields.TryGetValue(key, out var v) ? v ?? "" : "";

        // Field validation lives here (provider-specific; moved from the former C++ ValidateFields when secret
        // fields became provider-declared — docs/provider-extensibility.md §2). host + database are required;
        // an out-of-range/non-numeric port is rejected. Surfaces at connect/ATTACH time.
        if (string.IsNullOrEmpty(Field("host")))
        {
            throw new ArgumentException("fabricator secret: missing required field 'host'");
        }
        if (string.IsNullOrEmpty(Field("database")))
        {
            throw new ArgumentException("fabricator secret: missing required field 'database'");
        }
        var portStr = Field("port");
        if (!string.IsNullOrEmpty(portStr))
        {
            if (!long.TryParse(portStr, out var p))
            {
                throw new ArgumentException($"fabricator secret: port must be a valid integer. Got: {portStr}");
            }
            if (p < 1 || p > 65535)
            {
                throw new ArgumentException($"fabricator secret: port must be between 1 and 65535. Got: {p}");
            }
        }
        var port = string.IsNullOrEmpty(portStr) ? "1433" : portStr;
        var encryptStr = Field("use_encrypt");
        var encrypt = string.IsNullOrEmpty(encryptStr) || ParseBool(encryptStr); // default true

        var cs = new StringBuilder();
        cs.Append("Server=").Append(Field("host")).Append(',').Append(port)
          .Append(";Database=").Append(QuoteConnValue(Field("database")))
          .Append(";Encrypt=").Append(encrypt ? "True" : "False")
          .Append(";TrustServerCertificate=True");

        // application_name was DECLARED in SecretFields but never emitted here, so a secret carrying it was
        // silently ignored. It is not cosmetic: SqlClient sends it as the session's program name, which Fabric
        // records in queryinsights.exec_requests_history.program_name — making it the cheapest way to attribute
        // every statement of a run (including the SqlBulkCopy load, which no query hint can reach). Without it
        // every session reads "Core Microsoft SqlClient Data Provider" and runs are indistinguishable.
        // See docs/consumption-monitoring.md §2.2.
        var applicationName = Field("application_name");
        if (applicationName.Length > 0)
        {
            cs.Append(";Application Name=").Append(QuoteConnValue(applicationName));
        }

        var accessToken = Field("access_token");
        if (accessToken.Length > 0)
        {
            // Token auth: SqlServerCatalog strips this marker and sets SqlConnection.AccessToken.
            return cs.Append(SqlServerCatalog.AccessTokenKeyword).Append(accessToken).ToString();
        }

        var authKw = MapAuthentication(Field("authentication"));
        var user = Field("user");
        var password = Field("password");
        if (authKw.Length > 0)
        {
            cs.Append(";Authentication=").Append(authKw);
            if (user.Length > 0) cs.Append(";User Id=").Append(QuoteConnValue(user));       // SP: client id; MI: client id
            if (password.Length > 0) cs.Append(";Password=").Append(QuoteConnValue(password)); // SP: client secret
        }
        else
        {
            cs.Append(";User Id=").Append(QuoteConnValue(user)).Append(";Password=").Append(QuoteConnValue(password));
        }
        return cs.ToString();
    }

    private static bool ParseBool(string v)
    {
        var t = v.Trim().ToLowerInvariant();
        return t is "true" or "1" or "yes";
    }

    // Quotes a connection-string value per Microsoft.Data.SqlClient rules (double quotes around
    // ; = ', single quotes when the value itself contains a double quote).
    private static string QuoteConnValue(string v)
    {
        var needs = v.Length == 0 || v.IndexOfAny(new[] { ';', '=', '"', '\'' }) >= 0 || v[0] == ' ' || v[^1] == ' ';
        if (!needs)
        {
            return v;
        }
        return v.Contains('"') ? "'" + v.Replace("'", "''") + "'" : "\"" + v + "\"";
    }

    // Maps a friendly/explicit authentication value to the SqlClient `Authentication` keyword
    // (Azure Entra). Returns "" for plain SQL auth; throws on an unknown value.
    private static string MapAuthentication(string raw)
    {
        var k = NormalizeAuth(raw);
        if (k.Length == 0 || k is "sql" or "sqlpassword")
        {
            return "";
        }
        return k switch
        {
            "serviceprincipal" or "spn" or "activedirectoryserviceprincipal" => "Active Directory Service Principal",
            "password" or "entrapassword" or "activedirectorypassword" => "Active Directory Password",
            "managedidentity" or "msi" or "activedirectorymanagedidentity" => "Active Directory Managed Identity",
            "default" or "activedirectorydefault" => "Active Directory Default",
            "interactive" or "activedirectoryinteractive" => "Active Directory Interactive",
            "devicecode" or "devicecodeflow" or "activedirectorydevicecodeflow" => "Active Directory Device Code Flow",
            "workloadidentity" or "activedirectoryworkloadidentity" => "Active Directory Workload Identity",
            "integrated" or "activedirectoryintegrated" => "Active Directory Integrated",
            _ => throw new ArgumentException($"fabricator secret: unsupported authentication '{raw}'"),
        };
    }

    private static string NormalizeAuth(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw.ToLowerInvariant())
        {
            if (c != ' ' && c != '_' && c != '-')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// A SQL Server connection target. Each query opens a fresh pooled
/// <see cref="SqlConnection"/> (ADO.NET pools by connection string); the
/// connection's lifetime is owned by the returned Arrow stream.
/// </summary>
public sealed partial class SqlServerCatalog : IBackendCatalog
{
    // Traces every T-SQL statement the provider sends to SQL Server (the scan/filter/DML SQL, the connection
    // routing and affected-row counts) — off by default (FABRICATOR_LOG_LEVEL). This is the SQL-provider
    // analog of the Fabricator.Delta trace: what actually reaches the server, so a query/pushdown/DML issue
    // is visible without a profiler. Category "Fabricator.Sql".
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql");

    // Trims a statement to a bounded single line for log output (queries can be large; the WHERE/mode of
    // interest is at the front). Collapses interior whitespace so one statement is one log line.
    private static string Trunc(string sql, int max = 400)
    {
        var s = Regex.Replace(sql, @"\s+", " ").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…(" + s.Length + " chars)";
    }

    // Non-standard trailing segment used by SqlServerBackend.BuildConnectionString to carry an Azure
    // Entra access token (not a valid SqlClient connection-string keyword); it is stripped here and
    // applied via SqlConnection.AccessToken.
    internal const string AccessTokenKeyword = ";FabricatorAccessToken=";

    // Provider-authored custom scalar functions, keyed "schema.name" (case-insensitive). Surfaced into
    // the catalog like discovered functions (see GetMetadata) but dispatched to C# (see ExecuteScalar /
    // GetFunctionParamSchema / GetFunctionReturnSchema) instead of generating SQL.
    //
    // ALL SIX catalog-bound kinds live in ONE CatalogFunctionSet (Fabricator.Bridge), which owns the lookup, the
    // declaration rows and the kind strings. This used to be six hand-maintained dictionaries here plus a
    // parallel copy of the same dispatch in every other provider; the set is the shared half, and what stays
    // below it is only the part that IS SqlServer-specific — falling back to a DISCOVERED routine when a name is
    // not a custom function. Kinds: scalar, table, table_sql, inout, collector, aggregate(_spill).
    // This is the ATTACH-INDEPENDENT half; lookups go through the per-catalog `Functions` property below, which
    // is this set on a plain SQL Server and this set plus the fabric_* functions on a Fabric attach.
    private static readonly CatalogFunctionSet CustomFunctionSet = new(
        CustomFunctions.Scalar, CustomFunctions.Table, CustomFunctions.SqlTable, CustomFunctions.InOut,
        CustomFunctions.Collector, CustomFunctions.Aggregate);

    /// <summary>
    /// This catalog's catalog-bound custom functions: the static <see cref="CustomFunctionSet"/> on a plain
    /// SQL Server, and that set PLUS the Fabric REST <c>fabric_*</c> functions when the attach targets Fabric.
    /// Per catalog (not static) because the Fabric functions capture attach context — the workspace/item
    /// defaults and the REST credential — exactly as they do on a OneLake Delta attach.
    /// </summary>
    /// <remarks>
    /// Lazy, so an attach that never touches a function pays nothing; and the gate below reads only the
    /// connection STRING, so nothing here costs a round trip at ATTACH.
    /// </remarks>
    private CatalogFunctionSet Functions => _functions ??= BuildFunctionSet();
    private CatalogFunctionSet? _functions;

    private CatalogFunctionSet BuildFunctionSet()
    {
        if (!IsFabricEndpoint(_baseConnectionString))
        {
            return CustomFunctionSet;
        }
        var scalars = new List<ICatalogScalarFunction>(CustomFunctions.Scalar);
        var tables = new List<ICatalogTableFunction>(CustomFunctions.Table);
        FabricApiFunctions.Register(
            scalars, tables, ResolveApiWorkspace(), ResolveApiItem(),
            _fabricCredFields is null ? null : FabricCredentialResolver.Resolve(_fabricCredFields));
        return new CatalogFunctionSet(scalars, tables, CustomFunctions.SqlTable, CustomFunctions.InOut,
                                      CustomFunctions.Collector, CustomFunctions.Aggregate);
    }

    /// <summary>
    /// The workspace the <c>fabric.*</c> functions default to: the <c>API_WORKSPACE</c> option if given, else
    /// the workspace id ENCODED IN THE ENDPOINT HOST, else null (the functions then demand
    /// <c>workspace :=</c> per call and say so).
    /// </summary>
    /// <remarks>
    /// <para>The inference is what makes <c>API_WORKSPACE</c> optional. It is a pure string decode — no
    /// connection and no REST call — so it costs nothing at ATTACH, which is the constraint the whole
    /// registration path is built around (see <see cref="IsFabricEndpoint"/>).</para>
    /// <para><b>⚠ It returns null rather than guessing.</b> The host encoding is undocumented, so a future
    /// change must degrade to "tell me the workspace", never to "use the wrong workspace" — a wrong id would
    /// aim REST calls at a different workspace that the identity may well have access to. The enumerate-and-
    /// match fallback (list workspaces, compare each item's endpoint connection string against this host) is
    /// deliberately NOT implemented here: it costs O(workspaces × items) REST calls at ATTACH to replace an
    /// error message with a slow success. It belongs behind an explicit opt-in if anyone wants it.</para>
    /// </remarks>
    private string? ResolveApiWorkspace()
    {
        if (_fabricWorkspace.Length > 0)
        {
            return _fabricWorkspace;
        }
        var host = FabricSqlEndpointHost.ServerFromConnectionString(_baseConnectionString);
        return FabricSqlEndpointHost.WorkspaceIdFromHost(host)?.ToString();
    }

    /// <summary>
    /// The item the <c>fabric.*</c> functions default to: the <c>API_ITEM</c> option if given, else the
    /// connection string's <c>Database</c>.
    /// </summary>
    /// <remarks>
    /// On a Fabric SQL endpoint the database IS the item — a lakehouse or warehouse of that name — so the
    /// default is exact rather than a heuristic. Overriding it is the interesting case and stays supported:
    /// <c>API_ITEM</c> (or a per-call <c>item :=</c>) points the functions at a DIFFERENT item from the one you
    /// query, which is how a project attached to a Warehouse refreshes a Lakehouse's SQL endpoint.
    /// </remarks>
    private string? ResolveApiItem()
        => _fabricItem.Length > 0
               ? _fabricItem
               : FabricSqlEndpointHost.DatabaseFromConnectionString(_baseConnectionString);

    /// <summary>
    /// Whether this connection targets <b>Fabric</b> — the gate for registering the <c>fabric.*</c> functions,
    /// mirroring the Delta catalog's <c>IsOneLake</c> check: off Fabric they have no workspace, no item and no
    /// REST endpoint, so advertising them would put functions in the catalog that can only fail.
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately NOT <c>ServerProfile.IsWarehouse</c>, which is the obvious-looking choice and is wrong:
    /// <c>EngineEdition == 11</c> covers Fabric Warehouse, the Lakehouse SQL endpoint AND <b>Synapse
    /// serverless</b>, so it would advertise Fabric functions on a Synapse attach. The host is what actually
    /// identifies the platform. It also costs no connection — <c>IsWarehouse</c> would force profile detection
    /// at ATTACH, turning function registration into a round trip.
    /// </remarks>
    internal static bool IsFabricEndpoint(string connectionString)
    {
        string host;
        try
        {
            host = new SqlConnectionStringBuilder(connectionString).DataSource;
        }
        catch (Exception)
        {
            return false; // unparseable: SqlClient reports the real error at connect
        }
        // Strip a "tcp:" prefix and any ,port / \instance suffix before matching the domain.
        int comma = host.IndexOfAny(new[] { ',', '\\' });
        if (comma >= 0) host = host.Substring(0, comma);
        host = host.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
        // Covers every Fabric SQL surface: <id>.datawarehouse.fabric.microsoft.com (Warehouse + Lakehouse SQL
        // endpoint) and <id>.database.fabric.microsoft.com (Fabric SQL database). Matched on the domain SUFFIX
        // rather than an exact middle segment so a new surface under the same domain is not silently excluded.
        return host.EndsWith(".fabric.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Expands a <see cref="CatalogFunctionSet.AllSchemas"/> ("__all__") declaration across this catalog's
    /// schemas — the every-schema registration the <c>fabric_*</c> set uses, so <c>w.dbo.fabric_*</c> resolves
    /// the same way it does on a Delta attach.
    /// </summary>
    /// <remarks>
    /// <para>This USED to throw: the provider's own declarations all name a concrete schema, and the functions
    /// stream is built as T-SQL with — at the time — no catalog instance in scope. Hosting the Fabric set here
    /// gave the sentinel a real caller, so the enumeration is now implemented rather than refused.</para>
    /// <para>Costs one extra metadata query, and ONLY when some declaration actually uses the sentinel: the
    /// callback is invoked lazily by <c>Declarations</c> for exactly that reason, so a non-Fabric attach (whose
    /// declarations all name a schema) never runs it. <c>schema_filter</c> is applied, so a function is not
    /// declared in a schema the catalog does not surface.</para>
    /// </remarks>
    private IReadOnlyList<string> ExpandAllSchemas()
    {
        var names = new List<string>();
        foreach (var row in ReadMetadataRows(SchemasSql, 1))
        {
            if (row[0] is { } name && (_schemaFilter is null || _schemaFilter.IsMatch(name)))
            {
                names.Add(name);
            }
        }
        return names;
    }

    private readonly string _baseConnectionString;   // user connstr (no MARS); basis for the finalized string
    private readonly string? _accessToken;
    // Server capability profile, detected lazily on the first connection (see docs/warehouse-support.md).
    // Probed on a NON-MARS connection (Synapse/Fabric reject a MARS connection outright), after which the
    // working connection string re-enables MARS only when the engine supports it.
    private volatile ServerProfile? _profile;
    // BOTH finalized in EnsureProfile — the SERVER's capability is a property of the server and is cached
    // once, but WHICH of these a connection uses is decided per connection, from the opening SESSION's
    // `mssql_mars` (see EffectiveMars). Two strings rather than one rebuilt per open: SqlClient pools BY
    // connection string, so a stable pair gives two pools instead of a new pool per open.
    private string? _connectionStringMars;
    private string? _connectionStringNoMars;
    private readonly object _profileLock = new();

    // Provider-owned ATTACH options (parsed from open_catalog's options_json; docs/provider-extensibility.md §3).
    // schema_filter/table_filter (icase regex, substring match) are applied in GetMetadata so discovery returns
    // only matches; _isolationLevel is this catalog's default SQL isolation for table-in-out sessions (a SET
    // mssql_isolation_level overrides it, resolved in InOutBind).
    private readonly Regex? _schemaFilter;
    private readonly Regex? _tableFilter;
    // function_filter (icase regex on the routine NAME) gates which discovered scalar UDFs / TVFs / procs
    // register — symmetric with table_filter (which is table-only). Applied to the Functions discovery in C#
    // (schema_filter already gates functions by schema on the C++ side).
    private readonly Regex? _functionFilter;
    private readonly string _isolationLevel = "";
    // ATTACH option `read_isolation '<level>'` (unset = OFF = the shipped behaviour). Set, it makes ordinary
    // scans inside a DuckDB transaction run on that transaction's pinned connection, which is then opened at
    // this level — the opt-in for cross-statement read stability. ⚠ DISTINCT from _isolationLevel above, which
    // scopes table-in-out sessions only; reusing that one would have switched this on for anyone who had ever
    // set it, and the cost (a held connection + open transaction for the transaction's life) must be asked for.
    private readonly string _readIsolation = "";
    // ATTACH option `copy_into_staging <location>` (default unset = SqlBulkCopy). The storage location this
    // catalog may stage temporary parquet in, which is also the opt-in for the COPY INTO load path — see
    // ResolveCopyIntoStaging and WarehouseCopyInto. Validated at ATTACH so a typo fails there.
    private readonly string _copyIntoStaging = "";
    // ATTACH option `mars auto|true|false` (unset => auto). Outranked by a SET mssql_mars, like every other
    // behaviour option here. Kept as the raw string so one parser validates both surfaces.
    private readonly string? _mars;

    // ATTACH option `materialize true|false` (default true). See ResolveMaterialize; a SET mssql_materialize
    // overrides it per session, the same precedence as isolation_level and command_timeout.
    private readonly bool? _materialize;
    // ATTACH option `command_timeout <seconds>` (0 = infinite, default): the catalog default SqlCommand.CommandTimeout
    // for scans/DML/bulk. A SET mssql_command_timeout overrides it per session (ResolveCommandTimeout).
    private readonly int _commandTimeout;
    // ATTACH option `add_identity true`: created tables get an auto BIGINT IDENTITY surrogate key (<table>_id)
    // when none is otherwise specified. The mssql_add_identity SET setting overrides this per session (turn OFF
    // for fact tables that don't need a surrogate key). Resolved by ResolveAddIdentity().
    private readonly bool _addIdentityOnCreate;
    // Ambient Fabric-notebook credential (token-service-backed, refreshing) — set in the ctor when the
    // connstr carries no credential AND the process runs on Fabric compute. Applied per connection open via
    // SqlConnection.AccessTokenCallback (so tokens refresh, unlike the static _accessToken).
    private readonly Fabricator.Bridge.FabricNotebookCredential? _fabricAmbientCredential;
    // Fabric REST credential fields carried from the ATTACH secret (null ⇒ the ambient chain). Distinct from
    // _accessToken / _fabricAmbientCredential above, which authenticate SQL: those are SQL-audience and cannot
    // call the Fabric API. See SqlServerFabricCredential.
    private readonly IReadOnlyDictionary<string, string>? _fabricCredFields;
    // ATTACH options `workspace` / `item`: the Fabric workspace and item the catalog-bound fabric_* functions
    // act on by default. A Fabric SQL connection string does not name either (its host is an opaque routing
    // GUID), so unlike a OneLake Delta root there is nothing to derive them from — they are supplied, or the
    // functions' `workspace :=` / `item :=` named parameters become required. See docs/fabric-api-functions.md §9h.
    private readonly string _fabricWorkspace = "";
    private readonly string _fabricItem = "";

    public SqlServerCatalog(string connectionString, string optionsJson)
    {
        // Empty connection string is rejected early with a clear message.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("fabricator: empty connection string", nameof(connectionString));
        }
        // A `mssql://[user[:password]@]host[:port]/database[?encrypt=..&trustservercertificate=..]`
        // URI is translated to a SqlClient connection string (SqlClient can't parse it).
        if (connectionString.StartsWith("mssql://", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = ParseMssqlUri(connectionString);
        }

        // The Fabric REST credential marker is stripped FIRST: the access-token marker below claims everything
        // after itself as the token, so the two only compose in this order (SqlServerFabricCredential).
        (connectionString, _fabricCredFields) = SqlServerFabricCredential.Extract(connectionString);

        int idx = connectionString.IndexOf(AccessTokenKeyword, StringComparison.OrdinalIgnoreCase);
        string connStr;
        if (idx >= 0)
        {
            // Everything after the keyword is the token (the builder appends it last).
            _accessToken = connectionString.Substring(idx + AccessTokenKeyword.Length);
            connStr = connectionString.Substring(0, idx);
        }
        else
        {
            connStr = connectionString;
        }
        // Ambient Fabric-notebook auth: on Fabric compute (token service present) a connstr that carries NO
        // credential — bare, or an explicit Authentication=Active Directory Default (which SqlClient would
        // run through DefaultAzureCredential, PROVEN sourceless on Fabric compute) — switches to a REFRESHING
        // AccessTokenCallback minting SQL-audience tokens from the Fabric token service (the session's
        // executing identity: interactive user / pipeline SP / workspace identity). Strictly gated: never
        // engages off-Fabric or when any credential (token, password, integrated, other AD modes) is given.
        if (_accessToken is null && Fabricator.Bridge.FabricNotebookCredential.IsAvailable)
        {
            try
            {
                var probe = new SqlConnectionStringBuilder(connStr);
                bool noCredential = probe.Password.Length == 0 && probe.UserID.Length == 0 &&
                                    !probe.IntegratedSecurity &&
                                    probe.Authentication is SqlAuthenticationMethod.NotSpecified
                                        or SqlAuthenticationMethod.ActiveDirectoryDefault;
                if (noCredential)
                {
                    probe.Remove("Authentication");
                    connStr = probe.ConnectionString;
                    _fabricAmbientCredential = new Fabricator.Bridge.FabricNotebookCredential();
                }
            }
            catch
            {
                // unparseable connstr — leave it untouched; SqlClient reports the real error at connect
            }
        }
        // Defer the MARS decision to first-connection profile detection: MARS is forced only when the
        // engine supports it (box SQL Server / Azure SQL DB), since Synapse/Fabric reject a MARS
        // connection outright. See EnsureProfile + docs/transactions.md (read-your-writes on the pinned
        // connection requires MARS so a scan reader and DML coexist).
        _baseConnectionString = connStr;

        // Parse the provider-owned ATTACH options (a flat JSON object of strings). Keys are matched
        // case-insensitively; unknown keys are ignored (forward-compat). A bad filter regex fails ATTACH
        // here with a clean message (the former C++ ValidateCatalogFilters).
        if (!string.IsNullOrEmpty(optionsJson))
        {
            using var doc = JsonDocument.Parse(optionsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string val = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "") : "";
                switch (prop.Name.ToLowerInvariant())
                {
                    case "schema_filter": _schemaFilter = CompileFilter("schema_filter", val); break;
                    case "table_filter": _tableFilter = CompileFilter("table_filter", val); break;
                    case "function_filter": _functionFilter = CompileFilter("function_filter", val); break;
                    case "isolation_level": _isolationLevel = val; break;
                    // Validated HERE so a typo fails the ATTACH, not the first SELECT inside a transaction —
                    // by then the statement that fails is not the one that named the option.
                    case "read_isolation":
                        ParseIsolationLevel(val);
                        _readIsolation = val;
                        break;
                    // Validated at ATTACH for the same reason: a mistyped staging location would otherwise
                    // surface at the first large INSERT, as a storage error naming a path the statement did
                    // not mention. Parse throws with the accepted spellings.
                    case "copy_into_staging":
                        OneLakeStagingLocation.Parse(val);
                        _copyIntoStaging = val;
                        break;
                    case "materialize":
                        _materialize = !(string.Equals(val, "false", StringComparison.OrdinalIgnoreCase) || val == "0");
                        break;
                    // ⚠ ADDED WITH CHANGE B, and it RESTORES a capability that change would otherwise have
                    // removed. MARS used to be frozen per catalog at its first connect, so attaching twice
                    // under different `SET mssql_mars` values was the way to get two catalogs on different
                    // modes. Resolving per connection makes MARS a SESSION property, under which those two
                    // attaches are identical — so the per-catalog form has to be expressible directly.
                    // Validated here, at ATTACH, rather than at the first connection that uses it.
                    case "mars":
                        ParseMarsMode(val); // throws on a bad spelling
                        _mars = val;
                        break;
                    case "command_timeout":
                        if (int.TryParse(val, out var ctSecs) && ctSecs >= 0) { _commandTimeout = ctSecs; }
                        break;
                    case "add_identity":
                        _addIdentityOnCreate = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
                        break;
                    // API_WORKSPACE / API_ITEM: the workspace and item the `fabric.*` REST functions act on.
                    // ⚠ NAMED FOR THE API, NOT FOR THE ATTACH, and that distinction is the point. They do NOT
                    // change what you attached — the SQL catalog still comes from the connection string's
                    // Database. Two attaches differing only in API_ITEM expose IDENTICAL tables; the option is
                    // invisible until a fabric.* function runs. The earlier names (`WORKSPACE`/`ITEM`) read as
                    // if they selected the attach target, which is what made them confusing.
                    // Both are OPTIONAL: omitted, they are inferred from the connection string (see
                    // ResolveApiWorkspace / ResolveApiItem). Supplying one is an OVERRIDE, which is a real
                    // feature — one attach can drive a different lakehouse's endpoint.
                    case "api_workspace": _fabricWorkspace = val; break;
                    case "api_item": _fabricItem = val; break;
                    // ⚠ THE OLD NAMES MUST FAIL LOUDLY, NOT BE IGNORED. Unknown ATTACH keys are dropped for
                    // forward-compat (see the comment above), so leaving `workspace`/`item` unhandled would
                    // make an existing script SILENTLY change behaviour: the option is discarded and the
                    // functions fall back to the inferred defaults, which for `item` is the connstr's Database
                    // — a DIFFERENT item, with no error. Since the point of these options is to target another
                    // item, that silent switch would redirect a refresh at the wrong lakehouse. Erroring turns
                    // a wrong-target bug into a one-line edit.
                    case "workspace":
                    case "item":
                        throw new NotSupportedException(
                            $"ATTACH option '{prop.Name}' was renamed to 'api_{prop.Name.ToLowerInvariant()}' "
                            + "— it scopes the fabric.* REST FUNCTIONS, not the attach itself (the catalog still "
                            + "comes from the connection string's Database). Both are now OPTIONAL: the "
                            + "workspace is decoded from the endpoint host and the item defaults to Database, so "
                            + "you may simply drop it unless you are deliberately targeting a different item.");
                }
            }
        }
    }

    // Compiles an ATTACH filter pattern (icase regex, unanchored substring match — parity with the former
    // C++ CatalogFilters std::regex_search). Empty => no filter. A bad pattern => a clean ATTACH error.
    private static Regex? CompileFilter(string key, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"fabricator: invalid {key} regex '{pattern}': {ex.Message}");
        }
    }

    // Translates a mssql://[user[:password]@]host[:port]/database[?params] URI into
    // a Microsoft.Data.SqlClient connection string. Mirrors the C++ mssql extension:
    // user/password before the LAST '@' (passwords may contain unencoded '@'), all
    // components percent-decoded; encrypt/trustservercertificate query params honored
    // (TLS on by default for compatibility).
    internal static string ParseMssqlUri(string uri)
    {
        string rest = uri.Substring("mssql://".Length);
        string query = "";
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest.Substring(q + 1);
            rest = rest.Substring(0, q);
        }

        var builder = new SqlConnectionStringBuilder();
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            string userInfo = rest.Substring(0, at);
            rest = rest.Substring(at + 1);
            int colon = userInfo.IndexOf(':');
            if (colon >= 0)
            {
                builder["User ID"] = Uri.UnescapeDataString(userInfo.Substring(0, colon));
                builder["Password"] = Uri.UnescapeDataString(userInfo.Substring(colon + 1));
            }
            else
            {
                builder["User ID"] = Uri.UnescapeDataString(userInfo);
            }
        }

        int slash = rest.IndexOf('/');
        string hostPort = rest;
        if (slash >= 0)
        {
            hostPort = rest.Substring(0, slash);
            builder["Database"] = Uri.UnescapeDataString(rest.Substring(slash + 1));
        }
        // host:port -> "host,port" (split at the last ':')
        int hostColon = hostPort.LastIndexOf(':');
        builder.DataSource = hostColon >= 0
            ? hostPort.Substring(0, hostColon) + "," + hostPort.Substring(hostColon + 1)
            : hostPort;

        bool encryptSet = false, trustSet = false;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = Uri.UnescapeDataString(part.Substring(0, eq)).ToLowerInvariant();
            string val = Uri.UnescapeDataString(part.Substring(eq + 1));
            switch (key)
            {
                case "encrypt":
                case "ssl":
                case "use_ssl":
                    builder["Encrypt"] = val; encryptSet = true; break;
                case "trustservercertificate":
                    builder["TrustServerCertificate"] = val; trustSet = true; break;
                // schema_filter/table_filter/authenticator/... are accepted but not wired.
            }
        }
        if (!encryptSet)
        {
            builder["Encrypt"] = "True";
        }
        if (!trustSet)
        {
            builder["TrustServerCertificate"] = "True";
        }
        return builder.ConnectionString;
    }

    // The connected engine's capability profile (detected once, on first connection).
    internal ServerProfile Profile
    {
        get { EnsureProfile(); return _profile!; }
    }

    // Detect the server profile on first use and build BOTH working connection strings. Probed on a NON-MARS
    // connection so Synapse/Fabric (which reject a MARS connection) can be classified.
    //
    // One-time per catalog, and that is right for the PROFILE — it describes the server. It is NOT right for
    // the MARS choice, which is why that no longer happens here: `mssql_mars` is session-scoped, so which of
    // the two strings a connection uses is decided per connection, at open time (see OpenConnection).
    private void EnsureProfile()
    {
        if (_profile is not null)
        {
            return;
        }
        lock (_profileLock)
        {
            if (_profile is not null)
            {
                return;
            }
            // Probe MARS-free regardless of what the user put in the connection string: Synapse/Fabric
            // reject a MARS connection outright, so detection must not ride one.
            using var probe = OpenRaw(
                new SqlConnectionStringBuilder(_baseConnectionString) { MultipleActiveResultSets = false }.ConnectionString);
            probe.Open();
            var profile = ServerProfile.Detect(probe);
            // mssql_mars (provider setting) is tri-state: auto (default) => the engine's capability
            // (profile.SupportsMars); true/false force it. Forcing MARS on an engine that rejects it
            // (Fabric/Synapse) is the user's choice — it fails loudly at connect.
            _connectionStringMars =
                new SqlConnectionStringBuilder(_baseConnectionString) { MultipleActiveResultSets = true }.ConnectionString;
            _connectionStringNoMars =
                new SqlConnectionStringBuilder(_baseConnectionString) { MultipleActiveResultSets = false }.ConnectionString;
            _profile = profile; // volatile write last → publishes both strings to fast-path readers
        }
    }

    /// <summary>
    /// The MARS mode a connection opened NOW would get: <c>mssql_mars</c> resolved against the server's
    /// capability, read from the CURRENT session each time.
    /// </summary>
    /// <remarks>
    /// ⚠ NOT CACHED, and that is the whole of change B. It used to be resolved once per catalog in
    /// <see cref="EnsureProfile"/>, which made `SET mssql_mars` after the ATTACH a SILENT no-op — the README
    /// had to tell users to set it first, and a catalog is shared by every DuckDB connection, so even a
    /// correctly-ordered SET applied to everyone. Session-scoped settings (ABI v69) made that the last
    /// setting still baked at first connect; every other one is already read at use time.
    /// </remarks>
    private bool EffectiveMars() => ResolveMarsMode(Profile);

    /// <summary>
    /// The MARS mode that governs the AMBIENT DuckDB transaction: the PINNED connection's own mode when one
    /// exists, else what a fresh connection would get.
    /// </summary>
    /// <remarks>
    /// ⚠ THE DISTINCTION IS LOAD-BEARING, and using <see cref="EffectiveMars"/> at the routing sites instead
    /// would be a correctness bug rather than an imprecision. A pinned connection's MARS mode was fixed when
    /// it was opened; the routing questions ("may this scan reuse the pinned connection?", "can it block on
    /// this transaction's own locks?") are about THAT connection, not about one we might open later. They
    /// differ exactly when a session changes `mssql_mars` mid-transaction — which is meaningless as a
    /// request, but must not be allowed to reroute a scan onto a connection that cannot take it: on a
    /// no-MARS pinned connection that is limitation 1.15's UNBOUNDED HANG, not an error.
    /// </remarks>
    private bool TxnMars()
    {
        long txnId = AmbientTransaction.Current;
        if (txnId != 0 && _txns.TryGetValue(txnId, out var state))
        {
            lock (state)
            {
                if (state.Connection is not null)
                {
                    return state.MarsEnabled;
                }
            }
        }
        return EffectiveMars();
    }

    // mssql_mars: "auto"/empty => the engine default (profile.SupportsMars); "true"/"false" force it.
    // A genuinely unknown value throws. When MARS is off, reads never reuse the pinned write connection
    // (no read-your-writes) — they take a fresh pooled connection (see ExecuteQuery), which is what makes
    // a non-MARS warehouse (Fabric/Synapse) work: an open scan reader and DML can't coexist on one
    // non-MARS connection. See docs/transactions.md + docs/warehouse-support.md.
    private bool ResolveMarsMode(ServerProfile profile)
    {
        // SET wins over the ATTACH option, matching materialize / read_isolation / copy_into_staging.
        var v = ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_mars");
        if (string.IsNullOrWhiteSpace(v))
        {
            v = _mars;
        }
        return ParseMarsMode(v) ?? profile.SupportsMars;
    }

    /// <summary>
    /// Parses an `mssql_mars` / ATTACH `mars` value. Null (and `auto`) mean "the engine's capability";
    /// anything unrecognised THROWS rather than silently meaning auto.
    /// </summary>
    /// <remarks>Shared by the setting and the ATTACH option so the two cannot accept different spellings —
    /// and so the option is validated AT ATTACH, where the mistake is, rather than at the first connection
    /// that happens to use it.</remarks>
    private static bool? ParseMarsMode(string? v) =>
        (v ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "auto" => null,
            "true" or "on" or "1" or "yes" => true,
            "false" or "off" or "0" or "no" => false,
            _ => throw new ArgumentException($"fabricator: invalid mssql_mars '{v}' (expected auto | true | false)"),
        };

    // Builds a connection on a specific connection string, applying an Azure access token when one was
    // supplied via the secret (Entra "bring-your-own-token" auth). Does NOT trigger profile detection
    // (used by the detection probe itself).
    private SqlConnection OpenRaw(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        if (_accessToken is not null)
        {
            connection.AccessToken = _accessToken;
        }
        else if (_fabricAmbientCredential is not null)
        {
            var cred = _fabricAmbientCredential;
            connection.AccessTokenCallback = async (_, ct) =>
            {
                var token = await cred.GetTokenAsync(
                    new Azure.Core.TokenRequestContext(new[] { Fabricator.Bridge.FabricCredentialResolver.SqlScope }),
                    ct).ConfigureAwait(false);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            };
        }
        return connection;
    }

    // Creates a connection on the finalized (profile-aware) connection string, detecting the server
    // profile on first use.
    private SqlConnection OpenConnection() => OpenConnection(out _);

    /// <summary>
    /// Opens a connection on the profile-aware string, reporting which MARS mode it got. The opening SESSION
    /// decides (see <see cref="EffectiveMars"/>); a transaction's PINNED connection records the answer in
    /// <see cref="TxnState.MarsEnabled"/> so the routing keeps asking about the connection in play.
    /// </summary>
    private SqlConnection OpenConnection(out bool marsEnabled)
    {
        EnsureProfile(); // explicit: the strings below are null until it has run
        marsEnabled = EffectiveMars();
        return OpenRaw(marsEnabled ? _connectionStringMars! : _connectionStringNoMars!);
    }

    // ---- Transaction state (per DuckDB transaction) ---------------------------
    // Each concurrent DuckDB transaction (keyed by its global_transaction_id, carried per-thread via
    // AmbientTransaction) gets its OWN pinned provider connection + SqlTransaction, opened lazily on the
    // first write. This is what makes concurrent writes — e.g. dbt --threads N building several models at
    // once, each in its own explicit BEGIN/COMMIT — correct: they no longer collapse onto one shared,
    // non-thread-safe SqlConnection (which produced error 595). Reads in a transaction reuse that
    // transaction's connection (read-your-writes); reads with no active transaction take a fresh pooled
    // connection. Matches the native mssql-extension's per-MSSQLTransaction connection model. See
    // docs/transaction-concurrency.md.
    private sealed class TxnState
    {
        public SqlConnection? Connection;
        public SqlTransaction? Transaction;

        /// <summary>
        /// The MARS mode <see cref="Connection"/> was opened with. Meaningless while Connection is null.
        /// </summary>
        /// <remarks>
        /// ⚠ Recorded rather than re-derived because `mssql_mars` is session-scoped and read per open (change
        /// B): re-resolving it later could answer differently from what this connection actually has, and the
        /// routing decisions it feeds are about THIS connection — a scan sent to a no-MARS pinned connection
        /// on the strength of a MARS-on answer is limitation 1.15's unbounded hang.
        /// </remarks>
        public bool MarsEnabled;

        /// <summary>
        /// Tables this transaction has written, keyed <c>[schema].[table]</c>; the value is true when the
        /// write changed the SCHEMA (DDL). Read by <see cref="EnsureScanCannotSelfBlock"/> — with MARS off a
        /// scan takes a POOLED connection, so reading a table this same transaction has written on its
        /// PINNED connection waits on locks only that transaction can release, and it cannot release them
        /// because it is blocked waiting for the scan. Guarded by the lock on this instance, like the rest.
        /// </summary>
        public readonly Dictionary<string, bool> Touched = new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Serializes execute+drain on <see cref="Connection"/> for the no-MARS <c>mssql_read_isolation</c>
        /// path, where several of a statement's scans share this one connection and it admits a single reader.
        /// ⚠ A SEPARATE object from the instance lock ON PURPOSE: it is held for a whole query, and the
        /// instance lock guards short field reads that must not queue behind one.
        /// </summary>
        public readonly object ExecGate = new();
    }

    /// <summary>Records that the ambient transaction has written <paramref name="tableName"/>, so a later
    /// scan of it can be refused rather than deadlocking (see <see cref="EnsureScanCannotSelfBlock"/>).
    /// No-op outside a transaction that has pinned a connection.</summary>
    /// <remarks>⚠ INCOMPLETE BY CONSTRUCTION, in the SAFE direction: a write issued through raw
    /// <c>fabricator_exec</c> (<see cref="ExecuteNonQuery"/>) names no table we can see, so a scan of a table
    /// written that way is NOT refused and still hangs as before. Every path that knows its table records
    /// here; a missed one costs the old behaviour, never a wrong refusal.</remarks>
    private void RecordTouch(string schemaName, string tableName, bool schemaChanged = false)
    {
        long txnId = AmbientTransaction.Current;
        if (txnId == 0)
        {
            return;
        }
        // ⚠ GetOrAdd, NOT TryGetValue: every caller records BEFORE its BeginWrite(), so on the transaction's
        // FIRST write no state exists yet and a lookup would silently record nothing — the tracking would be
        // dead for exactly the write that creates the hazard. BeginWrite's own GetOrAdd then finds this
        // entry and fills in the connection; a state with a null Connection is already handled everywhere
        // (the routing and the check both test it).
        var state = _txns.GetOrAdd(txnId, _ => new TxnState());
        var key = $"{Quote(schemaName)}.{Quote(tableName)}";
        lock (state)
        {
            state.Touched[key] = state.Touched.TryGetValue(key, out var was) ? (was || schemaChanged) : schemaChanged;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TxnState> _txns = new();

    // Per catalog, because the function pins THIS catalog's transaction connection. Lazy so a catalog that
    // never calls it pays nothing.
    private SqlServerSessionTagFunction SessionTag => _sessionTag ??= new SqlServerSessionTagFunction(this);
    private SqlServerSessionTagFunction? _sessionTag;

    // Explicit user BEGIN..COMMIT transaction ids (v60 is_explicit; the ambient id is set by set_active_txn
    // right before begin_transaction). Consulted by the external-table INSERT routing: a storage-side write
    // commits its own Delta commit / parquet PUT and cannot roll back with the SQL catalog's transaction,
    // so it is rejected inside an explicit transaction (autocommit only).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _explicitTxns = new();

    // The connection + provider transaction are pinned lazily on the first write (BeginWrite), keyed by the
    // ambient transaction id; begin only records explicitness. A read-only transaction never creates state.
    public void BeginTransaction(bool isExplicit)
    {
        if (isExplicit && AmbientTransaction.Current != 0)
        {
            _explicitTxns[AmbientTransaction.Current] = 1;
        }
    }

    public void CommitTransaction() => EndTransaction(AmbientTransaction.Current, commit: true);

    public void RollbackTransaction() => EndTransaction(AmbientTransaction.Current, commit: false);

    private void EndTransaction(long txnId, bool commit)
    {
        _explicitTxns.TryRemove(txnId, out _);
        if (!_txns.TryRemove(txnId, out var state))
        {
            return; // no write happened in this transaction (or already finished)
        }
        try
        {
            if (state.Transaction is not null)
            {
                if (commit)
                {
                    state.Transaction.Commit();
                }
                else
                {
                    state.Transaction.Rollback();
                }
            }
        }
        finally
        {
            state.Transaction?.Dispose();
            state.Connection?.Dispose();
        }
    }

    // Returns a connection for a write. When a DuckDB transaction is active (ambient id != 0) it is that
    // transaction's pinned connection (opened + provider-transaction started on first use), owns=false so
    // the caller must NOT dispose it (COMMIT/ROLLBACK disposes it). With no active transaction (id 0) it is
    // a fresh connection (owns=true). The connection is already open. Internal so the top-level
    // SqlServerProcEach (the proc `_each` exchange binding) can run its per-row EXEC on it.
    internal (SqlConnection connection, SqlTransaction? transaction, bool owns) BeginWrite()
    {
        long txnId = AmbientTransaction.Current;
        if (txnId != 0 && AmbientTransaction.JoinOnly)
        {
            // Raw fabricator_exec: join the active transaction's connection ONLY if one already exists (a
            // DuckDB-managed write is in flight) — then the exec is atomic with the transaction and sees its
            // uncommitted writes. Otherwise autocommit on a fresh connection without creating persistent
            // state (nothing would ever commit it — see AmbientTransaction.JoinOnly).
            if (_txns.TryGetValue(txnId, out var existing))
            {
                lock (existing)
                {
                    if (existing.Connection is not null)
                    {
                        return (existing.Connection, existing.Transaction, false);
                    }
                }
            }
            var own = OpenConnection();
            own.Open();
            return (own, null, true);
        }
        if (txnId != 0)
        {
            var state = EnsureTxnConnection(txnId);
            lock (state)
            {
                return (state.Connection!, state.Transaction, false);
            }
        }
        var connection = OpenConnection();
        connection.Open();
        return (connection, null, true);
    }

    // Opens (once) the connection + provider transaction pinned to DuckDB transaction `txnId`, at the level
    // ResolveTxnIsolation picks, and returns the state with a non-null Connection.
    //
    // ⚠ Called from TWO places now — the first WRITE (BeginWrite) and, under the mssql_read_isolation opt-in,
    // the first READ. That is the substantive half of the opt-in: before it, a read-only transaction had no
    // server-side transaction at all, so there was nothing for an isolation level to apply to and no amount of
    // level-setting could have given cross-statement stability.
    private TxnState EnsureTxnConnection(long txnId)
    {
        var state = _txns.GetOrAdd(txnId, _ => new TxnState());
        // One thread at a time touches a given transaction (DuckDB serializes a transaction's statements), so
        // locking the single state is enough; distinct transactions use distinct states.
        lock (state)
        {
            if (state.Connection is null)
            {
                // ⚠ The mode comes from the SAME call that chose the connection string, so the recorded
                // value cannot disagree with the connection: every routing decision for this transaction
                // reads it back rather than re-resolving a session setting that may since have changed.
                var conn = OpenConnection(out bool mars);
                conn.Open();
                var level = ResolveTxnIsolation();
                // ⚠ Probe ONLY when the level came from the OPT-IN, never when it came from the profile.
                // ServerProfile.DefaultWriteIsolation is "snapshot" on Fabric/Synapse-serverless, so an
                // unconditional check would add a round trip to EVERY write transaction on the engines where
                // snapshot is the only level there is — and could fail on a surface that does not expose the
                // DMV. Fabric needs no probe: it cannot be turned off there.
                if (level == IsolationLevel.Snapshot && !string.IsNullOrEmpty(ResolveReadIsolation()))
                {
                    // Msg 3952 otherwise, which names the ALTER but not which of our options asked for it.
                    EnsureSnapshotIsolationAllowed(conn, "mssql_read_isolation='snapshot'");
                }
                state.MarsEnabled = mars; // before publishing Connection — TxnMars() reads it under this lock
                state.Connection = conn;
                state.Transaction = level == IsolationLevel.Unspecified
                    ? conn.BeginTransaction()
                    : conn.BeginTransaction(level);
            }
            return state;
        }
    }

    /// <summary>
    /// Sets a session-context key on THIS transaction's pinned connection and returns the identifiers Fabric's
    /// monitoring keys on. Backs <c>db.dbo.fabricator_session_tag(key, value)</c> — see
    /// <see cref="SqlServerSessionTagFunction"/> for why it cannot be done with <c>fabricator_exec</c>.
    /// </summary>
    /// <remarks>
    /// <para>The call goes through <see cref="BeginWrite"/> WITHOUT the join-only restriction, so it PINS the
    /// transaction's connection (creating the <see cref="TxnState"/> if the transaction has not written yet).
    /// That is the point: everything the transaction does afterwards reuses that connection, so the tag applies
    /// to the actual work rather than to a pooled connection that was handed straight back.</para>
    /// <para>Rejected outside an explicit transaction on purpose. In autocommit the pin is committed and released
    /// with this very statement, so the tag would be set and discarded — a silent no-op, which is the failure
    /// mode this whole function exists to eliminate.</para>
    /// <para>Key and value are PARAMETERS, never interpolated: they are user text, and a correlation id is
    /// exactly the sort of value someone eventually builds by string concatenation.</para>
    /// </remarks>
    internal RecordBatch SetSessionTag(string key, string? value)
    {
        long txnId = AmbientTransaction.Current;
        if (txnId == 0 || !_explicitTxns.ContainsKey(txnId))
        {
            throw new NotSupportedException(
                $"{SqlServerSessionTagFunction.FunctionName}: must be called inside an explicit transaction "
                + "(BEGIN … COMMIT). In autocommit the connection it tags is released with this statement, so "
                + "the tag would not apply to anything. In dbt, use a pre-hook on a transactional model.");
        }
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            using (var set = connection.CreateCommand())
            {
                set.Transaction = transaction;
                // The values ride as PARAMETERS (they are user text), which has one consequence worth the
                // trailing comment: a parameterized EXEC is recorded in queryinsights as
                // "EXEC sp_set_session_context @key, @value" with the VALUES ABSENT, so the tag would be
                // invisible to anyone searching the history for it — measured. A caller who keeps the returned
                // connection_id does not care, but a dbt pre-hook discards its result set, so the tag has to be
                // findable from the history alone. The comment puts it back in `command` while the actual
                // parameters stay parameters; `*/` is stripped so a hostile value cannot escape the comment.
                set.CommandText = "EXEC sp_set_session_context @key, @value "
                                  + $"/* fabricator_session_tag {CommentSafe(key)}={CommentSafe(value)} */";
                set.Parameters.Add(new SqlParameter("@key", System.Data.SqlDbType.NVarChar, 128) { Value = key });
                set.Parameters.Add(new SqlParameter("@value", System.Data.SqlDbType.NVarChar, 4000)
                {
                    Value = (object?)value ?? DBNull.Value,
                });
                set.CommandTimeout = ResolveCommandTimeout();
                set.ExecuteNonQuery();
            }
            // Read the ids back on the SAME connection. connection_id joins the two queryinsights views (and is
            // a GUID, unlike the reusable spid); dist_statement_id is the Capacity Metrics "Operation Id".
            //
            // connection_id comes from sys.dm_exec_CONNECTIONS, not dm_exec_requests, and that choice fixed a
            // real flakiness: the request-level view describes the CURRENTLY EXECUTING request, so it
            // intermittently had no row yet and MAX() returned NULL — the suite failed at a moving line about
            // one run in three. The connection-level view always has a row for a live session. (Note box SQL
            // Server returns SEVERAL rows per spid because MARS maps multiple logical connections onto one
            // session; Fabric, which has no MARS, returns one. MAX picks deterministically either way, and only
            // Fabric has the monitoring views this id is for.) dist_statement_id stays request-level and is
            // legitimately NULL when no distributed statement is in flight.
            string? connectionId = null, distStatementId = null;
            int sessionId = 0;
            try
            {
                using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText =
                    "SELECT @@SPID AS session_id, "
                    + "(SELECT MAX(CONVERT(varchar(64), c.connection_id)) FROM sys.dm_exec_connections c "
                    + "  WHERE c.session_id = @@SPID) AS connection_id, "
                    + "(SELECT MAX(CONVERT(varchar(64), r.dist_statement_id)) FROM sys.dm_exec_requests r "
                    + "  WHERE r.session_id = @@SPID) AS dist_statement_id";
                read.CommandTimeout = ResolveCommandTimeout();
                using var reader = read.ExecuteReader();
                if (reader.Read())
                {
                    sessionId = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                    connectionId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    distStatementId = reader.IsDBNull(2) ? null : reader.GetString(2);
                }
            }
            catch (SqlException ex)
            {
                // The TAG is the point; these ids are diagnostics. Reading them needs VIEW SERVER STATE (or its
                // Fabric equivalent), which a restricted principal may lack — losing the ids must not fail a tag
                // that was actually set. The caller sees NULLs and can still correlate via the recorded comment.
                Log.LogDebug(ex, "session_tag: could not read the session's monitoring ids");
            }
            Log.LogDebug("session_tag {Key}={Value} on connection {ConnectionId} (spid {Spid})",
                         key, value, connectionId, sessionId);
            return TagRow(connectionId, sessionId, distStatementId, key, value);
        }
        finally
        {
            // `owns` is false here by construction (an explicit transaction always yields the pinned
            // connection), but honour the contract rather than assuming it.
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    // Makes a value safe to embed in a /* … */ comment: only the comment terminator can escape it, and a
    // newline would split the recorded command text awkwardly. Not a security boundary on its own — the values
    // are still passed as parameters — but the comment must not be breakable.
    private static string CommentSafe(string? value) =>
        (value ?? "").Replace("*/", "* /").Replace('\r', ' ').Replace('\n', ' ');

    private static RecordBatch TagRow(
        string? connectionId, int sessionId, string? distStatementId, string key, string? value)
    {
        var cid = new StringArray.Builder();
        var sid = new Int32Array.Builder();
        var dsid = new StringArray.Builder();
        var k = new StringArray.Builder();
        var v = new StringArray.Builder();
        cid.Append(connectionId);
        sid.Append(sessionId);
        dsid.Append(distStatementId);
        k.Append(key);
        v.Append(value);
        return new RecordBatch(SqlServerSessionTagFunction.Columns,
                               new IArrowArray[] { cid.Build(), sid.Build(), dsid.Build(), k.Build(), v.Build() },
                               1);
    }

    public IArrowArrayStream ExecuteQuery(string sql) => ExecuteQuery(sql, null);

    public IArrowArrayStream ExecuteQuery(string sql, IReadOnlyList<SqlParameter>? parameters) =>
        ExecuteQuery(sql, parameters, readYourWrites: false);

    // A short metadata read (e.g. FetchTableColumns / FetchRowIdColumns) that must see the transaction's
    // own uncommitted writes — e.g. CREATE TABLE then immediately re-fetch the new table's columns to build
    // the catalog entry. Unlike a data SCAN, it holds no long-lived reader, so it is safe on the pinned
    // connection even without MARS (see ExecuteQuery's readYourWrites note). Routes through the pinned
    // write connection whenever one exists, regardless of MARS.
    internal IArrowArrayStream ExecuteMetadataQuery(string sql) => ExecuteQuery(sql, null, readYourWrites: true);

    // Drains `source` completely and hands back an equivalent in-memory stream, disposing the source (and
    // with it the reader, so nothing is left outstanding on the connection). This is the whole point of
    // a marked scan the provider chose to drain (see SinkRequiresDrainedScan): SQL Server refuses a bulk
    // load while a result set is still open on the same
    // MARS connection (error 595), and closing the reader before the sink starts is what removes it.
    // Costs the pipelining — the full source is buffered before the sink sees a row — which is why the
    // host only asks for it when a scan and a sink share a catalog.
    private static IArrowArrayStream DrainToMemory(IArrowArrayStream source)
    {
        try
        {
            var batches = new List<RecordBatch>();
            // ⚠ The row count exists ONLY for the mark, so it is gated — MemoryProbe's own rule. Off (the
            // default) this adds one bool test per batch.
            bool marking = Fabricator.Bridge.MemoryProbe.Enabled;
            long rows = 0;
            while (true)
            {
                var batch = source.ReadNextRecordBatchAsync().GetAwaiter().GetResult();
                if (batch is null)
                {
                    break;
                }
                batches.Add(batch);
                if (marking)
                {
                    rows += batch.Length;
                }
            }
            // The ONLY unbounded in-memory buffer on the read path, and until now the one heavy path with no
            // mark on it — every other mark is DML or bulk. It carries BOTH consumers: the default
            // same-catalog read+write materialisation, and the no-MARS mssql_read_isolation route where every
            // read is drained. `heap` is the buffer; `ws` includes DuckDB's side and lags (see MemoryProbe).
            if (marking)
            {
                Fabricator.Bridge.MemoryProbe.Mark("mssql scan: drained to memory", rows);
            }
            return new InMemoryArrayStream(source.Schema, batches);
        }
        finally
        {
            // Always — on the throw path too, or a failed drain leaves the reader open on the pinned
            // connection and the very collision this exists to prevent happens on the NEXT statement.
            source.Dispose();
        }
    }

    // SET mssql_materialize wins if set, else this catalog's `materialize` ATTACH option, else TRUE.
    //
    // ⚠ THE FLAT `true` IS A USER DECISION (2026-08-11), AND IT IS NOT THE HAZARD THE HISTORY MAKES IT LOOK.
    // Between 2026-08-10 and 2026-08-11 this read `?? TxnMars()`, i.e. it FOLLOWED MARS, because draining PINS
    // the marked scan onto the transaction's connection and `SqlBulkCopy.WriteToServer` wanted that same
    // connection — box with `mssql_mars='false'` failed 0 of 8 and Fabric 4 of 4. **That measurement predates
    // the BULK DEFERRAL, which landed the same day.** `BulkSession` now waits for the first batch (or
    // end-of-stream) before calling BulkInsert, so the load always acquires SECOND, after a drained scan has
    // finished and released. MEASURED after it: `mars='false'` + `materialize='true'` went 0 of 8 to **8 of 8,
    // with read-your-writes restored** — gated at verify_mars_off_same_catalog §6, which calls it a supported
    // configuration rather than an accident.
    //
    // ⇒ the flat default makes that working configuration the default on EVERY engine, MARS or not, and
    // read-your-writes on the scanned table comes with it (measured 15 rows where the pooled route answers
    // 10). The real trade is DRAIN vs STREAM — the whole source is buffered in memory with no spill, and a
    // same-catalog 1M-row CTAS on Fabric measured ~27% more CPU and 484 MB more allocation drained than
    // streamed. `SET mssql_materialize='false'` (or the `materialize false` ATTACH option) takes the
    // snapshot-read route and buys the streaming back, at the cost of read-your-writes.
    //
    // ⚠ DO NOT RE-DERIVE THE OLD WARNING FROM THE OLD NUMBERS. "No MARS ⇒ draining deadlocks" was true for
    // one day and is false now; the 0-of-8 grid in docs/transactions.md §5.6a describes the pre-deferral
    // engine. I asserted the hazard three times from those numbers before measuring it.
    //
    // ⚠ FALSE HERE MEANS THE SNAPSHOT-READ ROUTE, NOT A PLAIN POOLED READ, and that distinction is what makes
    // it safe. `ScanFromSource` maps it to `snapshotRead`, i.e. pooled AND at SNAPSHOT — which is exempt from
    // the EnsureScanCannotSelfBlock refusal because a versioned read cannot wait on this transaction's locks.
    // Returning false all the way up (not drained AND not snapshot-read) would instead give a plain READ
    // COMMITTED pooled read, which that precheck REFUSES — trading a hang for a refusal rather than a fix.
    //
    // ⚠ ITS PREREQUISITE IS SNAPSHOT ISOLATION, which is why this follows MARS rather than being unconditional.
    // Fabric/Synapse are snapshot-versioned by construction, so the engines that lack MARS are exactly the
    // engines that have the prerequisite. A box user who FORCES `mssql_mars='false'` on a database without
    // ALLOW_SNAPSHOT_ISOLATION gets a clear error from the `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`, not a
    // hang — and that combination is opt-in twice over.
    //
    // ⚠ THE COST IS READ-YOUR-WRITES ON THE SCANNED TABLE, accepted deliberately (user, 2026-08-10): this path
    // exists for bulk movement, where observing the transaction's own uncommitted rows is not the point. It
    // takes nothing away that worked — every configuration that WOULD have delivered read-your-writes here is
    // one of the failing rows above.
    internal bool ResolveMaterialize()
    {
        var set = ProviderSettingsStore.Instance.GetBool(SqlServerBackend.ProviderName, "mssql_materialize");
        return set ?? _materialize ?? true;
    }

    /// <summary>
    /// Did someone ASK for <c>materialize=false</c> (via <c>SET</c> or the ATTACH option), as opposed to it
    /// resolving false from the MARS-derived default?
    /// </summary>
    /// <remarks>
    /// ⚠ The distinction only started to matter when the default stopped being a constant. A rule written as
    /// "the user set X" must test what the USER supplied, not what the resolver returned — otherwise the day
    /// the default changes, the rule silently starts applying to everybody. That is exactly what happened
    /// here: the read_isolation contradiction check tested the resolved value and became a hard error for
    /// every no-MARS user of `mssql_read_isolation`.
    /// </remarks>
    private bool MaterializeExplicitlyFalse()
    {
        var set = ProviderSettingsStore.Instance.GetBool(SqlServerBackend.ProviderName, "mssql_materialize");
        return (set ?? _materialize) == false;
    }

    // Does a scan the host MARKED (its plan writes to this catalog) actually have to be drained?
    //
    // ⚠ THE HOST NO LONGER ANSWERS THIS, and this method is why. Whether an open reader is a problem depends
    // on how WE are about to write, which the host cannot know. Today there is exactly one case where the
    // answer is no, and it is worth real memory:
    //
    //   INSERT INTO <SQL Server EXTERNAL table> SELECT ... FROM <a table in this catalog>
    //
    // routes the write to STORAGE (ExternalTableInsert -> parquet/Delta on S3 or ADLS), so no SqlBulkCopy
    // ever runs on the pinned connection and there is nothing for an outstanding result set to collide with.
    // MEASURED before this split: that shape drained 200 000 rows into memory for no reason
    // (`mssql scan: drained to memory` beside `delta bulk: streamed to files`), and the drain has no spill —
    // roughly one byte of process working set per byte of result.
    //
    // ⚠ Restricted to `insert`. A CTAS/replace over an external table is NOT the storage-write shape
    // (BulkInsert's own guard is `!createTable && !replace`), so claiming it here would stream a scan into a
    // bulk load that really does run.
    //
    // ⚠ DetectExternalTable is CACHED per table and returns null immediately on a warehouse engine, so this
    // costs nothing in the common case — and on the path where it does probe, BulkInsert is about to make
    // the identical call moments later and hit the same cache entry.
    //
    // A future write path that holds no reader — Fabric `COPY INTO` over staged parquet, say — extends this
    // method and needs no host change at all. That is the point of the sink being NAMED rather than judged.
    private bool SinkRequiresDrainedScan(ScanSpec.SinkInfo sink, string? touchKey, bool schemaProbe)
    {
        // THE STAGED `COPY INTO` PATH STREAMS. It runs no SqlBulkCopy, so nothing on the write connection can
        // collide with an open reader — and MEASURED 2026-08-10 on Fabric, a same-catalog 1M-row CTAS: four
        // streaming trials 14.5–16.1 s against three drained ones 16.8–28.9 s (no overlap between the two
        // sets), ~27% less CPU (4.5 s vs 6.2 s user) and 484 MB of allocation avoided. Draining serialises
        // the SQL read and the parquet write; streaming overlaps them, and both legs are network-bound.
        //
        // ⚠ THE DRAIN WAS BUYING NOTHING IN THAT SHAPE, which is sharper than "it was slower": BOTH legs
        // logged the scan as `pooled`. On an autocommit CTAS the transaction's connection does not exist yet
        // when the scan starts, so `materialize` had nothing to pin the scan to and the drain bought neither
        // read-your-writes nor 595 protection — only the copy.
        //
        // ⚠ BUT IT IS NOT UNCONDITIONAL, because there IS a shape where the drain is the difference between
        // working and refused: inside an explicit transaction that has already WRITTEN the scanned table, a
        // pooled read waits forever on locks only this transaction can release (limitation 1.15). Draining
        // pins the scan onto that same connection and makes it legal. So the question is asked directly —
        // via the same predicate the refusal uses, so the two cannot disagree.
        // ⚠ IT NEVER DRAINS, AND GIVING UP READ-YOUR-WRITES HERE IS A DELIBERATE PRODUCT DECISION (user,
        // 2026-08-10) RATHER THAN AN OVERSIGHT. A staged load is chosen for BULK — the shape where the source
        // is a large scan and the point is to move bytes, not to observe this transaction's own uncommitted
        // rows. So the scan reads a committed snapshot and streams.
        //
        // An earlier version drained when the transaction had already written the scanned table, to keep
        // read-your-writes for that one shape. It was removed for two reasons: it bought a guarantee this
        // path does not need, and it was ACTIVELY HARMFUL — draining pins the scan onto the write connection,
        // where it collides with the sink's own CREATE TABLE (issued from the bulk's background thread) on a
        // no-MARS engine, hanging 30 s and killing the transaction. MEASURED both ways: drained ⇒ hang;
        // streaming ⇒ commits, every query pooled, 0 failures. ⚠ That collision is PRE-EXISTING and not ours
        // — the same shape fails identically with no staging at all, on the plain SqlBulkCopy path — so this
        // does not fix it, it just declines to walk into it.
        if (ResolveCopyIntoStaging() is not null)
        {
            return false;
        }
        // ⚠ WITH MARS OFF THE DRAIN BUYS A SECOND THING, and skipping it here would be a quiet regression.
        // An ordinary scan reaches the PINNED connection only when `TxnMars() || readYourWrites ||
        // materialize` (see ExecuteQuery), so on a no-MARS engine the drain is the ONLY reason the scan runs
        // inside the transaction at all — dropping it would cost read-your-writes, and (where this
        // transaction has already written the scanned table) turn a working statement into limitation 1.15's
        // refusal. The 595 argument below is about the sink; this one is about the source, and it is why the
        // question is "does the drain buy anything", not "is a bulk load coming".
        //
        // Fabric/Synapse are unaffected either way: DetectExternalTable returns null on a warehouse engine,
        // so the branch below could never fire there. This guard is what protects box with mssql_mars=false.
        if (ResolveCopyIntoStaging() is not null)
        {
            return false;
        }
        if (!TxnMars())
        {
            return true;
        }
        if (string.Equals(sink.Kind, "insert", StringComparison.OrdinalIgnoreCase) &&
            DetectExternalTable(sink.Schema, sink.Table) is not null)
        {
            return false; // the write goes to storage, not through this connection
        }
        return true;
    }

    // SET mssql_read_isolation wins if set, else this catalog's `read_isolation` ATTACH option, else "" (OFF).
    // "" is the shipped behaviour: reads do not join the transaction. See ResolveTxnIsolation.
    internal string ResolveReadIsolation()
    {
        var set = ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_read_isolation");
        return string.IsNullOrEmpty(set) ? _readIsolation : set;
    }

    // SET mssql_copy_into_staging wins if set, else this catalog's `copy_into_staging` ATTACH option, else
    // null = OFF (SqlBulkCopy, the shipped behaviour).
    //
    // ⚠ A CONFIGURED LOCATION ON AN ENGINE WITH NO `COPY INTO` IS IGNORED, NOT REFUSED — deliberately, and
    // it is the same split this codebase applies to the Delta write options. A SET spans every catalog in the
    // session, so a dbt project attaching a Fabric warehouse AND a box SQL Server would have its box writes
    // fail on a setting that was never aimed at them. The ATTACH option names one catalog and could in
    // principle refuse, but it is parsed before any connection exists, so the engine is not yet known — and
    // an option that refuses through one door and is ignored through the other is worse than one rule.
    internal StagingLocation? ResolveCopyIntoStaging()
    {
        if (!Profile.IsWarehouse)
        {
            return null;
        }
        var set = ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_copy_into_staging");
        var location = string.IsNullOrEmpty(set) ? _copyIntoStaging : set;
        if (string.IsNullOrEmpty(location))
        {
            return null;
        }
        if (!WarehouseCopyInto.CanStage)
        {
            // The parquet is written through the host's own COPY, so with no host query surface there is no
            // way to stage. Loud rather than a silent fallback: the user asked for this path by naming a
            // location, and quietly loading over TDS instead would look like the option had no effect.
            throw new NotSupportedException(
                "mssql_copy_into_staging is set, but the host query surface needed to write the staged "
                + "parquet is unavailable in this context.");
        }
        return OneLakeStagingLocation.Parse(location);
    }

    // The level the DuckDB transaction's pinned SqlTransaction is opened at.
    //
    // ⚠ ONE resolver, used by BOTH BeginWrite and the read pin, and that is the point: with two, the level
    // would depend on whether a READ or a WRITE touched the catalog first — the transaction is opened once, by
    // whichever came first, and silently keeps that level. A transaction whose isolation depends on statement
    // order is the kind of thing that is only ever discovered in production.
    private IsolationLevel ResolveTxnIsolation()
    {
        var read = ResolveReadIsolation();
        // Profile is already detected (OpenConnection runs EnsureProfile). Warehouse engines report
        // "snapshot" (Fabric's only level); box reports empty => Unspecified => connection/server default.
        return ParseIsolationLevel(string.IsNullOrEmpty(read) ? _profile!.DefaultWriteIsolation : read);
    }

    // -1 unknown, 0 disallowed, 1 allowed. Cached per catalog: the answer is a database property, and the
    // check costs a round trip we only ever pay on the opt-in mssql_materialize=false path.
    private int _snapshotIsolationAllowed = -1;

    // mssql_materialize=false keeps a scan of the catalog being written STREAMING by putting it on a pooled
    // connection at SNAPSHOT isolation. That requires ALLOW_SNAPSHOT_ISOLATION on the database, and there is
    // no way to satisfy it from here — enabling it is an ALTER DATABASE with a tempdb version-store cost that
    // is the DBA's decision, not ours. So: fail with a message that names the setting AND the remedy.
    private void EnsureSnapshotIsolationAllowed(SqlConnection connection, string? asker = null)
    {
        if (_snapshotIsolationAllowed < 0)
        {
            using var probe = connection.CreateCommand();
            // ⚠ 0 = OFF, 1 = ON, 2/3 = in transition — VERIFIED against sys.databases, not recalled. A first
            // version of this used 2 for OFF (the value actually means "in transition to on"), so the probe
            // read a disabled database as enabled and let SQL Server's raw Msg 3952 through instead — the
            // exact confusion this method exists to prevent.
            //
            // Only a definite 0 refuses. A transitional state is treated as allowed: SQL Server's own error is
            // the authoritative answer, and refusing mid-transition would fail a statement about to be legal.
            probe.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()";
            probe.CommandType = CommandType.Text;
            var state = probe.ExecuteScalar();
            _snapshotIsolationAllowed = (state is byte b && b == 0) ? 0 : 1;
        }
        if (_snapshotIsolationAllowed == 0 && asker is not null)
        {
            throw new InvalidOperationException(
                $"fabricator: {asker} needs snapshot isolation, which is not enabled on this database. It " +
                "opens this DuckDB transaction's SQL Server transaction at SNAPSHOT so every read inside it " +
                "sees one consistent view. Either run ALTER DATABASE [<db>] SET ALLOW_SNAPSHOT_ISOLATION ON, " +
                "or unset mssql_read_isolation (the default), in which case each read takes its own view.");
        }
        if (_snapshotIsolationAllowed == 0)
        {
            throw new InvalidOperationException(
                "fabricator: mssql_materialize=false needs snapshot isolation, which is not enabled on this " +
                "database. It keeps a scan of the table being written STREAMING by reading it on a separate " +
                "connection at SNAPSHOT isolation; without that the read would block on the write's locks " +
                "indefinitely. Either run ALTER DATABASE [<db>] SET ALLOW_SNAPSHOT_ISOLATION ON, or leave " +
                "mssql_materialize at its default (true), which buffers the scan instead and needs nothing.");
        }
    }

    // The pinned-connection half of ExecuteQuery, extracted only so the no-MARS read_isolation path can wrap
    // the WHOLE execute+drain in one lock (see TxnState.ExecGate). Behaviour is identical to the inline form
    // it replaced.
    private IArrowArrayStream RunPinned(SqlConnection pinned, SqlTransaction? pinnedTransaction, string sql,
                                        IReadOnlyList<SqlParameter>? parameters, bool materialize,
                                        InterruptScope? interrupt, System.Threading.CancellationToken token)
    {
        var pinnedCommand = pinned.CreateCommand();
        pinnedCommand.CommandText = sql;
        pinnedCommand.CommandType = CommandType.Text;
        pinnedCommand.CommandTimeout = ResolveCommandTimeout();
        pinnedCommand.Transaction = pinnedTransaction;
        AddParameters(pinnedCommand, parameters);
        try
        {
            var pinnedReader = pinnedCommand.ExecuteReaderAsync(token).GetAwaiter().GetResult();
            IArrowArrayStream pinnedStream = new DbDataReaderArrowStream(
                pinned, pinnedCommand, pinnedReader, ownsConnection: false, interrupt: interrupt);
            return materialize ? DrainToMemory(pinnedStream) : pinnedStream;
        }
        catch
        {
            pinnedCommand.Dispose();
            interrupt?.Dispose();
            throw;
        }
    }

    public IArrowArrayStream ExecuteQuery(string sql, IReadOnlyList<SqlParameter>? parameters, bool readYourWrites)
        => ExecuteQuery(sql, parameters, readYourWrites, materialize: false);

    public IArrowArrayStream ExecuteQuery(string sql, IReadOnlyList<SqlParameter>? parameters, bool readYourWrites,
                                          bool materialize)
        => ExecuteQuery(sql, parameters, readYourWrites, materialize, snapshotRead: false);

    // `snapshotRead` is the mssql_materialize=false route for a scan the planner marked: instead of draining
    // the reader, keep STREAMING it but force it onto a POOLED connection at SNAPSHOT isolation, so it shares
    // no connection with the pinned writer.
    //
    // ⚠ THE SNAPSHOT IS NOT OPTIONAL AND MUST NEVER FALL BACK TO READ COMMITTED. MEASURED on box, 100k rows,
    // pooled streaming read + pinned write: under READ COMMITTED it HANGS — an unbounded lock wait, no error,
    // no timeout (the reader blocks on the writer's locks). With versioned reads the same shape completes at
    // exactly 2N rows. So a silent fallback would turn an opt-in optimisation into a deadlock.
    //
    // It requires ALLOW_SNAPSHOT_ISOLATION on the database; without it SQL Server raises Msg 3952, which names
    // the ALTER to run. That is a documented prerequisite of the setting, and a loud failure rather than a
    // silent one — which is also what keeps the reader from ever seeing the rows the write is producing.
    public IArrowArrayStream ExecuteQuery(string sql, IReadOnlyList<SqlParameter>? parameters, bool readYourWrites,
                                          bool materialize, bool snapshotRead)
    {
        // Inside a transaction that has a pinned connection (a write has happened), read on that connection
        // so the query sees uncommitted changes (read-your-writes). Borrowed: the stream must not dispose the
        // connection. For a data SCAN this is gated on MARS — an open scan reader and the transaction's DML
        // can only coexist on one connection under MARS, so with MARS off (Fabric/Synapse, or mssql_mars=false)
        // scans take a fresh pooled connection (documented warehouse trade-off — docs/transactions.md §5.1).
        // A METADATA read (readYourWrites) is exempt from the MARS gate: it fully drains immediately (no held
        // reader), and on MARS-off the pinned connection never carries a concurrent scan reader, so reusing it
        // is safe — and REQUIRED so a just-created table's metadata is visible (else the self-healing cache
        // would evict the table the CREATE just made; see FabricatorSchemaEntry::CreateTable).
        SqlConnection? pinned = null;
        SqlTransaction? pinnedTransaction = null;
        long txnId = AmbientTransaction.Current;
        // OPT-IN (mssql_read_isolation): CREATE the pin for a read, so a transaction that has not written
        // still has a server-side transaction for the level to apply to. Without this the block below finds no
        // state at all and the read goes pooled — which is why the level alone was never enough.
        //
        // ⚠ NOT for a snapshotRead scan: that is mssql_materialize=false explicitly asking for a POOLED read
        // outside the transaction, the opposite request. Refused rather than silently resolved (below).
        // ⚠ NOT in autocommit (txnId == 0): there is no transaction to be stable across.
        //
        // ⚠ AND ONLY WHEN THE FALSE WAS ASKED FOR — `MaterializeExplicitlyFalse`, not `!ResolveMaterialize()`.
        // Since `mssql_materialize` began DEFAULTING to MARS (2026-08-10), a no-MARS engine resolves it to
        // false with nobody having requested anything, so testing the RESOLVED value made this refusal fire
        // for every user who set `mssql_read_isolation` alone — a hard error on Fabric/Synapse, where the
        // default is always false. MEASURED on box with `mssql_mars='false'`: 3 of 3 runs refused. The whole
        // premise of the message below is that BOTH are active requests; a default is not a request.
        if (snapshotRead && txnId != 0 && MaterializeExplicitlyFalse() &&
            !string.IsNullOrEmpty(ResolveReadIsolation()))
        {
            // Both are ACTIVE requests and they contradict: one asks for every read to be inside the
            // transaction, the other for this particular read to be outside it on a pooled connection. Honouring
            // either silently would give the statement a view the user did not ask for, so refuse and let them
            // pick. (mssql_materialize=false only ever marks a same-catalog read+write scan, so this cannot
            // fire on an ordinary SELECT.)
            throw new InvalidOperationException(
                "fabricator: mssql_materialize=false and mssql_read_isolation contradict each other. The first " +
                "keeps a scan of the table being written STREAMING on a POOLED connection outside this " +
                "transaction; the second puts every read INSIDE it so they share one view. This scan cannot do " +
                "both. Either unset mssql_read_isolation, or leave mssql_materialize at its default (true), " +
                "which buffers that scan onto the transaction's own connection.");
        }
        // Non-null only on the no-MARS read_isolation path: serializes execute+drain on the shared pinned
        // connection (see below). Never taken on the default path, so ordinary routing is unaffected.
        object? execGate = null;
        bool optedMars = false; // the read_isolation pin's own connection mode; only read under readIsolationPin
        bool readIsolationPin = txnId != 0 && !snapshotRead && !string.IsNullOrEmpty(ResolveReadIsolation());
        if (readIsolationPin)
        {
            var opted = EnsureTxnConnection(txnId);
            lock (opted)
            {
                // ⚠ SET DIRECTLY rather than left to the gate below, and that is not style. Routing it
                // through `materialize` would couple this to a condition three lines away: if that gate ever
                // stopped admitting the read, the scan would go POOLED while EnsureScanCannotSelfBlock had
                // already exempted it — and a pooled read against this transaction's own uncommitted writes
                // with MARS off is the UNBOUNDED HANG of limitation 1.15, not an error. Exempting a check
                // must be paid for by guaranteeing the condition it checked for.
                pinned = opted.Connection;
                pinnedTransaction = opted.Transaction;
                optedMars = opted.MarsEnabled; // this connection's own mode, read with the fields it describes
            }
            if (!optedMars)
            {
                // With MARS off the pinned connection admits ONE reader at a time, so BOTH halves are needed
                // and draining alone is NOT enough — measured: two scalar subqueries over one table start in
                // the same millisecond on two threads, so the second ExecuteReader lands while the first is
                // still draining ("The connection does not support MultipleActiveResultSets"). The drain
                // bounds how long a reader is open; the gate stops two from being open at once.
                //
                // This is the cost the opt-in trades streaming for on a no-MARS engine (Fabric/Synapse):
                // transaction-scoped consistency and streaming multi-ref reads are mutually exclusive there,
                // and it picks consistency.
                materialize = true;
                execGate = opted.ExecGate;
            }
        }
        if (!readIsolationPin && txnId != 0 && _txns.TryGetValue(txnId, out var state))
        {
            lock (state)
            {
                // `materialize` joins the MARS/metadata exemptions for the same reason they qualify: the
                // reader is drained before this call returns, so the pinned connection never carries a
                // concurrent scan reader. That is what restores READ-YOUR-WRITES on a no-MARS engine
                // (Fabric/Synapse), where an ordinary scan is routed to a pooled connection and therefore
                // sees only committed state.
                // ⚠ state.MarsEnabled, NOT a freshly resolved value: this decides whether the scan may reuse
                // THIS pinned connection, and only the mode it was opened with answers that.
                //
                // ⚠ DEFENSIVE, NOT GATED — say so rather than implying a test covers it. Re-resolving here
                // is a mutant that SURVIVES the suite, and necessarily: a transaction belongs to ONE DuckDB
                // connection, so the two answers can differ only if that session changes `mssql_mars`
                // BETWEEN pinning the connection and this scan. That is meaningless as a request, and the
                // failure it would cause (a scan sent to a no-MARS pinned connection) is limitation 1.15's
                // unbounded HANG — so a gate for it would be a test that hangs rather than fails, which is
                // worse than none.
                if (state.Connection is not null && !snapshotRead &&
                    (state.MarsEnabled || readYourWrites || materialize))
                {
                    pinned = state.Connection;
                    pinnedTransaction = state.Transaction;
                }
            }
        }
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("query [{Conn}{RYW} txn={Txn} params={P}]: {Sql}",
                pinned is not null ? "pinned" : "pooled", readYourWrites ? " ryw" : "", txnId,
                parameters?.Count ?? 0, Trunc(sql));
        }
        // A DATA scan can run long (slow ExecuteReader, big row set) — wire a query-interrupt scope so Ctrl+C /
        // timeout cancels the async SqlClient calls. A METADATA read (readYourWrites) is short + happens at
        // bind/catalog time (no live scan context to poll), so it stays uncancelled. See docs/cancellation.md.
        var interrupt = readYourWrites ? null : new InterruptScope(AmbientOpener.Current);
        var token = interrupt?.Token ?? default;
        if (pinned is not null)
        {
            // execGate is non-null only on the no-MARS read_isolation path, where `materialize` is forced
            // true — so the gate is released with the reader already drained and closed, never held across a
            // stream the caller is still pulling from.
            if (execGate is not null)
            {
                lock (execGate)
                {
                    return RunPinned(pinned, pinnedTransaction, sql, parameters, materialize, interrupt, token);
                }
            }
            return RunPinned(pinned, pinnedTransaction, sql, parameters, materialize, interrupt, token);
        }

        SqlConnection? connection = null;
        SqlCommand? command = null;
        try
        {
            connection = OpenConnection();
            connection.OpenAsync(token).GetAwaiter().GetResult();
            if (snapshotRead)
            {
                // PRECONDITION FIRST, and deliberately not a try/catch around the read. SET TRANSACTION
                // ISOLATION LEVEL SNAPSHOT SUCCEEDS on a database that disallows it — SQL Server only raises
                // Msg 3952 later, when a snapshot transaction first touches a USER table, which on this path
                // is inside the lazily-consumed reader. The user would then see it as "failed to read next
                // batch from stream: Snapshot isolation transaction failed ...", naming neither the setting
                // they changed nor the fact that it is OUR opt-in path that requires it.
                EnsureSnapshotIsolationAllowed(connection);

                // Session-scoped, so it governs the implicit transaction this SELECT runs in. Set on every
                // such open rather than once: pooled connections are recycled and sp_reset_connection puts
                // the isolation level back to the default, so assuming it persists would silently give us a
                // READ COMMITTED reader — the shape measured to HANG (unbounded lock wait, no error).
                using var iso = connection.CreateCommand();
                iso.CommandText = "SET TRANSACTION ISOLATION LEVEL SNAPSHOT";
                iso.CommandType = CommandType.Text;
                iso.ExecuteNonQuery();
            }
            command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            command.CommandTimeout = ResolveCommandTimeout();
            AddParameters(command, parameters);
            var reader = command.ExecuteReaderAsync(token).GetAwaiter().GetResult();
            IArrowArrayStream pooledStream =
                new DbDataReaderArrowStream(connection, command, reader, interrupt: interrupt);
            return materialize ? DrainToMemory(pooledStream) : pooledStream;
        }
        catch
        {
            command?.Dispose();
            connection?.Dispose();
            interrupt?.Dispose();
            throw;
        }
    }

    internal static void AddParameters(SqlCommand command, IReadOnlyList<SqlParameter>? parameters)
    {
        if (parameters is null)
        {
            return;
        }
        foreach (var p in parameters)
        {
            command.Parameters.Add(p);
        }
    }

    public long ExecuteNonQuery(string sql)
    {
        // A raw exec (fabricator_exec) can be a slow DML (a big UPDATE/DELETE). Cancel it on query interrupt
        // via the async SqlClient token. The opener is set fresh before the exec (FabricatorExecFunction), so
        // AmbientOpener.Current is this statement's ClientContext. See docs/cancellation.md.
        using var interrupt = new InterruptScope(AmbientOpener.Current);
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            command.CommandTimeout = ResolveCommandTimeout();
            command.Transaction = transaction;
            Log.LogDebug("exec [txn={Txn} own={Own}]: {Sql}", AmbientTransaction.Current, owns, Trunc(sql));
            // ExecuteNonQuery returns -1 for statements that don't affect rows
            // (DDL, SET, ...); report 0 for those (matches the C++ mssql extension).
            var affected = Math.Max(0, command.ExecuteNonQueryAsync(interrupt.Token).GetAwaiter().GetResult());
            Log.LogDebug("exec done: affected={Affected}", affected);
            return affected;
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable, bool replace,
                           bool checkConstraints, long txnId, IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode, bool partitionOverwrite,
                           string? optionsJson)
    {
        RecordTouch(schemaName, tableName, schemaChanged: false);
        if (partitionOverwrite)
        {
            // An overwrite flag must never be silently ignored: SQL Server's bulk path has no partition
            // semantics, so honoring it is impossible — reject rather than quietly appending.
            throw new NotSupportedException(
                "COPY PARTITION_OVERWRITE is a Delta-provider option (dynamic partition overwrite); "
                + "the SQL Server provider has no table-partition semantics on the bulk path.");
        }
        // CETAS-analog (slice B): CREATE TABLE ... WITH (location=..., table_type=...) AS ... — the data is
        // written client-side to S3 (Delta/parquet), then the EXTERNAL TABLE is provisioned over it.
        var withOpts = ParseWithOptions(optionsJson);
        if (withOpts is { External: { } extCreate })
        {
            if (!createTable)
            {
                throw new NotSupportedException("WITH options apply to CREATE TABLE [AS], not to an append.");
            }
            if (replace)
            {
                throw new NotSupportedException(
                    "CREATE OR REPLACE with WITH (location=...) is not supported — DROP the external "
                    + "table first (the storage data is kept; delete/replace it explicitly).");
            }
            if (_explicitTxns.ContainsKey(txnId))
            {
                throw new NotSupportedException(
                    "CREATE TABLE ... WITH (location=...) writes directly to storage and cannot roll back "
                    + "with an explicit transaction — COMMIT first (autocommit only).");
            }
            var (clientUri, relLocation) = ParseExternalLocation(extCreate.Location);
            var writeSchema = data.Schema; // capture before the stream is consumed
            long extRows = extCreate.TableType == "DELTA"
                ? ExternalTableRouting.CreateDeltaAs(clientUri, data, txnId, partitionColumns, sortColumns)
                : ExternalTableRouting.AppendParquet(clientUri, data);
            // Data first, DDL second: CREATE EXTERNAL TABLE validates the location, so the table must exist.
            ProvisionExternalTable(extCreate, schemaName, tableName, relLocation,
                                   BuildExternalColumnList(writeSchema));
            return extRows;
        }
        // A detected S3 external table's INSERT routes to STORAGE (slice C) — SQL Server itself can't
        // INSERT into it. Plain appends only; CTAS/replace over an external table is not a bulk shape here.
        if (!createTable && !replace && DetectExternalTable(schemaName, tableName) is { } externalTarget)
        {
            return ExternalTableInsert(externalTarget, schemaName, tableName, data, checkConstraints, txnId);
        }
        if (createTable || replace)
        {
            _externalInfo.TryRemove(ExternalKey(schemaName, tableName), out _); // re-probe after re-create
        }
        // partitionColumns is a Delta/lakehouse concept; SQL Server table partitioning is out of scope here — ignored.
        // sortColumns (native SORTED BY) becomes a Fabric Warehouse WITH (CLUSTER BY (cols)) on the created table.
        // schemaMode (COPY SCHEMA_MODE merge/overwrite) is a Delta concept — ignored here (SQL Server REPLACE already
        // drops+recreates = adopts the source schema; append is strict — evolve with ALTER ADD COLUMN).
        // The bulk-copy runs on a background task (its own pool thread), so the host can't carry the active
        // transaction id to us via the per-thread ambient — it captured it at begin_bulk and hands it here;
        // we re-establish it on THIS thread so BeginWrite + read-your-writes use the right per-transaction
        // connection (joining an explicit BEGIN's pinned connection; a fresh one in autocommit). This is a
        // DuckDB-managed write that creates + owns the per-transaction connection, so normal mode (not join-only).
        AmbientTransaction.Current = txnId;
        AmbientTransaction.JoinOnly = false;
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            string qualified = Quote(schemaName) + "." + Quote(tableName);
            string objectLiteral = "N'" + (schemaName + "." + tableName).Replace("'", "''") + "'";

            if (replace)
            {
                using var drop = connection.CreateCommand();
                drop.Transaction = transaction;
                drop.CommandText = $"IF OBJECT_ID({objectLiteral}, 'U') IS NOT NULL DROP TABLE {qualified}";
                drop.ExecuteNonQuery();
            }
            if (createTable)
            {
                using var create = connection.CreateCommand();
                create.Transaction = transaction;
                // CTAS has no GENERATED-column marker (identityColumns null), but add_identity still applies —
                // the auto surrogate key is engine-generated and absent from the SELECT, so the bulk copy skips it.
                create.CommandText = $"IF OBJECT_ID({objectLiteral}, 'U') IS NULL " +
                                     BuildCreateTable(qualified, data.Schema, Profile, null, null, null, sortColumns,
                                                      null, ResolveAddIdentity(), withOpts);
                // Logged because it was previously invisible: the bulk path's DDL is issued inside SqlBulkCopy's
                // own sequence, so a Debug trace showed only "bulk <table>: create=True" and not the statement.
                // Diagnosing the Fabric in-transaction rename failure needed exactly this text.
                Log.LogDebug("bulk ddl [txn={Txn} own={Own}]: {Sql}",
                             AmbientTransaction.Current, owns, Trunc(create.CommandText));
                create.ExecuteNonQuery();
            }

            // If the source maps an IDENTITY column of an existing target, preserve the
            // explicit values (KeepIdentity); otherwise let SQL Server auto-generate them.
            var options = SqlBulkCopyOptions.Default;
            if (!createTable)
            {
                var identityColumns = GetIdentityColumns(connection, transaction, schemaName, tableName);
                if (identityColumns.Count > 0 &&
                    data.Schema.FieldsList.Any(f => identityColumns.Contains(f.Name)))
                {
                    options |= SqlBulkCopyOptions.KeepIdentity;
                }
            }
            // INSERT enforces CHECK / FOREIGN KEY constraints (SqlBulkCopy skips them by
            // default — a bulk-load optimization that would silently accept rows a
            // classic INSERT would reject). COPY/CTAS pass false for bulk-load speed.
            if (checkConstraints)
            {
                options |= SqlBulkCopyOptions.CheckConstraints;
            }

            // THE STAGED `COPY INTO` PATH (warehouse engines, opt-in via mssql_copy_into_staging). Placed
            // here on purpose: after the CREATE, because COPY INTO requires the target table to exist, and
            // after `options`, so the one bulk-copy behaviour it cannot express is decided by exactly the
            // computation the bulk path uses rather than a second, drifting copy of it.
            if (ResolveCopyIntoStaging() is { } staging)
            {
                // ⚠ KeepIdentity has NO COPY INTO equivalent, so the explicit values would be replaced by
                // engine-generated ones — a silently different table. Refuse and name the way back.
                // (`checkConstraints` needs no such guard: a warehouse enforces no CHECK or FOREIGN KEY
                // constraint at all, so SqlBulkCopy's CheckConstraints is already vacuous here — the two
                // paths cannot diverge on it.)
                if (options.HasFlag(SqlBulkCopyOptions.KeepIdentity))
                {
                    throw new NotSupportedException(
                        $"{qualified}: the insert supplies values for an IDENTITY column, which a staged "
                        + "COPY INTO load cannot preserve (the engine would generate its own). Unset "
                        + "mssql_copy_into_staging for this statement to load it over TDS instead.");
                }
                return WarehouseCopyInto.Load(connection, transaction, qualified, staging, data,
                                              ResolveCommandTimeout());
            }

            using var reader = new ArrowDataReader(data);
            using var bulk = new SqlBulkCopy(connection, options, transaction)
                { DestinationTableName = qualified, BulkCopyTimeout = ResolveCommandTimeout() };
            // Map by name (case-insensitive) so source/target column order need not match.
            foreach (var field in data.Schema.FieldsList)
            {
                bulk.ColumnMappings.Add(field.Name, field.Name);
            }
            Log.LogDebug("bulk {Table}: create={Create} replace={Replace} checkConstraints={Check} " +
                         "options={Options} txn={Txn}", qualified, createTable, replace, checkConstraints, options, txnId);
            bulk.WriteToServer(reader);
            Log.LogInformation("bulk {Table}: {Rows} rows copied", qualified, bulk.RowsCopied);
            return bulk.RowsCopied;
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys)
    {
        RecordTouch(schemaName, tableName, schemaChanged: false);
        // slice D: a detected external DELTA table's rowid is its Delta IDENTITY column — the DELETE routes
        // to storage (identity -> transient rowid resolution + the delta provider's own rowid DELETE).
        if (DetectExternalTable(schemaName, tableName) is { IdentityColumn: { } delIdCol } extDel)
        {
            // A guard throw must not leak the imported key stream (a GC-finalized imported C stream
            // outlives the host's struct and segfaults) — dispose it deterministically.
            try
            {
                GuardExternalDml(schemaName, tableName, "DELETE FROM");
            }
            catch
            {
                keys.Dispose();
                throw;
            }
            return ExternalTableRouting.DeleteByIdentity(extDel.StorageUri!, delIdCol, keys, AmbientTransaction.Current);
        }
        // Cancel a long rowid DELETE (many chunked batches) on query interrupt. The opener is fresh here (the
        // modify operator's Finalize calls FabricatorSetActiveTxn -> SetActiveOpener). See docs/cancellation.md.
        using var interrupt = new InterruptScope(AmbientOpener.Current);
        var token = interrupt.Token;
        var (connection, transaction, owns) = BeginWrite();
        try
        {
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        var keyColumns = keys.Schema.FieldsList.Select(f => f.Name).ToList();

        using var reader = new ArrowDataReader(keys);
        long total = 0;
        var batch = new List<object?[]>();

        void Flush()
        {
            if (batch.Count == 0)
            {
                return;
            }
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = ResolveCommandTimeout();
            var sb = new StringBuilder("DELETE FROM ").Append(qualified).Append(" WHERE ");
            int p = 0;
            for (int r = 0; r < batch.Count; r++)
            {
                if (r > 0)
                {
                    sb.Append(" OR ");
                }
                sb.Append('(');
                for (int c = 0; c < keyColumns.Count; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(" AND ");
                    }
                    string pn = "@p" + p++;
                    sb.Append(Quote(keyColumns[c])).Append(" = ").Append(pn);
                    cmd.Parameters.AddWithValue(pn, batch[r][c] ?? DBNull.Value);
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            if (Log.IsEnabled(LogLevel.Debug))
                Log.LogDebug("dml {Schema}.{Table}: {Sql}", schemaName, tableName, Trunc(cmd.CommandText));
            total += cmd.ExecuteNonQueryAsync(token).GetAwaiter().GetResult();
            batch.Clear();
        }

        while (reader.Read())
        {
            var row = new object?[keyColumns.Count];
            for (int c = 0; c < keyColumns.Count; c++)
            {
                row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
            }
            batch.Add(row);
            if (batch.Count >= 500)
            {
                Flush();
            }
        }
        Flush();
        // Row-scaling: this path issues per-row parameterized DML on the pinned connection, so heap must
        // NOT grow with rows. The rowid keys ARE materialized upstream, which is what the mark checks.
        Fabricator.Bridge.MemoryProbe.Mark("mssql delete: rowid DML complete", total);
        return total;
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data)
    {
        RecordTouch(schemaName, tableName, schemaChanged: false);
        // slice D: identity-keyed UPDATE routes to storage (see ExecuteDelete). SET of the identity column
        // itself is rejected — it is engine-assigned.
        if (DetectExternalTable(schemaName, tableName) is { IdentityColumn: { } updIdCol } extUpd)
        {
            // Guard throws must not leak the imported update stream (finalizer segfault) — dispose it.
            try
            {
                GuardExternalDml(schemaName, tableName, "UPDATE");
                for (int j = 0; j < setColumnCount; j++)
                {
                    if (string.Equals(data.Schema.FieldsList[j].Name, updIdCol, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException(
                            $"UPDATE of the identity column '{updIdCol}' on external table "
                            + $"{schemaName}.{tableName} is not supported (engine-assigned).");
                    }
                }
            }
            catch
            {
                data.Dispose();
                throw;
            }
            return ExternalTableRouting.UpdateByIdentity(extUpd.StorageUri!, updIdCol, setColumnCount, data);
        }
        // Cancel a long rowid UPDATE (one statement per matched row) on query interrupt. Opener is fresh (the
        // modify Finalize's FabricatorSetActiveTxn -> SetActiveOpener). See docs/cancellation.md.
        using var interrupt = new InterruptScope(AmbientOpener.Current);
        var token = interrupt.Token;
        var (connection, transaction, owns) = BeginWrite();
        try
        {
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        var columns = data.Schema.FieldsList.Select(f => f.Name).ToList();
        int keyStart = setColumnCount;

        using var reader = new ArrowDataReader(data);
        long total = 0;
        while (reader.Read())
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = ResolveCommandTimeout();
            var sb = new StringBuilder("UPDATE ").Append(qualified).Append(" SET ");
            for (int c = 0; c < setColumnCount; c++)
            {
                if (c > 0)
                {
                    sb.Append(", ");
                }
                string pn = "@s" + c;
                sb.Append(Quote(columns[c])).Append(" = ").Append(pn);
                cmd.Parameters.AddWithValue(pn, reader.IsDBNull(c) ? DBNull.Value : reader.GetValue(c));
            }
            sb.Append(" WHERE ");
            for (int c = keyStart; c < columns.Count; c++)
            {
                if (c > keyStart)
                {
                    sb.Append(" AND ");
                }
                string pn = "@k" + c;
                sb.Append(Quote(columns[c])).Append(" = ").Append(pn);
                cmd.Parameters.AddWithValue(pn, reader.IsDBNull(c) ? DBNull.Value : reader.GetValue(c));
            }
            cmd.CommandText = sb.ToString();
            if (Log.IsEnabled(LogLevel.Debug))
                Log.LogDebug("dml {Schema}.{Table}: {Sql}", schemaName, tableName, Trunc(cmd.CommandText));
            total += cmd.ExecuteNonQueryAsync(token).GetAwaiter().GetResult();
        }
        Fabricator.Bridge.MemoryProbe.Mark("mssql update: rowid DML complete", total);
        return total;
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows)
    {
        RecordTouch(schemaName, tableName, schemaChanged: false);
        if (DetectExternalTable(schemaName, tableName) is not null)
        {
            rows.Dispose(); // never leak the imported stream to the finalizer
            throw new NotSupportedException(
                $"INSERT ... RETURNING is not supported on external table {schemaName}.{tableName} "
                + "(the write routes to storage, which returns no OUTPUT INSERTED rows) — "
                + "use a plain INSERT.");
        }
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            string qualified = Quote(schemaName) + "." + Quote(tableName);
            var columns = rows.Schema.FieldsList.Select(f => f.Name).ToList();
            string columnList = string.Join(", ", columns.Select(Quote));

            using var reader = new ArrowDataReader(rows);
            var batch = new List<object?[]>();
            Schema? outputSchema = null;
            var outputBatches = new List<RecordBatch>();

            void Flush()
            {
                if (batch.Count == 0)
                {
                    return;
                }
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                var sb = new StringBuilder("INSERT INTO ").Append(qualified).Append(" (").Append(columnList)
                    .Append(") OUTPUT INSERTED.* VALUES ");
                int p = 0;
                for (int r = 0; r < batch.Count; r++)
                {
                    if (r > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append('(');
                    for (int c = 0; c < columns.Count; c++)
                    {
                        if (c > 0)
                        {
                            sb.Append(", ");
                        }
                        string pn = "@p" + p++;
                        sb.Append(pn);
                        cmd.Parameters.AddWithValue(pn, batch[r][c] ?? DBNull.Value);
                    }
                    sb.Append(')');
                }
                cmd.CommandText = sb.ToString();
                var rdr = cmd.ExecuteReader();
                // The OUTPUT result set is the inserted rows; materialize them as Arrow
                // batches (borrowed connection — only the reader/command are disposed).
                using var stream = new DbDataReaderArrowStream(connection, cmd, rdr, ownsConnection: false);
                outputSchema ??= stream.Schema;
                RecordBatch? rb;
                while ((rb = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
                {
                    outputBatches.Add(rb);
                }
                batch.Clear();
            }

            while (reader.Read())
            {
                var row = new object?[columns.Count];
                for (int c = 0; c < columns.Count; c++)
                {
                    row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                }
                batch.Add(row);
                if (batch.Count >= 1000) // SQL Server's row-constructor VALUES limit
                {
                    Flush();
                }
            }
            Flush();

            // No rows inserted: return an empty stream carrying the table's schema.
            outputSchema ??= ReadTableSchema(connection, transaction, qualified);
            return new InMemoryArrayStream(outputSchema, outputBatches);
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    // Reads a table's column layout (zero rows) as an Arrow schema.
    private static Schema ReadTableSchema(SqlConnection connection, SqlTransaction? transaction, string qualified)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT * FROM {qualified} WHERE 1 = 0";
        using var rdr = cmd.ExecuteReader();
        using var stream = new DbDataReaderArrowStream(connection, cmd, rdr, ownsConnection: false);
        return stream.Schema;
    }

    // Metadata reads use ExecuteMetadataQuery (read-your-writes: routed through the pinned write connection
    // when one exists, regardless of MARS) so a table/columns just created in this transaction are visible —
    // otherwise the self-healing cache would evict a freshly CREATEd table on a non-MARS engine (Fabric).
    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        // schema_filter/table_filter (ATTACH options) are applied here so discovery sees only matches —
        // the provider owns its filtering (the C++ core no longer knows these option names). No filter set =>
        // stream the query directly (no materialization).
        MetadataKind.Schemas => SchemasMetadata(),
        MetadataKind.Tables => _schemaFilter is null && _tableFilter is null
                                   ? ExecuteMetadataQuery(TablesSql)
                                   : FilteredTables(),
        // Zero-row result whose Arrow schema describes the table's columns; the
        // C++ host reads that schema to learn the DuckDB column types.
        MetadataKind.Columns => ColumnsMetadata(Require(schema, table).schema, Require(schema, table).table),
        MetadataKind.RowId => RowIdMetadata(Require(schema, table).schema, Require(schema, table).table),
        // Both statistics reads are SKIPPED on a warehouse engine, for the same reason as the external-table
        // probe above: Fabric/Synapse do not support sys.dm_db_partition_stats or sys.dm_db_stats_histogram
        // ("DMV ... is not supported"), and on Fabric a failed statement ABORTS AN OPEN TRANSACTION — so an
        // optional, best-effort statistics query was able to poison a user's transaction. Statistics are
        // costing-only (never pruning), so returning "unknown" costs an optimizer hint and nothing else.
        MetadataKind.RowCount => Profile.IsWarehouse
            ? EmptyStringTable("n")
            : ExecuteMetadataQuery(RowCountSql(Require(schema, table).schema, Require(schema, table).table)),
        MetadataKind.ColumnNdv => Profile.IsWarehouse
            ? EmptyStringTable("column_name", "ndv")
            : ExecuteMetadataQuery(ColumnNdvSql(Require(schema, table).schema, Require(schema, table).table)),
        MetadataKind.Functions => _functionFilter is null
            ? ExecuteMetadataQuery(FunctionsMetadataSql())
            : FilteredFunctions(),
        // The detected capability profile as (property, value) rows — the fabricator_server_info() diagnostic.
        // Built from the in-memory profile (not a re-query), so it surfaces the derived flags.
        MetadataKind.ServerInfo => ServerInfoStream(),
        // No provider virtual columns (the Delta catalog's stable row-tracking pair has no SQL analog).
        MetadataKind.VirtualColumns => EmptyStringTable("name", "type"),
        // Provider-declared CATALOG-BOUND macros (schema, name, create_sql) — bound by the host into this
        // catalog's schemas as db.schema.m(...). Note this deliberately does NOT go through
        // FunctionsMetadataSql: a macro body is a local declaration, so embedding it in a T-SQL literal to send
        // to the server and read back would be pure waste, and would make the declaration depend on server
        // reachability. Gated by schema_filter host-side (a macro whose schema was not registered is dropped).
        MetadataKind.CatalogMacros => CatalogMacroMetadata.Stream(CustomFunctions.CatalogMacros),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "fabricator: unknown metadata kind"),
    };

    /// <summary>
    /// The table's columns, as a zero-row stream whose Arrow schema is the answer — and, when the object is
    /// gone, an <see cref="ObjectNotFoundException"/> rather than the raw provider error.
    ///
    /// <para>The host turns ABSENCE into "this table no longer exists": it drops the catalog entry and the
    /// name, so <c>CREATE TABLE IF NOT EXISTS</c> / <c>OR REPLACE</c> behave correctly after a DROP issued
    /// out of band (via <c>fabricator_exec</c>, or by another session). It used to infer that from ANY
    /// failure here, which meant an unreadable table was erased just as readily as a deleted one; now the
    /// provider has to say so, and this is where SQL Server says it.</para>
    ///
    /// <para>Established from the ERROR NUMBER, not the message: <b>208</b> is SQL Server's "Invalid object
    /// name". Note it also covers an object the principal may not SEE — SQL Server reports 208 rather than a
    /// permission error, deliberately, so as not to leak existence. Treating that as absence is exactly what
    /// this path did before, so the semantics are unchanged; every OTHER error number now keeps its own
    /// message instead of being reported as a missing table.</para>
    /// </summary>
    private IArrowArrayStream ColumnsMetadata(string schemaName, string tableName)
    {
        try
        {
            return ExecuteMetadataQuery(
                $"SELECT * FROM {Quote(schemaName)}.{Quote(tableName)} WHERE 1 = 0");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == InvalidObjectNameError)
        {
            throw new ObjectNotFoundException("table", $"{schemaName}.{tableName}", ex);
        }
    }

    /// <summary>SQL Server error 208, "Invalid object name" — the server's own statement that the object is
    /// not there (or not visible to this principal, which it reports identically on purpose).</summary>
    private const int InvalidObjectNameError = 208;

    // slice D: a detected external DELTA table with a Delta IDENTITY column advertises THAT column as its
    // rowid (an external table has no PK/UNIQUE/IDENTITY SQL-side, so RowIdSql finds nothing) — the scan
    // reads it as a normal column through PolyBase, and ExecuteDelete/ExecuteUpdate resolve the values back
    // to transient rowids on the Delta side. Everything else keeps the standard PK/unique/identity discovery.
    private IArrowArrayStream RowIdMetadata(string schemaName, string tableName)
    {
        if (DetectExternalTable(schemaName, tableName) is { IdentityColumn: { } idCol })
        {
            var s = new Schema(new[] { new Field("name", StringType.Default, nullable: false) }, metadata: null);
            var names = new StringArray.Builder();
            names.Append(idCol);
            return new InMemoryArrayStream(s, new[] { new RecordBatch(s, new IArrowArray[] { names.Build() }, 1) });
        }
        return ExecuteMetadataQuery(RowIdSql(schemaName, tableName, Profile));
    }

    private static IArrowArrayStream EmptyStringTable(params string[] columns)
    {
        var builder = new Schema.Builder();
        foreach (var c in columns)
        {
            builder.Field(new Field(c, StringType.Default, nullable: true));
        }
        return new InMemoryArrayStream(builder.Build(), System.Array.Empty<RecordBatch>());
    }

    // Builds a two-column (property, value) stream from the detected ServerProfile. Accessing Profile
    // detects it (via the non-MARS probe) on first use; for an attached catalog it is already cached.
    private IArrowArrayStream ServerInfoStream()
    {
        // `supports_mars` is the SERVER's capability; `mars_enabled` is what a connection opened by THIS
        // SESSION gets (capability ∧ the mssql_mars setting/ATTACH option), which was observable from SQL
        // nowhere at all. That gap had teeth: `verify_mars_off_same_catalog` exists to exercise the no-MARS
        // path and its own header warns that getting the SET/ATTACH order wrong "silently produced a MARS-ON
        // catalog and a vacuously passing suite" — with no assertion able to tell the difference. This is
        // that assertion's observable.
        //
        // ⚠ SESSION-DEPENDENT since change B — two connections on one catalog can legitimately report
        // different values, which is the feature, not an inconsistency to normalise away.
        var rows = Profile.Properties().Concat(new[] { ("mars_enabled", EffectiveMars() ? "true" : "false") })
                          .ToList();
        var schema = new Schema(new[]
        {
            new Field("property", StringType.Default, nullable: false),
            new Field("value", StringType.Default, nullable: true),
        }, metadata: null);
        var properties = new StringArray.Builder();
        var values = new StringArray.Builder();
        foreach (var (property, value) in rows)
        {
            properties.Append(property);
            values.Append(value);
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { properties.Build(), values.Build() }, rows.Count);
        return new InMemoryArrayStream(schema, new[] { batch });
    }

    // Reads a metadata query's result rows as strings (small result sets: schema/table discovery). Uses the
    // metadata connection (read-your-writes when in a write transaction), mirroring GetMetadata.
    private List<string?[]> ReadMetadataRows(string sql, int columnCount)
    {
        var rows = new List<string?[]>();
        using var src = ExecuteMetadataQuery(sql);
        while (true)
        {
            var batch = src.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            using (batch)
            {
                var cols = new StringArray[columnCount];
                for (int c = 0; c < columnCount; c++)
                {
                    cols[c] = (StringArray)batch.Column(c);
                }
                for (int i = 0; i < batch.Length; i++)
                {
                    var row = new string?[columnCount];
                    for (int c = 0; c < columnCount; c++)
                    {
                        row[c] = cols[c].GetString(i);
                    }
                    rows.Add(row);
                }
            }
        }
        return rows;
    }

    /// <summary>
    /// The advertised schemas: the server's own, optionally <c>schema_filter</c>ed (icase regex, substring),
    /// plus the synthetic
    /// <c>fabric</c> FUNCTION namespace on a Fabric endpoint.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠ Without the synthetic name the whole Fabric function set silently disappears.</b> The host
    /// drops a declared function whose schema it did not register
    /// (<c>FabricatorCatalog::LoadCatalog</c>) — no error, just ~50 missing functions — and <c>fabric</c> is not
    /// a real SQL schema, so <c>sys.schemas</c> will never return it.</para>
    /// <para><b>It is deliberately NOT subject to <c>schema_filter</c></b>, matching
    /// <c>DeltaCatalog.CatalogSchemaNames</c> (which appends it after the already-filtered list). That filter
    /// scopes DATA discovery; silently removing the entire Fabric API because someone narrowed which tables they
    /// wanted would be a surprising coupling, and <c>function_filter</c> is the option that exists for functions.
    /// </para>
    /// <para>The no-filter, non-Fabric case still STREAMS the query with no materialization — the common path is
    /// unchanged.</para>
    /// </remarks>
    private IArrowArrayStream SchemasMetadata()
    {
        bool addFunctionSchema = IsFabricEndpoint(_baseConnectionString);
        if (_schemaFilter is null && !addFunctionSchema)
        {
            return ExecuteMetadataQuery(SchemasSql);
        }
        var schema = new Schema(new[] { new Field("name", StringType.Default, nullable: false) }, metadata: null);
        var names = new StringArray.Builder();
        int n = 0;
        bool haveFunctionSchema = false;
        foreach (var row in ReadMetadataRows(SchemasSql, 1))
        {
            if (row[0] is { } name && (_schemaFilter is null || _schemaFilter.IsMatch(name)))
            {
                names.Append(name);
                n++;
                haveFunctionSchema |= string.Equals(name, FabricApiFunctions.SchemaName,
                                                    StringComparison.OrdinalIgnoreCase);
            }
        }
        // A real SQL schema actually NAMED "fabric" already covers it; appending would advertise a duplicate,
        // which the host's ensure_schema treats as a collision rather than a merge.
        if (addFunctionSchema && !haveFunctionSchema)
        {
            names.Append(FabricApiFunctions.SchemaName);
            n++;
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { names.Build() }, n);
        return new InMemoryArrayStream(schema, new[] { batch });
    }

    // schema_filter + table_filter applied: a table is kept only if its schema matches schema_filter (or none
    // set) AND its name matches table_filter (or none set) — parity with the former C++ CatalogFilters.
    private IArrowArrayStream FilteredTables()
    {
        var schema = new Schema(new[]
        {
            new Field("schema_name", StringType.Default, nullable: false),
            new Field("table_name", StringType.Default, nullable: false),
            new Field("table_type", StringType.Default, nullable: false),
        }, metadata: null);
        var schemas = new StringArray.Builder();
        var tables = new StringArray.Builder();
        var types = new StringArray.Builder();
        int n = 0;
        foreach (var row in ReadMetadataRows(TablesSql, 3))
        {
            if (row[0] is not { } sn || row[1] is not { } tn)
            {
                continue;
            }
            if ((_schemaFilter is null || _schemaFilter.IsMatch(sn)) &&
                (_tableFilter is null || _tableFilter.IsMatch(tn)))
            {
                schemas.Append(sn);
                tables.Append(tn);
                types.Append(row[2]);
                n++;
            }
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { schemas.Build(), tables.Build(), types.Build() }, n);
        return new InMemoryArrayStream(schema, new[] { batch });
    }

    // function_filter applied: only routines whose NAME matches the icase regex are surfaced (so the C++
    // catalog registers only those). Type-preserving (the diagnostic fabricator_functions keeps param_count
    // as INT) — filters the rows of the discovery stream, which schema/name/kind (string) + param_count (int)
    // + return_type (string). schema_filter is applied C++-side (register only functions in registered schemas).
    private IArrowArrayStream FilteredFunctions()
    {
        using var src = ExecuteMetadataQuery(FunctionsMetadataSql());
        var schema = src.Schema;
        int nameIdx = 1; // schema_name(0), name(1), kind(2), param_count(3), return_type(4)
        var outBatches = new List<RecordBatch>();
        while (true)
        {
            var batch = src.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            using (batch)
            {
                var names = (StringArray)batch.Column(nameIdx);
                var keep = new List<int>();
                for (int i = 0; i < batch.Length; i++)
                {
                    var nm = names.GetString(i);
                    if (nm is not null && _functionFilter!.IsMatch(nm))
                    {
                        keep.Add(i);
                    }
                }
                if (keep.Count == 0)
                {
                    continue;
                }
                var cols = new IArrowArray[batch.ColumnCount];
                for (int c = 0; c < batch.ColumnCount; c++)
                {
                    cols[c] = TakeRows(batch.Column(c), keep);
                }
                outBatches.Add(new RecordBatch(schema, cols, keep.Count));
            }
        }
        return new InMemoryArrayStream(schema, outBatches);
    }

    // Selects rows by index from a discovery column, preserving its Arrow type (Utf8 or Int32 — the only
    // types the Functions metadata produces). Apache.Arrow C# has no built-in take, so rebuild via a builder.
    private static IArrowArray TakeRows(IArrowArray col, List<int> idx)
    {
        switch (col)
        {
            case StringArray s:
            {
                var b = new StringArray.Builder();
                foreach (var i in idx) { if (s.IsNull(i)) b.AppendNull(); else b.Append(s.GetString(i)); }
                return b.Build();
            }
            case Int32Array a:
            {
                var b = new Int32Array.Builder();
                foreach (var i in idx) { if (a.IsNull(i)) b.AppendNull(); else b.Append(a.GetValue(i)!.Value); }
                return b.Build();
            }
            default:
                throw new NotSupportedException($"function_filter: unexpected discovery column type {col.GetType().Name}");
        }
    }

    // Discovered SQL Server routines + the provider's custom scalar/table functions, appended via
    // UNION ALL so the C++ catalog discovers + registers them uniformly (then dispatches custom ones to C#).
    private string FunctionsMetadataSql()
    {
        // NOTE: no IsEmpty early-return — the bespoke session-tag row below is always appended, so the
        // UNION ALL is always needed even when no custom functions are registered.
        static string Esc(string s) => s.Replace("'", "''");
        // FunctionsSql ends with ORDER BY; strip it so the UNION ALL is valid (the diagnostic /
        // discovery callers sort themselves, so dropping the ordering here is harmless).
        int orderIdx = FunctionsSql.LastIndexOf(" ORDER BY ", StringComparison.Ordinal);
        var sb = new StringBuilder(orderIdx >= 0 ? FunctionsSql[..orderIdx] : FunctionsSql);
        // One literal row per declaration, in the discovered stream's five-column shape
        // (schema_name, name, kind, param_count, return_type). The host reads the first three; the last two
        // exist because every UNION ALL branch must match the discovery query's arity.
        // The bespoke session-tag function: always present on a SQL Server catalog, and NOT in the function set
        // because it must capture the live catalog (it pins that catalog's CONNECTION), which the per-catalog
        // Functions set still does not carry — it captures ATTACH context (Fabric workspace/item/credential),
        // not a connection. Same "match the name first" pattern the DAX provider uses for daxeval.
        sb.Append(" UNION ALL SELECT 'dbo', '").Append(Esc(SqlServerSessionTagFunction.FunctionName))
          .Append("', 'table', 2, ''");
        foreach (var d in Functions.Declarations(ExpandAllSchemas))
        {
            sb.Append(" UNION ALL SELECT '").Append(Esc(d.SchemaName)).Append("', '").Append(Esc(d.Name))
              .Append("', '").Append(Esc(d.Kind)).Append("', ").Append(d.ParamCount).Append(", '")
              .Append(Esc(d.ReturnType)).Append('\'');
        }
        return sb.ToString();
    }

    // Discovered routines (user scalar/table functions + procedures), uniform shape
    // (schema_name, name, kind, param_count, return_type). For scalar functions the
    // return value is sys.parameters.parameter_id = 0; input params are parameter_id > 0.
    private const string FunctionsSql =
        "SELECT s.name AS schema_name, o.name AS name, " +
        "CASE o.type WHEN 'FN' THEN 'scalar' WHEN 'FS' THEN 'scalar' " +
        "WHEN 'IF' THEN 'table' WHEN 'TF' THEN 'table' WHEN 'FT' THEN 'table' " +
        "WHEN 'P' THEN 'proc' WHEN 'PC' THEN 'proc' ELSE 'other' END AS kind, " +
        "(SELECT COUNT(*) FROM sys.parameters p WHERE p.object_id = o.object_id AND p.parameter_id > 0) AS param_count, " +
        "ISNULL((SELECT t.name FROM sys.parameters p JOIN sys.types t ON p.user_type_id = t.user_type_id " +
        "WHERE p.object_id = o.object_id AND p.parameter_id = 0), '') AS return_type " +
        "FROM sys.objects o JOIN sys.schemas s ON o.schema_id = s.schema_id " +
        "WHERE o.type IN ('FN','FS','IF','TF','FT','P','PC') AND o.is_ms_shipped = 0 " +
        // THIS PROVIDER'S per-row form. A discovered TVF or proc also gets `<name>_each`, which applies it
        // ONCE PER INPUT ROW — a TVF via CROSS APPLY, a proc via a per-row EXEC. It is declared HERE, as an
        // ordinary in-out function, because it is a SQL-Server semantic: the host used to invent a `_each`
        // alias for every table-kind function of every provider, which produced entries that could only fail
        // on providers with nothing to apply per row (30 dead siblings on a Fabric attach alone).
        // Only routines that TAKE parameters get one — there is nothing to apply per input row otherwise.
        "UNION ALL " +
        "SELECT s.name AS schema_name, o.name + '_each' AS name, 'inout' AS kind, 0 AS param_count, " +
        "'' AS return_type " +
        "FROM sys.objects o JOIN sys.schemas s ON o.schema_id = s.schema_id " +
        "WHERE o.type IN ('IF','TF','FT','P','PC') AND o.is_ms_shipped = 0 " +
        "AND EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = o.object_id AND p.parameter_id > 0) " +
        "ORDER BY schema_name, name";

    // Per-column distinct-value estimate (NDV) from existing statistics — (column,
    // ndv) rows. Derived from the leading-column histogram of each stats object:
    // sum of distinct values strictly inside the steps + one per step boundary. Cheap
    // (metadata, no scan); only columns that are a leading stat key appear (others =>
    // no row => unknown). Used ONLY for selectivity estimation, so stale/approximate
    // is safe (never drives pruning); min/max is deliberately not exposed.
    private static string ColumnNdvSql(string schema, string table)
    {
        string objectLiteral = "N'" + (schema + "." + table).Replace("'", "''") + "'";
        return
            "SELECT c.name AS column_name, CAST(MAX(h.ndv) AS VARCHAR(32)) AS ndv " +
            "FROM sys.stats s " +
            "JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND sc.stats_column_id = 1 " +
            "JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id " +
            "CROSS APPLY (SELECT SUM(hist.distinct_range_rows) + COUNT_BIG(*) AS ndv " +
            "             FROM sys.dm_db_stats_histogram(s.object_id, s.stats_id) hist) h " +
            "WHERE s.object_id = OBJECT_ID(" + objectLiteral + ") AND h.ndv > 0 " +
            "GROUP BY c.name";
    }

    // Approximate row count (one VARCHAR cell) from partition stats — a cheap
    // metadata read (not COUNT(*)) used for the optimizer's cardinality estimate.
    // Views / tables with no partition rows yield 0.
    private static string RowCountSql(string schema, string table)
    {
        string objectLiteral = "N'" + (schema + "." + table).Replace("'", "''") + "'";
        return "SELECT CAST(COALESCE(SUM(p.row_count), 0) AS VARCHAR(32)) AS n " +
               "FROM sys.dm_db_partition_stats p " +
               "WHERE p.object_id = OBJECT_ID(" + objectLiteral + ") AND p.index_id IN (0, 1)";
    }

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        var qualified = $"{Quote(schemaName)}.{Quote(tableName)}";
        return ScanFromSource(qualified, System.Array.Empty<SqlParameter>(), specJson, filterValues,
                              touchKey: qualified);
    }

    /// <summary>
    /// Refuses a scan that would DEADLOCK AGAINST ITS OWN TRANSACTION instead of letting it hang forever.
    /// </summary>
    /// <remarks>
    /// <para>With MARS off a data scan cannot share the transaction's pinned connection, so it takes a
    /// POOLED one. Reading a table this same transaction has already written then waits on locks only that
    /// transaction can release — and it cannot, because it is blocked waiting for this very scan. It is a
    /// genuine self-deadlock across two connections, invisible to SQL Server's deadlock monitor (one session
    /// waits, the other merely sits idle), and <c>mssql_command_timeout</c> defaults to 0 = infinite, so the
    /// symptom is an unbounded hang with no error. Measured: docs/known-limitations.md 1.15.</para>
    /// <para><b>⚠ The check is ordered so the only expensive step runs LAST</b> — the RCSI probe is a round
    /// trip, and every condition before it is false in normal operation (MARS is on by default on box, and
    /// off only where reads are versioned anyway). In the shipped configuration this method returns at the
    /// first test.</para>
    /// <para><b>⚠ Why DATA writes and SCHEMA writes differ.</b> Row versioning versions ROWS, so with RCSI
    /// on a pooled read no longer blocks on uncommitted rows and there is nothing to refuse. It does NOT
    /// version METADATA: an uncommitted `ALTER` holds Sch-M, which blocks a reader's Sch-S at every
    /// isolation level. So a schema change is refused regardless of RCSI.</para>
    /// </remarks>
    /// <summary>
    /// Would a POOLED read of <paramref name="qualified"/> wait on locks only THIS transaction can release?
    /// Returns the reason, or null when the read is safe.
    /// </summary>
    /// <remarks>
    /// Factored out of <see cref="EnsureScanCannotSelfBlock"/> because two callers need the same question and
    /// give it opposite answers: the precheck REFUSES the scan, while <see cref="SinkRequiresDrainedScan"/>
    /// uses it to decide whether draining is worth its cost — the drain pins the scan onto the transaction's
    /// own connection, which is exactly the remedy for this hazard. Sharing one predicate is what keeps
    /// "when do we drain" and "when do we refuse" from drifting into disagreement, which would show up as a
    /// scan that is refused although the drain would have saved it, or drained although nothing needed it.
    /// </remarks>
    /// <summary>Has the CURRENT transaction written <paramref name="qualified"/> on its own pinned
    /// connection? <paramref name="schemaChanged"/> distinguishes a DDL touch from a data write.</summary>
    private bool TransactionHasWritten(string qualified, out bool schemaChanged)
    {
        schemaChanged = false;
        long txnId = AmbientTransaction.Current;
        if (txnId == 0 || !_txns.TryGetValue(txnId, out var state))
        {
            return false; // nothing pinned => no uncommitted work of ours
        }
        lock (state)
        {
            return state.Connection is not null && state.Touched.TryGetValue(qualified, out schemaChanged);
        }
    }

    private (string Why, bool SchemaChanged)? PooledScanSelfBlockReason(string qualified, bool schemaProbe)
    {
        if (TxnMars())
        {
            return null; // the scan shares the pinned connection — same session, owns the locks
        }
        if (!string.IsNullOrEmpty(ResolveReadIsolation()))
        {
            // The mssql_read_isolation opt-in routes this scan onto the transaction's OWN connection (and
            // drains it, since MARS is off here), so it runs in the session that holds the locks and cannot
            // wait on them. This hazard and the opt-in are alternative answers to the same problem; leaving
            // both armed would refuse a scan that now works.
            return null;
        }
        if (!TransactionHasWritten(qualified, out bool schemaChanged))
        {
            return null; // nothing pinned, or this transaction has not written THIS table
        }
        if (schemaProbe && !schemaChanged)
        {
            return null; // a `WHERE 1 = 0` probe reads no rows, so uncommitted ROWS cannot block it
        }
        if (!schemaChanged && VersionedReads())
        {
            return null; // RCSI (or a snapshot engine): the pooled read sees a version, never a lock
        }
        return (schemaChanged
            ? "this transaction has an uncommitted schema change on it (an uncommitted ALTER holds a "
              + "schema-modification lock, which blocks readers at EVERY isolation level — row versioning "
              + "does not version metadata)"
            : "this transaction has uncommitted writes to it and this database does not have "
              + "READ_COMMITTED_SNAPSHOT enabled", schemaChanged);
    }

    private void EnsureScanCannotSelfBlock(string qualified, bool materialize, bool snapshotRead,
                                           bool schemaProbe)
    {
        if (TxnMars())
        {
            return; // the scan shares the pinned connection — same session, owns the locks
        }
        if (materialize || snapshotRead)
        {
            return; // materialise => pinned + drained; snapshotRead => pooled at SNAPSHOT. Neither blocks.
        }
        if (PooledScanSelfBlockReason(qualified, schemaProbe) is not { } block)
        {
            return;
        }
        var (why, schemaChanged) = block;
        throw new System.InvalidOperationException(
            $"fabricator: cannot read {qualified} — {why}, and mssql_mars is off, so the scan would run on a "
            + "separate connection and wait forever for locks only this transaction can release. Remedies: "
            + "SET mssql_mars='auto' before ATTACH (the default on SQL Server, which lets the scan share the "
            + "transaction's connection)"
            // The read_isolation opt-in is a remedy here too, and for a DIFFERENT reason than the others: it
            // does not make the pooled read safe, it stops the read being pooled at all (the scan joins the
            // transaction's own connection, drained). Offered last because it changes the transaction's
            // isolation and holds a connection for its life — a bigger commitment than turning MARS back on.
            + "; SET mssql_read_isolation='snapshot' (puts the read on this transaction's own connection)"
            + (schemaChanged
                ? "; or COMMIT the schema change before reading the table."
                : "; ALTER DATABASE <db> SET READ_COMMITTED_SNAPSHOT ON; or COMMIT before reading."));
    }

    // is_read_committed_snapshot_on for THIS database, probed once per catalog. A warehouse engine
    // (Fabric/Synapse) reads at SNAPSHOT by construction and is reported versioned without a round trip.
    // ⚠ A FAILED probe answers TRUE (= "assume versioned, do not refuse"): this gate only ever turns a hang
    // into an error, so being unable to establish the fact must not manufacture a refusal.
    private bool VersionedReads()
    {
        if (_versionedReads is { } cached)
        {
            return cached;
        }
        bool result = true;
        try
        {
            if (_profile?.IsWarehouse != true)
            {
                using var conn = OpenConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT CAST(is_read_committed_snapshot_on AS INT) FROM sys.databases WHERE database_id = DB_ID()";
                result = System.Convert.ToInt32(cmd.ExecuteScalar() ?? 1) != 0;
            }
        }
        catch
        {
            result = true;
        }
        _versionedReads = result;
        return result;
    }

    private bool? _versionedReads;

    // Builds + runs a projected/filtered SELECT over an arbitrary FROM <source> — a table
    // (`[s].[t]`) or a parameterized TVF call (`[s].[f](@a0, ...)`). `sourceParams` are
    // bound for the source (TVF args, named @a* so they never collide with the filter's
    // @p*). Projection / TOP / ORDER BY / filter come from the scan spec; the filter is
    // best-effort — on any failure we fall back to no WHERE (DuckDB re-applies every
    // predicate, so correctness holds).
    internal IArrowArrayStream ScanFromSource(string source, IReadOnlyList<SqlParameter> sourceParams, string? specJson,
                                             IArrowArrayStream? filterValues, string? touchKey = null)
    {
        var spec = ScanSpec.Parse(specJson);
        // Only a CATALOG TABLE scan passes a touchKey — a TVF source has no table identity to compare.
        // ⚠ A SCHEMA PROBE IS CHECKED TOO, and excluding it was measured wrong: it reads no rows, so ROW
        // locks cannot block it, but `WHERE 1 = 0` still needs Sch-S and an uncommitted ALTER holds Sch-M.
        // The probe was the query that actually hung. It is exempted only from the data-write case.
        if (touchKey is not null)
        {
            bool drains = spec?.HasSink == true &&
                          SinkRequiresDrainedScan(spec.Sink!, touchKey, spec?.SchemaOnly == true);
            EnsureScanCannotSelfBlock(touchKey, drains && ResolveMaterialize(),
                                      drains && !ResolveMaterialize(),
                                      schemaProbe: spec?.SchemaOnly == true);
        }

        // Time travel (DuckDB AT clause). Only a catalog table scan carries it (AT is a base-table feature; a
        // TVF source never sets it). "version" (and any other unit) has no SQL Server equivalent -> a clean error.
        // TWO timestamp mechanisms by engine:
        //  - box / Azure SQL: FOR SYSTEM_TIME AS OF @__at — a per-table clause, requires a system-versioned
        //    (temporal) table.
        //  - Fabric Warehouse / Synapse: OPTION (FOR TIMESTAMP AS OF '<literal>') — a statement-level hint that
        //    works on ANY table (no temporal setup). UTC only; at most 3 fractional-second digits (Fabric errors
        //    on more). The literal is a fixed-format datetime (no injection) since OPTION takes no parameter.
        string optionClause = "";
        if (spec?.At is { } at)
        {
            if (!string.Equals(at.Unit, "timestamp", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"fabricator: AT ({at.Unit} => ...) time travel is not supported by the SQL Server provider; " +
                    "use AT (TIMESTAMP => ...) (a temporal table on box, or any table on Fabric Warehouse)");
            }
            var ts = DateTime.Parse(at.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Profile.IsWarehouse)
            {
                // Truncate to milliseconds (>= 4 fractional digits is rejected by Fabric, error 22440).
                var literal = ts.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                optionClause = $" OPTION (FOR TIMESTAMP AS OF '{literal}')";
            }
            else
            {
                var asOf = new SqlParameter("@__at", SqlDbType.DateTime2) { Value = ts };
                source = $"{source} FOR SYSTEM_TIME AS OF @__at";
                sourceParams = new List<SqlParameter>(sourceParams) { asOf };
            }
        }

        // Projection: SELECT only the requested columns (absent/empty => SELECT *).
        var columns = spec?.Columns is { Count: > 0 } cols
            ? string.Join(", ", cols.Select(Quote))
            : "*";

        // LIMIT pushdown: SELECT TOP (n). The host only sets this when there is no
        // pushed filter and no offset, so it is safe (DuckDB still re-applies LIMIT).
        var top = spec?.Top is long n and >= 0 ? $"TOP ({n}) " : "";

        // ORDER BY pushdown (paired with TOP for TopN). The host only sets this for
        // safe keys (non-string, NULL-order compatible, no filter); DuckDB re-sorts.
        var orderBy = spec?.OrderBy is { Count: > 0 } keys
            ? " ORDER BY " + string.Join(", ", keys.Select(k => $"{Quote(k.Col)}{(k.Desc ? " DESC" : " ASC")}"))
            : "";

        // DESCRIBE ONLY: the bind-time probe wants this scan's Arrow schema and no rows. `WHERE 1 = 0` is a
        // constant-false predicate, so the server returns the result-set METADATA without reading the table
        // — where the same probe without it starts a full scan that the bind then cancels. Placed ahead of
        // the filter branch because a schema request never carries one.
        //
        // ⚠ Routing is deliberately UNCHANGED (readYourWrites: false), so this stays an ordinary data read
        // on the same connection it used before. Marking it a metadata read would move it to the pinned
        // connection — arguably right, since it drains immediately — but that is a connection-routing change
        // with its own consequences and does not belong in a change about not reading the table.
        if (spec?.SchemaOnly == true)
        {
            filterValues?.Dispose();
            return ExecuteQuery($"SELECT {columns} FROM {source} WHERE 1 = 0{optionClause}",
                                sourceParams.Count > 0 ? sourceParams : null,
                                readYourWrites: false, materialize: false, snapshotRead: false);
        }

        // Filter pushdown: render spec.Filter into a parameterized WHERE. This is
        // best-effort — DuckDB re-applies every predicate above the scan — so if we
        // can't read a value or render a node, omit the WHERE and let DuckDB filter.
        if (spec?.Filter is not null)
        {
            try
            {
                var values = ReadFilterValues(filterValues);
                filterValues = null; // consumed + disposed by ReadFilterValues
                var builder = new FilterWhereBuilder(values);
                var where = builder.Build(spec.Filter);
                var allParams = new List<SqlParameter>(sourceParams);
                allParams.AddRange(builder.Parameters); // source @a* + filter @p* are disjoint
                bool drainF = spec.HasSink && SinkRequiresDrainedScan(spec.Sink!, touchKey, spec.SchemaOnly);
                return ExecuteQuery($"SELECT {top}{columns} FROM {source} WHERE {where}{orderBy}{optionClause}", allParams,
                                    readYourWrites: false, materialize: drainF && ResolveMaterialize(),
                                    snapshotRead: drainF && !ResolveMaterialize());
            }
            catch
            {
                // Fall through to an unfiltered scan; correctness preserved by DuckDB.
            }
        }

        filterValues?.Dispose();
        var marked = spec?.HasSink == true &&
                     SinkRequiresDrainedScan(spec.Sink!, touchKey, spec?.SchemaOnly == true);
        return ExecuteQuery($"SELECT {top}{columns} FROM {source}{orderBy}{optionClause}",
                            sourceParams.Count > 0 ? sourceParams : null,
                            readYourWrites: false, materialize: marked && ResolveMaterialize(),
                            snapshotRead: marked && !ResolveMaterialize());
    }

    // Reads the one-row filter value batch (column i == value i) into CLR values.
    private static List<object?> ReadFilterValues(IArrowArrayStream? stream)
    {
        if (stream is null)
        {
            return new List<object?>();
        }
        using (stream)
        {
            var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                return new List<object?>();
            }
            using (batch)
            {
                var values = new List<object?>(batch.ColumnCount);
                for (int i = 0; i < batch.ColumnCount; i++)
                {
                    values.Add(ArrowValueReader.ReadScalar(batch.Column(i), 0));
                }
                return values;
            }
        }
    }

    // Whether a created table should get an auto BIGINT IDENTITY surrogate key: the mssql_add_identity SET
    // setting wins when set (true/false), else the per-catalog add_identity ATTACH option.
    private bool ResolveAddIdentity() =>
        ProviderSettingsStore.Instance.GetBool(SqlServerBackend.ProviderName, "mssql_add_identity")
        ?? _addIdentityOnCreate;

    // ---- S3 external tables: write routing (docs/create-table-with-options.md slice C) -----------------
    // SQL Server external tables are READ-ONLY for SQL Server itself (no INSERT into an S3 external table;
    // CETAS exports parquet/CSV only and can never write Delta). When the INSERT target is a detected
    // DELTA/PARQUET external table over an s3:// data source, the write routes DIRECTLY to storage —
    // a Delta append via a transient delta-provider catalog / one new parquet file — and SQL Server keeps
    // serving the reads. See ExternalTableRouting (Bridge) for the endpoint-asymmetry + atomicity rules.

    private sealed record ExternalTableInfo(string? TableLocation, string? DataSourceLocation, string? FormatType,
                                            string? IdentityColumn)
    {
        public string? StorageUri => ExternalTableRouting.ComposeStorageUri(DataSourceLocation, TableLocation);
        public bool IsDelta => string.Equals((FormatType ?? "").Trim(), "DELTA", StringComparison.OrdinalIgnoreCase);
    }

    // Lazy per-table cache (positive AND negative — a normal table's INSERT must not pay a metadata query
    // per statement). Invalidated on DROP/CREATE through this catalog; an out-of-band conversion of an
    // already-probed table is picked up on re-ATTACH (documented staleness).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExternalTableInfo?> _externalInfo =
        new(StringComparer.OrdinalIgnoreCase);

    private static string ExternalKey(string schemaName, string tableName) => schemaName + "." + tableName;

    private ExternalTableInfo? DetectExternalTable(string schemaName, string tableName) =>
        _externalInfo.GetOrAdd(ExternalKey(schemaName, tableName), _ => ProbeExternalTable(schemaName, tableName));

    private ExternalTableInfo? ProbeExternalTable(string schemaName, string tableName)
    {
        // NEVER ISSUE THIS ON A WAREHOUSE ENGINE. Fabric/Synapse reject sys.external_file_formats outright
        // (error 15871 "'external_file_formats' is not supported"), and on Fabric a statement that ERRORS
        // inside an explicit transaction ABORTS THAT TRANSACTION. Because this probe's failure is swallowed
        // as "treated as not external", the transaction was poisoned SILENTLY and the next real statement
        // failed with a bewildering error instead — measured as dbt table models dying at their swap with
        // `15225: No item by the name of '[dbo].[<model>__dbt_tmp]' could be found`, and as `208: Invalid
        // object name` for a plain INSERT after a CREATE in the same transaction. A matched control (the same
        // sequence with a SUCCEEDING probe) completed fine, which is what pinned the cause.
        //
        // Warehouse engines have no PolyBase-style external tables at all, so skipping is also correct on the
        // merits — and saves a per-table round trip. See docs/warehouse-support.md §6.5.
        if (Profile.IsWarehouse)
        {
            return null;
        }
        // sys.external_tables → data source location + file format. LEFT JOIN: an external table without a
        // file format (RDBMS connectors) is detected but not routable. Profile-tolerant: an engine without
        // the catalog views (or without data virtualization) probes as "not external".
        string sql =
            "SELECT et.location, eds.location, eff.format_type " +
            "FROM sys.external_tables et " +
            "JOIN sys.external_data_sources eds ON eds.data_source_id = et.data_source_id " +
            "LEFT JOIN sys.external_file_formats eff ON eff.file_format_id = et.file_format_id " +
            $"WHERE et.object_id = OBJECT_ID({ObjectLiteral(schemaName, tableName)})";
        try
        {
            foreach (var row in ReadMetadataRows(sql, 3))
            {
                var info = new ExternalTableInfo(row[0], row[1], row[2], IdentityColumn: null);
                // slice D: a Delta IDENTITY column on the target enables identity-keyed UPDATE/DELETE —
                // it becomes the entry's rowid (a real, PolyBase-readable data column). One `_delta_log`
                // open, cached with the probe.
                if (info.IsDelta && info.StorageUri is { } uri)
                {
                    info = info with { IdentityColumn = ExternalTableRouting.FindDeltaIdentityColumn(uri) };
                }
                Log.LogInformation(
                    "external table {Schema}.{Table}: location={Loc} source={Src} format={Fmt} identity={Id}",
                    schemaName, tableName, row[0], row[1], row[2], info.IdentityColumn ?? "<none>");
                return info;
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug("external-table probe failed for {Schema}.{Table} (treated as not external): {Msg}",
                schemaName, tableName, ex.Message);
        }
        return null;
    }

    // Storage-side DML shares the INSERT's semantics: statement-atomic (its own Delta commit), so an
    // explicit DuckDB transaction can't wrap it.
    private void GuardExternalDml(string schemaName, string tableName, string verb)
    {
        if (_explicitTxns.ContainsKey(AmbientTransaction.Current))
        {
            throw new NotSupportedException(
                $"{verb} external table {schemaName}.{tableName} writes directly to its storage (its own "
                + "Delta commit) and cannot roll back with an explicit transaction — COMMIT first "
                + "(autocommit only).");
        }
    }

    private long ExternalTableInsert(ExternalTableInfo ext, string schemaName, string tableName,
                                     IArrowArrayStream data, bool checkConstraints, long txnId)
    {
        if (_explicitTxns.ContainsKey(txnId))
        {
            throw new NotSupportedException(
                $"INSERT into external table {schemaName}.{tableName} writes directly to its storage "
                + "(its own Delta commit / parquet file) and cannot roll back with an explicit "
                + "transaction — COMMIT first (autocommit only).");
        }
        var uri = ExternalTableRouting.ComposeStorageUri(ext.DataSourceLocation, ext.TableLocation)
            ?? throw new NotSupportedException(
                $"external table {schemaName}.{tableName} over data source location "
                + $"'{ext.DataSourceLocation}' is not routable — writable external tables need an "
                + "s3:// or adls:// data source. (abs:// names the same ADLS account through the blob "
                + "endpoint; deriving its DFS host would be a guess, so use adls:// to write. Reads work "
                + "on either.)");
        if (!ExternalTableRouting.CanRoute)
        {
            throw new NotSupportedException(
                "external-table INSERT needs the host query surface (host_query) — unavailable here.");
        }
        switch ((ext.FormatType ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "DELTA":
                return ExternalTableRouting.AppendDelta(uri, data, checkConstraints, txnId);
            case "PARQUET":
                return ExternalTableRouting.AppendParquet(uri, data);
            default:
                throw new NotSupportedException(
                    $"INSERT into a '{ext.FormatType}' external table is not supported (DELTA/PARQUET "
                    + "only) — SQL Server itself cannot INSERT into S3 external tables at all.");
        }
    }

    // CREATE TABLE [AS] ... WITH (location=..., table_type=..., data_source=... [, file_format=...]) —
    // the CETAS-analog (docs/create-table-with-options.md slice B): the DATA is written CLIENT-SIDE to the
    // S3 location (Delta — which SQL Server could never write — or parquet), then the EXTERNAL TABLE is
    // provisioned over it. `data_source` names a pre-provisioned EXTERNAL DATA SOURCE (the recommended,
    // no-secret-material posture; credential auto-provisioning from a DuckDB secret is deferred). Unknown
    // keys are rejected — a WITH option is never silently ignored.
    private sealed record ExternalCreateSpec(string Location, string TableType, string DataSource,
                                             string? FileFormat);

    /// <summary>Everything a SQL Server <c>CREATE TABLE … WITH (…)</c> can say. Two disjoint shapes share the
    /// key <c>table_type</c>, and its VALUE decides which:
    /// <list type="bullet">
    /// <item><c>DELTA</c> / <c>PARQUET</c> — the EXTERNAL-table CETAS analog. Needs <c>location</c> +
    /// <c>data_source</c>; carried in <see cref="External"/>.</item>
    /// <item><c>CLUSTERED COLUMNSTORE</c> / <c>CCI</c> / <c>HEAP</c> / <c>ROWSTORE</c> — the storage form of an
    /// ORDINARY table, i.e. the per-table form of <c>mssql_default_table_type</c>. Must NOT carry
    /// <c>location</c>.</item>
    /// </list>
    /// Sharing the key is deliberate rather than a collision: the two vocabularies do not overlap, so a value
    /// determines its own branch and <c>location</c> corroborates it. Keeping the per-table spelling equal to
    /// the SETTING it mirrors beats inventing a second key that means the same thing.</summary>
    private sealed record SqlServerWithOptions(
        ExternalCreateSpec? External = null,
        string? TableType = null,      // regular-table storage form (per-table mssql_default_table_type)
        long? VarcharLength = null,    // per-table mssql_default_varchar_length
        string? TextType = null);      // per-table mssql_ctas_text_type

    /// <summary>The EXTERNAL values of <c>table_type</c>. Anything else falls to the regular branch, so a typo
    /// surfaces as "not a valid storage form" rather than being silently treated as external.</summary>
    private static bool IsExternalTableType(string v) => v is "DELTA" or "PARQUET";

    /// <summary>The ORDINARY-table storage forms. The same vocabulary <c>mssql_default_table_type</c> accepts,
    /// so the per-table and per-session spellings cannot drift.</summary>
    private static bool IsRegularTableType(string v)
        => v is "CLUSTERED COLUMNSTORE" or "COLUMNSTORE" or "CCI" or "HEAP" or "ROWSTORE";

    private static SqlServerWithOptions? ParseWithOptions(string? optionsJson)
    {
        if (string.IsNullOrEmpty(optionsJson))
        {
            return null;
        }
        string? location = null, tableType = null, dataSource = null, fileFormat = null;
        string? textType = null;
        long? varcharLength = null;
        using (var doc = System.Text.Json.JsonDocument.Parse(optionsJson))
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                string value = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.Value.GetString() ?? string.Empty
                    : p.Value.ToString();
                switch (p.Name.ToLowerInvariant())
                {
                    case "location":
                        location = value;
                        break;
                    case "table_type":
                        tableType = value.Trim().ToUpperInvariant();
                        break;
                    case "format":
                        if (!value.Equals("parquet", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException(
                                $"WITH format: '{value}' is not supported (data files are parquet).");
                        }
                        break;
                    case "data_source":
                        dataSource = value;
                        break;
                    case "file_format":
                        fileFormat = value;
                        break;
                    case "varchar_length":
                        varcharLength = long.TryParse(value, System.Globalization.NumberStyles.Integer,
                                                      System.Globalization.CultureInfo.InvariantCulture,
                                                      out var vl) && vl > 0
                            ? vl
                            : throw new NotSupportedException(
                                $"WITH varchar_length: expected a positive integer, got '{value}'.");
                        break;
                    case "text_type":
                        textType = value.Trim();
                        break;
                    case "secret":
                        throw new NotSupportedException(
                            "WITH secret: credential auto-provisioning is not supported yet — pre-provision "
                            + "the DATABASE SCOPED CREDENTIAL + EXTERNAL DATA SOURCE once and pass "
                            + "data_source='<name>' instead.");
                    default:
                        throw new NotSupportedException(
                            $"unknown CREATE TABLE WITH option '{p.Name}' for the SQL Server provider "
                            + "(supported: table_type, varchar_length, text_type, and for an external "
                            + "table location, data_source, file_format).");
                }
            }
        }
        // WHICH SHAPE IS THIS? The table_type VALUE decides, and `location` corroborates. Note an ordinary
        // table needs no table_type at all — `WITH (varchar_length=200)` alone is valid — which is why
        // "location is present" also forces the external branch rather than table_type alone.
        bool external = location is not null || (tableType is not null && IsExternalTableType(tableType));
        if (external)
        {
            if (location is null)
            {
                throw new NotSupportedException(
                    $"WITH table_type='{tableType}' describes an S3 external table — "
                    + "location='s3://<sql-endpoint>/<bucket>/<path>' is required with it.");
            }
            if (tableType is null || !IsExternalTableType(tableType))
            {
                throw new NotSupportedException(
                    $"WITH table_type: '{tableType ?? "<missing>"}' is not supported alongside location "
                    + "(DELTA or PARQUET; ICEBERG has no writer). WITHOUT location, table_type instead "
                    + "selects an ordinary table's storage: CLUSTERED COLUMNSTORE or HEAP.");
            }
            if (string.IsNullOrWhiteSpace(dataSource))
            {
                throw new NotSupportedException(
                    "WITH data_source: required — the name of a pre-provisioned EXTERNAL DATA SOURCE over "
                    + "the location's endpoint (credentials never cross this path).");
            }
            // varchar_length / text_type describe how OUR CREATE TABLE spells a column type. An external
            // table's columns come from the storage files, so accepting them here would do nothing — and a
            // write option that silently does nothing is the failure this whole surface is built to avoid.
            if (varcharLength is not null || textType is not null)
            {
                throw new NotSupportedException(
                    "WITH varchar_length / text_type apply to an ordinary CREATE TABLE, not to an external "
                    + "table (its column types follow the storage files).");
            }
            return new SqlServerWithOptions(
                External: new ExternalCreateSpec(location, tableType!, dataSource!, fileFormat));
        }

        if (tableType is not null && !IsRegularTableType(tableType))
        {
            throw new NotSupportedException(
                $"WITH table_type: '{tableType}' is not supported (CLUSTERED COLUMNSTORE / CCI / HEAP / "
                + "ROWSTORE for an ordinary table; DELTA or PARQUET together with location for an "
                + "external one).");
        }
        if (dataSource is not null || fileFormat is not null)
        {
            throw new NotSupportedException(
                "WITH data_source / file_format describe an external table — pass location and "
                + "table_type='DELTA' (or 'PARQUET') with them.");
        }
        return new SqlServerWithOptions(TableType: tableType, VarcharLength: varcharLength,
                                        TextType: textType);
    }

    // Splits the SQL-visible location (s3://<host>/<bucket>/<path>) into the CLIENT-side URI
    // (s3://<bucket>/<path> — host discarded, the DuckDB s3 secret's ENDPOINT is authoritative) and the
    // external table's LOCATION (/<bucket>/<path>, relative to the data source).
    private static (string ClientUri, string RelLocation) ParseExternalLocation(string location)
    {
        const string prefix = "s3://";
        var trimmed = location.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"WITH location: '{location}' — only s3:// locations are supported.");
        }
        var rest = trimmed.Substring(prefix.Length).TrimEnd('/');
        int slash = rest.IndexOf('/');
        var rel = slash >= 0 ? rest.Substring(slash + 1).Trim('/') : string.Empty;
        if (slash < 0 || rel.IndexOf('/') < 0)
        {
            throw new NotSupportedException(
                $"WITH location: '{location}' must be s3://<endpoint>/<bucket>/<path> "
                + "(at least a bucket + a table folder).");
        }
        return (prefix + rel, "/" + rel);
    }

    // The external table's column declarations from the write schema — ONE source of truth, no drift.
    // Text columns get an explicit length instead of (MAX): external-table declarations reject MAX on some
    // engines, and the cap DIFFERS by type — VARCHAR(8000) but NVARCHAR(4000) (its max explicit length; a
    // larger value is error 2717). Declaration-only — the parquet/delta data holds full values.
    private string BuildExternalColumnList(Schema schema)
    {
        var parts = new List<string>(schema.FieldsList.Count);
        foreach (var f in schema.FieldsList)
        {
            string type = MapArrowToSqlType(f.DataType, Profile);
            if (type.EndsWith("(MAX)", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = type.Substring(0, type.Length - "(MAX)".Length);
                bool isN = prefix.TrimEnd().EndsWith("NVARCHAR", StringComparison.OrdinalIgnoreCase)
                           || prefix.TrimEnd().EndsWith("NCHAR", StringComparison.OrdinalIgnoreCase);
                type = prefix + (isN ? "(4000)" : "(8000)");
            }
            parts.Add(Quote(f.Name) + " " + type);
        }
        return string.Join(", ", parts);
    }

    // Provision the SQL side over data already written to storage: the (optional auto-created) file
    // format + the external table. Runs on the write connection (autocommit — external DDL rejects user
    // transactions anyway).
    private void ProvisionExternalTable(ExternalCreateSpec spec, string schemaName, string tableName,
                                        string relLocation, string columnList)
    {
        string ff = spec.FileFormat ?? (spec.TableType == "DELTA" ? "FabricatorDeltaFormat" : "FabricatorParquetFormat");
        if (spec.FileFormat is null)
        {
            ExecuteNonQuery(
                $"IF NOT EXISTS (SELECT 1 FROM sys.external_file_formats WHERE name = '{ff}') "
                + $"CREATE EXTERNAL FILE FORMAT {Quote(ff)} WITH (FORMAT_TYPE = {spec.TableType})");
        }
        ExecuteNonQuery(
            $"CREATE EXTERNAL TABLE {Quote(schemaName)}.{Quote(tableName)} ({columnList}) "
            + $"WITH (LOCATION = '{relLocation.Replace("'", "''")}', DATA_SOURCE = {Quote(spec.DataSource)}, "
            + $"FILE_FORMAT = {Quote(ff)})");
        _externalInfo.TryRemove(ExternalKey(schemaName, tableName), out _); // probe fresh (now external)
    }

    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists, string? primaryKey,
                            string? uniques, string? defaults, IReadOnlyList<string>? partitionColumns,
                            IReadOnlyList<string>? sortColumns, IReadOnlyList<string>? identityColumns,
                            string? optionsJson)
    {
        RecordTouch(schemaName, tableName, schemaChanged: true);
        // CETAS-analog, empty-CREATE shape (slice B): commit-0 Delta table + external table. The identity
        // marker rides through to the Delta create (declared plain BIGINT SQL-side — external tables can't
        // carry IDENTITY and don't need to), making the table slice-D DML-capable from birth.
        var withOpts = ParseWithOptions(optionsJson);
        if (withOpts is { External: { } extCreate })
        {
            if (extCreate.TableType != "DELTA")
            {
                throw new NotSupportedException(
                    "an empty CREATE with WITH (location=...) needs table_type='DELTA' (a parquet external "
                    + "table has no file to read until data exists — use CREATE TABLE ... AS instead).");
            }
            if (!string.IsNullOrEmpty(primaryKey) || !string.IsNullOrEmpty(uniques)
                || !string.IsNullOrEmpty(defaults))
            {
                throw new NotSupportedException(
                    "PRIMARY KEY / UNIQUE / DEFAULT cannot be combined with WITH (location=...) — "
                    + "external tables carry no constraints (the IDENTITY marker IS supported).");
            }
            if (_explicitTxns.ContainsKey(AmbientTransaction.Current))
            {
                throw new NotSupportedException(
                    "CREATE TABLE ... WITH (location=...) writes directly to storage and cannot roll back "
                    + "with an explicit transaction — COMMIT first (autocommit only).");
            }
            var (clientUri, relLocation) = ParseExternalLocation(extCreate.Location);
            ExternalTableRouting.CreateDeltaEmpty(clientUri, columns, partitionColumns, sortColumns,
                                                  identityColumns);
            ProvisionExternalTable(extCreate, schemaName, tableName, relLocation,
                                   BuildExternalColumnList(columns));
            return;
        }
        _externalInfo.TryRemove(ExternalKey(schemaName, tableName), out _); // re-probe after re-create
        // partitionColumns is a Delta/lakehouse concept; not applied to SQL Server DDL here (ignored).
        // sortColumns (native SORTED BY) becomes a Fabric Warehouse WITH (CLUSTER BY (cols)) layout — see BuildCreateTable.
        // identityColumns (DuckDB GENERATED-column marker) become IDENTITY columns — see BuildCreateTable.
        // Route through BeginWrite so this participates in the pinned transaction
        // when one is active — without it, CREATE OR REPLACE (DROP pinned + CREATE
        // fresh) would self-deadlock on the dropped table's schema lock.
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            string qualified = Quote(schemaName) + "." + Quote(tableName);
            var pk = ParseIndexGroup(primaryKey);
            var uniqueGroups = ParseIndexGroups(uniques);

            // Fabric Warehouse / Synapse reject PRIMARY KEY/UNIQUE declared INLINE in CREATE TABLE (error
            // 24584) and only support them as NONCLUSTERED NOT ENFORCED metadata hints added via ALTER TABLE.
            // So on a warehouse engine emit a plain CREATE then ALTER ADD CONSTRAINT per key. The hints are
            // NOT enforced (Fabric never checks uniqueness) but DO appear in sys.indexes, which seeds our
            // rowid discovery -> UPDATE/DELETE. Box SQL Server keeps the inline form (single statement).
            if (Profile.IsWarehouse && (pk.Count > 0 || uniqueGroups.Count > 0))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                if (ifNotExists)
                {
                    cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID({ObjectLiteral(schemaName, tableName)}, 'U') IS NULL THEN 0 ELSE 1 END";
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 1)
                    {
                        return; // table already exists
                    }
                }
                cmd.CommandText = BuildCreateTable(qualified, columns, Profile, null, null, defaults, sortColumns,
                                                   identityColumns, ResolveAddIdentity(), withOpts);
                Log.LogDebug("ddl create [txn={Txn} own={Own}] {Table}: {Sql}",
                            AmbientTransaction.Current, owns, qualified, Trunc(cmd.CommandText));
                cmd.ExecuteNonQuery();
                foreach (var alter in WarehouseConstraintAlters(qualified, tableName, columns, pk, uniqueGroups))
                {
                    cmd.CommandText = alter;
                    cmd.ExecuteNonQuery();
                }
                return;
            }

            string create = BuildCreateTable(qualified, columns, Profile, primaryKey, uniques, defaults, sortColumns,
                                              identityColumns, ResolveAddIdentity(), withOpts);
            using var cmd0 = connection.CreateCommand();
            cmd0.Transaction = transaction;
            cmd0.CommandText = ifNotExists
                ? $"IF OBJECT_ID({ObjectLiteral(schemaName, tableName)}, 'U') IS NULL {create}"
                : create;
            Log.LogDebug("ddl create [txn={Txn} own={Own}] {Table}: {Sql}",
                        AmbientTransaction.Current, owns, qualified, Trunc(cmd0.CommandText));
            cmd0.ExecuteNonQuery();
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    // The ALTER TABLE ADD CONSTRAINT statements for a warehouse table's PK/UNIQUE hints (NONCLUSTERED NOT
    // ENFORCED — the only form Fabric/Synapse accept, and only via ALTER, not inline). Names are derived
    // from the table so they're unique within the schema.
    private static IEnumerable<string> WarehouseConstraintAlters(string qualified, string tableName, Schema schema,
                                                                 List<int> pk, List<List<int>> uniqueGroups)
    {
        if (pk.Count > 0)
        {
            yield return $"ALTER TABLE {qualified} ADD CONSTRAINT {Quote("PK_" + tableName)} " +
                         $"PRIMARY KEY NONCLUSTERED ({ColumnList(schema, pk)}) NOT ENFORCED";
        }
        int u = 0;
        foreach (var group in uniqueGroups)
        {
            yield return $"ALTER TABLE {qualified} ADD CONSTRAINT {Quote($"UQ_{tableName}_{u++}")} " +
                         $"UNIQUE NONCLUSTERED ({ColumnList(schema, group)}) NOT ENFORCED";
        }
    }

    public void DropTable(string schemaName, string tableName, bool ifExists)
    {
        RecordTouch(schemaName, tableName, schemaChanged: true);
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        // A detected external table needs the EXTERNAL DDL form (SQL Server rejects plain DROP TABLE for
        // them). Metadata-only — the storage data stays (document; no purge). No IF EXISTS form exists for
        // DROP EXTERNAL TABLE pre-2025, so guard with OBJECT_ID.
        if (DetectExternalTable(schemaName, tableName) is not null)
        {
            ExecuteNonQuery(ifExists
                ? $"IF OBJECT_ID({ObjectLiteral(schemaName, tableName)}, 'U') IS NOT NULL DROP EXTERNAL TABLE {qualified}"
                : "DROP EXTERNAL TABLE " + qualified);
        }
        else
        {
            ExecuteNonQuery((ifExists ? "DROP TABLE IF EXISTS " : "DROP TABLE ") + qualified);
        }
        _externalInfo.TryRemove(ExternalKey(schemaName, tableName), out _);
    }

    public void CreateSchema(string schemaName, bool ifNotExists)
    {
        // CREATE SCHEMA must be the first statement in its batch, so run it via
        // EXEC; that also lets us guard it with IF SCHEMA_ID(...) IS NULL.
        string createExec = "EXEC('CREATE SCHEMA " + Quote(schemaName).Replace("'", "''") + "')";
        ExecuteNonQuery(ifNotExists
            ? $"IF SCHEMA_ID(N'{schemaName.Replace("'", "''")}') IS NULL {createExec}"
            : createExec);
    }

    public void DropSchema(string schemaName, bool ifExists)
    {
        ExecuteNonQuery((ifExists ? "DROP SCHEMA IF EXISTS " : "DROP SCHEMA ") + Quote(schemaName));
    }

    public void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2,
                           Field? column, int flags)
    {
        RecordTouch(schemaName, tableName, schemaChanged: true);
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        bool ifFlag = (flags & AlterKind.FlagIfExists) != 0;
        Log.LogDebug("ddl alter [txn={Txn}] {Table}: kind={Kind} arg1={A1} arg2={A2}",
                     AmbientTransaction.Current, qualified, alterKind, arg1, arg2);
        switch (alterKind)
        {
            case AlterKind.RenameTable:
                // sp_rename: @objname may be schema-qualified; @newname must NOT be.
                ExecuteNonQuery($"EXEC sp_rename N'{(Quote(schemaName) + "." + Quote(tableName)).Replace("'", "''")}', " +
                                $"N'{RequireArg(arg1, "new table name").Replace("'", "''")}'");
                break;
            case AlterKind.RenameColumn:
                ExecuteNonQuery(
                    $"EXEC sp_rename N'{(Quote(schemaName) + "." + Quote(tableName) + "." + Quote(RequireArg(arg1, "old column"))).Replace("'", "''")}', " +
                    $"N'{RequireArg(arg2, "new column").Replace("'", "''")}', 'COLUMN'");
                break;
            case AlterKind.AddColumn:
            {
                var field = RequireField(column, "added column");
                string colDef = Quote(RequireArg(arg1, "column name")) + " " + MapArrowToSqlType(field.DataType, Profile) +
                                (field.IsNullable ? " NULL" : " NOT NULL");
                string add = $"ALTER TABLE {qualified} ADD {colDef}";
                ExecuteNonQuery(ifFlag
                    ? $"IF COL_LENGTH({ObjectLiteral(schemaName, tableName)}, N'{RequireArg(arg1, "column name").Replace("'", "''")}') IS NULL " +
                      $"EXEC('{add.Replace("'", "''")}')"
                    : add);
                break;
            }
            case AlterKind.DropColumn:
                ExecuteNonQuery($"ALTER TABLE {qualified} DROP COLUMN {(ifFlag ? "IF EXISTS " : string.Empty)}" +
                                Quote(RequireArg(arg1, "column name")));
                break;
            case AlterKind.ColumnType:
            {
                // SQL Server defaults an ALTER COLUMN with no NULL/NOT NULL to NULLable,
                // so restate the column's current nullability explicitly.
                string col = RequireArg(arg1, "column name");
                bool nullable = ColumnIsNullable(schemaName, tableName, col);
                ExecuteNonQuery($"ALTER TABLE {qualified} ALTER COLUMN {Quote(col)} " +
                                MapArrowToSqlType(RequireField(column, "column type").DataType, Profile) +
                                (nullable ? " NULL" : " NOT NULL"));
                break;
            }
            case AlterKind.SetNotNull:
            case AlterKind.DropNotNull:
            {
                // SQL Server ALTER COLUMN must restate the type; reconstruct the
                // column's current type from the catalog and toggle nullability.
                string col = RequireArg(arg1, "column name");
                string type = ColumnTypeInfo(schemaName, tableName, col).fullType;
                ExecuteNonQuery($"ALTER TABLE {qualified} ALTER COLUMN {Quote(col)} {type} " +
                                (alterKind == AlterKind.SetNotNull ? "NOT NULL" : "NULL"));
                break;
            }
            case AlterKind.SetDefault:
            {
                // SQL Server defaults are named constraints: replace any existing one.
                string col = RequireArg(arg1, "column name");
                using var connection = OpenConnection();
                connection.Open();
                DropColumnDefault(connection, schemaName, tableName, col);
                string dataType = ColumnTypeInfo(schemaName, tableName, col).dataType;
                string literal = RenderDefaultBySqlType(dataType, DecodeDefaultArg(arg2));
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE {qualified} ADD DEFAULT ({literal}) FOR {Quote(col)}";
                cmd.ExecuteNonQuery();
                break;
            }
            case AlterKind.DropDefault:
            {
                string col = RequireArg(arg1, "column name");
                using var connection = OpenConnection();
                connection.Open();
                DropColumnDefault(connection, schemaName, tableName, col);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(alterKind), alterKind, "fabricator: unknown alter kind");
        }
    }

    // Reconstructs a column's current SQL type (with length/precision) from the
    // catalog, e.g. "nvarchar(max)", "decimal(10,2)", "datetime2(7)", "int".
    private (string fullType, string dataType) ColumnTypeInfo(string schemaName, string tableName, string columnName)
    {
        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION " +
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t AND COLUMN_NAME = @c";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException($"fabricator: column '{columnName}' not found on {schemaName}.{tableName}");
        }
        string dataType = reader.GetString(0);
        int? charLen = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1));
        int? numPrec = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2));
        int numScale = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
        int? dtPrec = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4));

        return (BuildSqlType(dataType, charLen, numPrec, numScale, dtPrec), dataType);
    }

    // Reconstructs a SQL Server type string (e.g. "varchar(50)", "decimal(10,2)") from
    // INFORMATION_SCHEMA size/precision columns. Shared by column + parameter type queries.
    private static string BuildSqlType(string dataType, int? charLen, int? numPrec, int numScale, int? dtPrec) =>
        dataType.ToLowerInvariant() switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
                $"{dataType}({(charLen == -1 ? "max" : charLen?.ToString())})",
            "decimal" or "numeric" => $"{dataType}({numPrec},{numScale})",
            "datetime2" or "time" or "datetimeoffset" => dtPrec is null ? dataType : $"{dataType}({dtPrec})",
            _ => dataType,
        };

    // A scalar function's parameters from INFORMATION_SCHEMA.PARAMETERS: input params
    // (ORDINAL_POSITION > 0) or, when wantReturn, the return value (ORDINAL_POSITION = 0).
    // Each carries a reconstructed SQL type; names are de-@'d (blank => positional fallback).
    internal List<(string name, string sqlType)> FunctionParameters(string schemaName, string functionName,
                                                                    bool wantReturn)
    {
        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT PARAMETER_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, " +
            "DATETIME_PRECISION FROM INFORMATION_SCHEMA.PARAMETERS " +
            "WHERE SPECIFIC_SCHEMA = @s AND SPECIFIC_NAME = @f AND ORDINAL_POSITION " +
            // Input params only: PARAMETER_MODE='IN'. Functions are always IN (no-op); for
            // a proc this excludes OUTPUT params (mode 'INOUT'), which are handled separately.
            (wantReturn ? "= 0" : "> 0 AND PARAMETER_MODE = 'IN' ORDER BY ORDINAL_POSITION");
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@f", functionName);
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = (reader.IsDBNull(0) ? "" : reader.GetString(0)).TrimStart('@');
            if (string.IsNullOrEmpty(name))
            {
                name = wantReturn ? "result" : $"arg{result.Count}";
            }
            int? charLen = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2));
            int? numPrec = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3));
            int numScale = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
            int? dtPrec = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5));
            result.Add((name, BuildSqlType(reader.GetString(1), charLen, numPrec, numScale, dtPrec)));
        }
        return result;
    }

    /// <summary>
    /// Suffix of the per-row form of a discovered routine. A PROVIDER convention, not a host one: the host no
    /// longer synthesises these, so stripping the suffix to find the underlying routine is this class's job.
    /// </summary>
    internal const string EachSuffix = "_each";

    private static string StripEach(string functionName) =>
        functionName.EndsWith(EachSuffix, StringComparison.OrdinalIgnoreCase)
            ? functionName.Substring(0, functionName.Length - EachSuffix.Length)
            : functionName;

    public Schema GetFunctionParamSchema(string schemaName, string functionName)
    {
        // A custom function of any kind answers from the set (which tags a table/sqlgen function's NAMED
        // parameters, so an optional argument can be written `fn(x, flag := true)`); otherwise it is a
        // discovered routine and its parameters come from INFORMATION_SCHEMA.
        if (SqlServerSessionTagFunction.Is(functionName))
        {
            return SessionTag.Parameters;
        }
        // The per-row form declares exactly one parameter: the INPUT TABLE. Its per-row argument values come
        // from the input's COLUMNS, not from constant args, so there is nothing else to declare — and
        // answering here keeps its signature coming from a declaration like every other function's, instead of
        // the host special-casing a name it no longer knows about.
        if (functionName.EndsWith(EachSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return new Schema(new[] { Params.TableInput("input") }, metadata: null);
        }
        var custom = Functions.ParamSchema(schemaName, functionName);
        if (custom is not null)
        {
            return custom;
        }
        using var s = RoutineParamSchemaQuery(schemaName, functionName);
        return s.Schema;
    }

    /// <summary>
    /// Generates the replacement SQL for a catalog-bound SQL-generating table function — the host parses it and
    /// substitutes it for the call (bind_replace). <paramref name="catalogName"/> is this catalog's DuckDB
    /// ATTACH alias, so a generator can emit references back into it; BIND-time only and possibly repeated, so
    /// the generator must be deterministic and side-effect-free. See docs/macros-and-sqlgen-functions.md §2.
    /// </summary>
    public string GenerateTableSql(string schemaName, string functionName, string catalogName, RecordBatch? args)
    {
        return Functions.GenerateTableSql(schemaName, functionName,
                                                  new SqlGenContext(catalogName, this), args)
               ?? throw new NotSupportedException(
                   $"fabricator: no SQL-generating table function '{schemaName}.{functionName}'");
    }

    // Zero-row Arrow stream of a routine's input parameters (typed-NULL SELECT reconstructed from
    // INFORMATION_SCHEMA.PARAMETERS). General over scalar/table/proc; shared by GetFunctionParamSchema and
    // SqlServerScalarFunction.Parameters.
    internal IArrowArrayStream RoutineParamSchemaQuery(string schemaName, string functionName)
    {
        var parms = FunctionParameters(schemaName, functionName, wantReturn: false);
        if (parms.Count == 0)
        {
            return new InMemoryArrayStream(new Schema(System.Array.Empty<Field>(), null),
                                           System.Array.Empty<RecordBatch>());
        }
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < parms.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append("CAST(NULL AS ").Append(parms[i].sqlType).Append(") AS ").Append(Quote(parms[i].name));
        }
        sb.Append(" WHERE 1 = 0");
        return ExecuteQuery(sb.ToString());
    }

    public Schema GetFunctionReturnSchema(string schemaName, string functionName)
    {
        // A pure custom scalar's field carries the CONSISTENT tag (constant folding) — see
        // ScalarFunctionMetadata; discovered SQL UDFs below never tag (remote bodies stay VOLATILE).
        var custom = Functions.ReturnSchema(schemaName, functionName);
        if (custom is not null)
        {
            return custom;
        }
        using var s = RoutineReturnSchemaQuery(schemaName, functionName);
        return s.Schema;
    }

    // Zero-row Arrow stream of a scalar function's single return field (typed-NULL SELECT). Throws if the
    // routine has no return (e.g. a TVF/proc). Shared by GetFunctionReturnSchema and SqlServerScalarFunction.Result.
    internal IArrowArrayStream RoutineReturnSchemaQuery(string schemaName, string functionName)
    {
        var ret = FunctionParameters(schemaName, functionName, wantReturn: true);
        if (ret.Count == 0)
        {
            throw new ArgumentException($"fabricator: '{schemaName}.{functionName}' is not a scalar function");
        }
        return ExecuteQuery($"SELECT CAST(NULL AS {ret[0].sqlType}) AS result WHERE 1 = 0");
    }

    // Resolves a scalar function to its IScalarFunction implementation: a provider-authored custom
    // function if registered, else a SqlServerScalarFunction wrapping the discovered SQL UDF. One uniform
    // dispatch for ExecuteScalar (created on demand — the wrapper just holds the catalog + name). Returns the
    // base IScalarFunction — execution needs only Invoke/Parameters/Result, not the catalog SchemaName.
    private IScalarFunction ResolveScalar(string schemaName, string functionName) =>
        Functions.TryScalar(schemaName, functionName, out var custom)
            ? custom
            : new SqlServerScalarFunction(this, schemaName, functionName);

    // Executes a scalar function over the input batches by dispatching to the resolved IScalarFunction
    // (a custom C# function, or a SqlServerScalarFunction wrapping the discovered SQL UDF). Each input batch's
    // results become one output batch; the result column is typed by the function's result (the C++ side
    // ingests by position). The SQL UDF's chunking under the ~2100-parameter cap lives in its Invoke.
    public IArrowArrayStream ExecuteScalar(string schemaName, string functionName, IArrowArrayStream args)
    {
        var fn = ResolveScalar(schemaName, functionName);
        using var input = args;
        var batches = new List<RecordBatch>();
        Schema? resultSchema = null;
        RecordBatch? inBatch;
        while ((inBatch = input.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
        {
            if (inBatch.Length == 0)
            {
                continue;
            }
            var resultArray = fn.Invoke(inBatch);
            resultSchema ??= new Schema(new[] { new Field("result", resultArray.Data.DataType, nullable: true) }, null);
            batches.Add(new RecordBatch(resultSchema, new[] { resultArray }, resultArray.Length));
        }
        // No non-empty input → a correctly-typed zero-row stream.
        resultSchema ??= new Schema(new[] { new Field("result", fn.Result.DataType, nullable: true) }, null);
        return new InMemoryArrayStream(resultSchema, batches);
    }

    // A table-valued function's output columns from INFORMATION_SCHEMA.ROUTINE_COLUMNS
    // (the result-set columns of inline + multi-statement TVFs), each with a
    // reconstructed SQL type. Empty => not a TVF (e.g. a stored procedure).
    internal List<(string name, string sqlType)> FunctionOutputColumns(string schemaName, string functionName)
    {
        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, " +
            "DATETIME_PRECISION FROM INFORMATION_SCHEMA.ROUTINE_COLUMNS " +
            "WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @f ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@f", functionName);
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(0) ? $"col{result.Count}" : reader.GetString(0);
            int? charLen = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2));
            int? numPrec = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3));
            int numScale = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
            int? dtPrec = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5));
            result.Add((name, BuildSqlType(reader.GetString(1), charLen, numPrec, numScale, dtPrec)));
        }
        return result;
    }

    // A stored procedure's first result-set columns via sp_describe_first_result_set (late-binding).
    // We use the sp (over `EXEC [s].[p] @a=@a, …` with the proc's input params declared NULL — sp_describe
    // analyzes statically, no execution) rather than the dm_exec_describe_first_result_set_for_object DMV
    // because Fabric Warehouse does NOT support that DMV (error 15871) but does support the sp; the sp works
    // on box SQL Server too, so one path serves both. system_type_name is the full SQL type, used directly.
    // Empty => no determinable result set (e.g. a proc that only does work). Only called for procs WITHOUT
    // OUTPUT params (those take the ProcOutputParams path), so an IN-params-only EXEC is correct.
    internal List<(string name, string sqlType)> ProcResultColumns(string schemaName, string functionName)
    {
        var inputs = FunctionParameters(schemaName, functionName, wantReturn: false);
        string exec = $"EXEC {Quote(schemaName)}.{Quote(functionName)}";
        if (inputs.Count > 0)
        {
            exec += " " + string.Join(", ", inputs.Select(p => $"@{p.name}=@{p.name}"));
        }
        string? paramDecls = inputs.Count > 0
            ? string.Join(", ", inputs.Select(p => $"@{p.name} {p.sqlType}"))
            : null;

        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "EXEC sys.sp_describe_first_result_set @tsql = @__tsql, @params = @__params, @browse_information_mode = 0";
        cmd.Parameters.AddWithValue("@__tsql", exec);
        cmd.Parameters.AddWithValue("@__params", (object?)paramDecls ?? DBNull.Value);
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        int iHidden = reader.GetOrdinal("is_hidden");
        int iName = reader.GetOrdinal("name");
        int iType = reader.GetOrdinal("system_type_name");
        while (reader.Read())
        {
            if (!reader.IsDBNull(iHidden) && Convert.ToBoolean(reader.GetValue(iHidden)))
            {
                continue; // skip hidden (browse-key) columns
            }
            var name = reader.IsDBNull(iName) ? $"col{result.Count}" : reader.GetString(iName);
            var sqlType = reader.IsDBNull(iType) ? "sql_variant" : reader.GetString(iType);
            result.Add((name, sqlType));
        }
        return result;
    }

    // A stored procedure's OUTPUT parameters (PARAMETER_MODE 'INOUT'), each with a
    // reconstructed SQL type, in ordinal order. De-@'d names. Empty => none.
    internal List<(string name, string sqlType)> ProcOutputParams(string schemaName, string functionName)
    {
        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT PARAMETER_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, " +
            "DATETIME_PRECISION FROM INFORMATION_SCHEMA.PARAMETERS " +
            "WHERE SPECIFIC_SCHEMA = @s AND SPECIFIC_NAME = @f AND PARAMETER_MODE = 'INOUT' ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@f", functionName);
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = (reader.IsDBNull(0) ? "" : reader.GetString(0)).TrimStart('@');
            if (string.IsNullOrEmpty(name))
            {
                name = $"out{result.Count}";
            }
            int? charLen = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2));
            int? numPrec = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3));
            int numScale = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
            int? dtPrec = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5));
            result.Add((name, BuildSqlType(reader.GetString(1), charLen, numPrec, numScale, dtPrec)));
        }
        return result;
    }

    public Schema GetFunctionOutputSchema(string schemaName, string functionName, RecordBatch? args = null)
    {
        // A custom table function's output schema may depend on the constant args (bound per call, then the
        // binding is discarded). `args` is null only for the in-out `_each` base-schema probe, which does not
        // apply to a pure-C# table function; a static function ignores it.
        if (SqlServerSessionTagFunction.Is(functionName))
        {
            return SqlServerSessionTagFunction.Columns;
        }
        var custom = Functions.OutputSchema(schemaName, functionName, args);
        if (custom is not null)
        {
            return custom;
        }
        // Custom in-out functions resolve their output schema through InOutBind (the exchange path), not here.
        // A discovered TVF (its result columns are in ROUTINE_COLUMNS) resolves its full output schema via
        // SqlServerTableValuedFunction; else a stored proc (OUTPUT params + return_value, else
        // sp_describe_first_result_set) via the SqlServerProcedure wrapper.
        if (FunctionOutputColumns(schemaName, functionName).Count > 0)
        {
            return new SqlServerTableValuedFunction(this, schemaName, functionName).OutputSchema;
        }
        using var procBinding = new SqlServerProcedure(this, schemaName, functionName).Bind(args!);
        return procBinding.OutputSchema;
    }

    // Phase 5 session model: bind a table-function call into an IBoundTable (the host then runs it via
    // table_execute and frees it via table_close). Classifies the function the same way
    // GetFunctionOutputSchema does — a custom (pure-C#) table function, else a discovered TVF (its result
    // columns are in ROUTINE_COLUMNS; pushdown), else a stored proc (no pushdown). The binding resolves the
    // output schema once and is reused across (prepared) re-executions.
    public IBoundTable TableBind(string schemaName, string functionName, RecordBatch? args)
    {
        // supportsPushdown = !is_proc (preserves the prior push_projection): a custom function maps its full
        // result by NAME (true); a discovered TVF pushes the projection into SQL (true); a stored proc is
        // projected positionally above the scan (false).
        if (SqlServerSessionTagFunction.Is(functionName))
        {
            return new BindingBoundTable(SessionTag.Bind(args!), supportsPushdown: true);
        }
        var custom = Functions.TableBind(schemaName, functionName, args);
        if (custom is not null)
        {
            return custom;
        }
        if (FunctionOutputColumns(schemaName, functionName).Count > 0)
        {
            return new TvfBoundTable(new SqlServerTableValuedFunction(this, schemaName, functionName), args!);
        }
        return new BindingBoundTable(new SqlServerProcedure(this, schemaName, functionName).Bind(args!), supportsPushdown: false);
    }

    // Phase 6 streaming-exchange bind for every `_each` form. A custom C# in-out (ICatalogInOutFunction —
    // directly or via the StaticInOutFunction base) binds itself; a discovered TVF `_each` CROSS APPLYs on a
    // read-only connection (SqlServerTvfEach); a stored-proc `_each` EXECs once per input row on DuckDB's
    // pinned write transaction (SqlServerProcEach). Proc vs TVF is classified the same way as elsewhere — a
    // TVF has result columns in ROUTINE_COLUMNS, a proc doesn't.
    public IArrowInOutBinding InOutBind(string schemaName, string functionName, RecordBatch? args, Schema inputSchema)
    {
        // A custom COLLECTOR (pipeline breaker) is tried before a streaming in-out inside the set: the host
        // registered it on the Sink+Source collector operator (kind='collector'), which feeds a NON-gated
        // buffered input stream, so its Collect reading all input before yielding (no sentinels) is safe.
        // Not a custom function ⇒ a discovered `_each`, classified as everywhere else.
        // Not a custom function => this provider's per-row form, `<routine>_each`. THIS CLASS strips the
        // suffix, because THIS CLASS chose it: the host used to resolve the alias and hand us the base name,
        // which is exactly the coupling that made a SQL-Server semantic the host's business.
        var routine = StripEach(functionName);
        var binding = Functions.InOutBind(schemaName, functionName, args, inputSchema)
                      ?? (FunctionOutputColumns(schemaName, routine).Count > 0
                          ? new SqlServerTvfEach(this, schemaName, routine, inputSchema)
                          : (IArrowInOutBinding)new SqlServerProcEach(this, schemaName, routine, inputSchema));
        // Resolve the SQL isolation for this in-out call and set it on the binding (if it honors isolation):
        // a SET mssql_isolation_level (pushed to the provider settings store) overrides this catalog's ATTACH
        // isolation_level default; pure-C# / proc bindings ignore it. Replaces the former C++
        // ResolveInOutIsolation + inout_exchange_open's isolation arg (docs/provider-extensibility.md §3).
        if (binding is IArrowInOutIsolation iso)
        {
            var setLevel = ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_isolation_level");
            iso.IsolationLevel = string.IsNullOrEmpty(setLevel) ? _isolationLevel : setLevel;
        }
        return binding;
    }

    // 4h custom aggregate (UDAF): open a session mapping DuckDB's per-group int64 state ids to live C#
    // accumulators (IAggregateFunction). Only provider-authored aggregates exist (SQL Server has no
    // DuckDB-aggregate to discover), so this requires a registered aggregate in the custom function set.
    public IAggregateSession AggOpen(string schemaName, string functionName) =>
        // The session impl lives in Fabricator.Bridge (shared with connection-free global aggregates).
        Functions.AggOpen(schemaName, functionName)
        ?? throw new ArgumentException(
            $"fabricator: '{schemaName}.{functionName}' is not a custom aggregate function");

    // (AggregateSession — the id->accumulator UDAF session — now lives in Fabricator.Bridge, shared by
    // catalog-bound aggregates (AggOpen above) and connection-free global aggregates.)

    // Effective SqlCommand.CommandTimeout (seconds; 0 = infinite) for scans / DML / bulk: a SET
    // mssql_command_timeout (provider settings store) wins if set, else this catalog's command_timeout ATTACH
    // option, else 0. Per-round-trip in ADO.NET (aborts a hung round-trip; a long-but-progressing scan is fine).
    // See docs/cancellation.md. Complements the InterruptScope token (Ctrl+C) — this is the non-interactive/hung
    // safety net; the token is user/query cancellation.
    internal int ResolveCommandTimeout()
    {
        var set = ProviderSettingsStore.Instance.GetLong(SqlServerBackend.ProviderName, "mssql_command_timeout");
        long v = set ?? _commandTimeout;
        return v < 0 ? 0 : (int)v;
    }

    // Maps an isolation_level string (ATTACH option / SET mssql_isolation_level) to ADO.NET. Empty =>
    // Unspecified (connection/provider default); a genuinely unknown name throws.
    private static IsolationLevel ParseIsolationLevel(string? level)
    {
        switch ((level ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty))
        {
            case "":
                return IsolationLevel.Unspecified;
            case "readuncommitted":
                return IsolationLevel.ReadUncommitted;
            case "readcommitted":
                return IsolationLevel.ReadCommitted;
            case "repeatableread":
                return IsolationLevel.RepeatableRead;
            case "serializable":
                return IsolationLevel.Serializable;
            case "snapshot":
                return IsolationLevel.Snapshot;
            default:
                throw new ArgumentException(
                    $"fabricator: unknown isolation level '{level}' (expected read uncommitted / read committed / " +
                    "repeatable read / serializable / snapshot)");
        }
    }

    // Begin the transaction scope for a table-in-out call: open a dedicated connection and begin one
    // transaction at the requested isolation so all per-chunk queries share one consistent view, committed
    // at the in-out's finish / rolled back on abort. Uses an ADO.NET SqlTransaction (MARS-compatible — a
    // raw BEGIN TRANSACTION can't span batches under the forced MARS). NOTE: independent of any surrounding
    // DuckDB transaction — a per-row stored proc that must participate in DuckDB's BEGIN/COMMIT (write
    // atomicity) will instead reuse the catalog's pinned transaction; that path is added with proc support.
    internal (SqlConnection connection, SqlTransaction transaction) BeginInOutScope(string isolation)
    {
        var connection = OpenConnection();
        connection.Open();
        var level = ParseIsolationLevel(isolation);
        var txn = level == IsolationLevel.Unspecified ? connection.BeginTransaction() : connection.BeginTransaction(level);
        return (connection, txn);
    }

    // Drops any DEFAULT constraint bound to a column (no-op if none).
    private static void DropColumnDefault(SqlConnection connection, string schemaName, string tableName, string column)
    {
        using var find = connection.CreateCommand();
        find.CommandText =
            "SELECT dc.name FROM sys.default_constraints dc " +
            "JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
            "WHERE dc.parent_object_id = OBJECT_ID(@obj) AND c.name = @col";
        find.Parameters.AddWithValue("@obj", schemaName + "." + tableName);
        find.Parameters.AddWithValue("@col", column);
        if (find.ExecuteScalar() is string name)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE {Quote(schemaName)}.{Quote(tableName)} DROP CONSTRAINT {Quote(name)}";
            drop.ExecuteNonQuery();
        }
    }

    // Decodes the SET DEFAULT arg2: "-" => DEFAULT NULL; "b"+base64 => literal text.
    private static string? DecodeDefaultArg(string? arg2)
    {
        if (arg2 == "-")
        {
            return null;
        }
        if (arg2 is not null && arg2.StartsWith('b'))
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(arg2.Substring(1)));
        }
        throw new ArgumentException("fabricator: SET DEFAULT requires a literal value");
    }

    // Renders a literal DEFAULT, quoting by the column's SQL data type.
    private static string RenderDefaultBySqlType(string dataType, string? value)
    {
        if (value is null)
        {
            return "NULL";
        }
        string dt = dataType.ToLowerInvariant();
        if (dt == "bit")
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("t", StringComparison.OrdinalIgnoreCase) || value == "1"
                ? "1"
                : "0";
        }
        switch (dt)
        {
            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
            case "decimal":
            case "numeric":
            case "real":
            case "float":
            case "money":
            case "smallmoney":
                return value; // numeric literal, no quoting
            default:
                return "N'" + value.Replace("'", "''") + "'";
        }
    }

    // Reads a column's current nullability from the catalog (defaults to nullable
    // if the column can't be found, matching SQL Server's ALTER COLUMN default).
    private bool ColumnIsNullable(string schemaName, string tableName, string columnName)
    {
        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
                          "WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t AND COLUMN_NAME = @c";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);
        var result = cmd.ExecuteScalar() as string;
        return !string.Equals(result, "NO", StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireArg(string? value, string what) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException($"fabricator: ALTER TABLE missing {what}") : value;

    private static Field RequireField(Field? field, string what) =>
        field ?? throw new ArgumentException($"fabricator: ALTER TABLE missing {what}");

    private static string ObjectLiteral(string schemaName, string tableName) =>
        "N'" + (schemaName + "." + tableName).Replace("'", "''") + "'";

    private static (string schema, string table) Require(string? schema, string? table)
    {
        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(table))
        {
            throw new ArgumentException("fabricator: metadata kind requires schema and table names");
        }
        return (schema, table);
    }

    // User schemas only (exclude system / fixed database roles).
    private const string SchemasSql =
        "SELECT s.name FROM sys.schemas s " +
        "WHERE s.name NOT IN ('sys','INFORMATION_SCHEMA','guest','db_owner','db_accessadmin'," +
        "'db_securityadmin','db_ddladmin','db_backupoperator','db_datareader','db_datawriter'," +
        "'db_denydatareader','db_denydatawriter') ORDER BY s.name";

    // Base tables and views across all schemas, with a uniform (schema, table, type) shape.
    private const string TablesSql =
        "SELECT s.name AS schema_name, t.name AS table_name, 'BASE TABLE' AS table_type " +
        "FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id " +
        "UNION ALL " +
        "SELECT s.name, v.name, 'VIEW' " +
        "FROM sys.views v JOIN sys.schemas s ON v.schema_id = s.schema_id " +
        "ORDER BY 1, 2";

    // Row-identity columns in key order: the primary key if present, else the
    // unique index with the fewest columns (tie-break by index_id).
    private static string RowIdSql(string schema, string table, ServerProfile profile)
    {
        string objectLiteral = "N'" + (schema + "." + table).Replace("'", "''") + "'";
        // The rowid = the PK, else the smallest unique index (fewest columns), by key ordinal.
        string keyIndexQuery =
               "SELECT c.name FROM sys.indexes i " +
               "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
               "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
               "WHERE i.object_id = OBJECT_ID(" + objectLiteral + ") AND i.index_id = (" +
               "  SELECT TOP 1 i2.index_id FROM sys.indexes i2 " +
               "  JOIN sys.index_columns ic2 ON ic2.object_id = i2.object_id AND ic2.index_id = i2.index_id " +
               "  WHERE i2.object_id = OBJECT_ID(" + objectLiteral + ") AND (i2.is_primary_key = 1 OR i2.is_unique = 1) " +
               "  GROUP BY i2.index_id, i2.is_primary_key " +
               "  ORDER BY i2.is_primary_key DESC, COUNT(*) ASC, i2.index_id ASC) " +
               "ORDER BY ic.key_ordinal";

        // An IDENTITY column is a fine single-column rowid too (engine-generated, effectively unique), so it lets
        // UPDATE/DELETE work on a table with no PK/UNIQUE at all. Precedence differs by engine:
        //  - Fabric Warehouse / Synapse: PK/UNIQUE are NON-ENFORCED hints (weak uniqueness), so the IDENTITY column
        //    is the BETTER rowid — prefer it, fall back to the PK/unique index.
        //  - Box / Azure SQL: enforced PKs are the intended key — prefer PK/unique, fall back to an IDENTITY column
        //    only when the table has no key constraint.
        string identityQuery = "SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(" + objectLiteral +
                               ") AND c.is_identity = 1";
        string identityExists = "EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(" + objectLiteral +
                                ") AND is_identity = 1)";
        string keyExists = "EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(" + objectLiteral +
                           ") AND (is_primary_key = 1 OR is_unique = 1))";
        return profile.IsWarehouse
            ? "IF " + identityExists + " " + identityQuery + " ELSE " + keyIndexQuery
            : "IF " + keyExists + " " + keyIndexQuery + " ELSE " + identityQuery;
    }

    // IDENTITY column names of an existing table (case-insensitive set).
    private static HashSet<string> GetIdentityColumns(SqlConnection connection, SqlTransaction? transaction,
                                                      string schemaName, string tableName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(@obj) AND c.is_identity = 1";
        cmd.Parameters.AddWithValue("@obj", schemaName + "." + tableName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private static string BuildCreateTable(string qualified, Schema schema, ServerProfile profile) =>
        BuildCreateTable(qualified, schema, profile, null, null, null, null, null, false);

    // IDENTITY column clause. Fabric Warehouse supports only bare BIGINT IDENTITY (no seed/increment); box /
    // Azure SQL take IDENTITY(1,1). Identity columns are always BIGINT here (Fabric requires it) and implicitly
    // NOT NULL, and can carry no DEFAULT.
    private static string IdentityClause(ServerProfile profile) =>
        profile.IsWarehouse ? " BIGINT IDENTITY" : " BIGINT IDENTITY(1,1)";

    private static string BuildCreateTable(string qualified, Schema schema, ServerProfile profile, string? primaryKey,
                                           string? uniques, string? defaults,
                                           IReadOnlyList<string>? clusterColumns = null,
                                           IReadOnlyList<string>? identityColumns = null, bool addIdentity = false,
                                           SqlServerWithOptions? with = null)
    {
        var defaultMap = ParseDefaults(defaults);
        // Columns marked IDENTITY (a DuckDB GENERATED-column marker), matched by name (case-insensitive).
        var identitySet = identityColumns is { Count: > 0 }
            ? new HashSet<string>(identityColumns, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingNames = new HashSet<string>(schema.FieldsList.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        // add_identity auto surrogate key: only when the option is on, no column was explicitly marked IDENTITY,
        // and no column already carries the target name (<table>_id). Skipped otherwise ("ignored if present").
        string autoIdentityName = TableNameFromQualified(qualified) + "_id";
        bool autoIdentity = addIdentity && identitySet.Count == 0 && !existingNames.Contains(autoIdentityName);

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(qualified).Append(" (");
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            var field = schema.FieldsList[i];
            if (i > 0)
            {
                sb.Append(", ");
            }
            if (identitySet.Contains(field.Name))
            {
                // A marked IDENTITY column: BIGINT IDENTITY (no NULL/DEFAULT — identity is NOT NULL, engine-assigned).
                sb.Append(Quote(field.Name)).Append(IdentityClause(profile));
                continue;
            }
            if (VariantMarker.IsVariantArrowField(field))
            {
                // A DuckDB VARIANT crosses the boundary as an ew.variant_transport-tagged blob (the Delta
                // provider's transport). Mapping it to VARBINARY would silently store opaque variant bytes —
                // reject instead (before the arrow extension existed this failed at export with a clean error).
                throw new NotSupportedException(
                    $"Column '{field.Name}' is a DuckDB VARIANT — not supported by the SQL Server provider "
                    + "(cast it to JSON/VARCHAR first, e.g. v::JSON).");
            }
            sb.Append(Quote(field.Name)).Append(' ')
              .Append(MapArrowToSqlType(field.DataType, profile, with?.TextType, with?.VarcharLength))
              .Append(field.IsNullable ? " NULL" : " NOT NULL");
            if (defaultMap.TryGetValue(i, out var defaultValue))
            {
                sb.Append(" DEFAULT ").Append(RenderDefault(field.DataType, defaultValue));
            }
        }
        // add_identity: append the auto surrogate-key column (engine-generated; absent from the source data, so
        // SqlBulkCopy's name-based mapping simply skips it on INSERT/CTAS).
        if (autoIdentity)
        {
            sb.Append(", ").Append(Quote(autoIdentityName)).Append(IdentityClause(profile));
        }
        // Clustered columnstore (mssql_default_table_type='clustered columnstore'). Box / Azure SQL only:
        // emit an inline INDEX … CLUSTERED COLUMNSTORE so the table is columnstore. Fabric/Synapse tables
        // are columnstore implicitly and reject an inline INDEX, so it is a no-op there (IsWarehouse gate).
        // When the clustered index IS the columnstore, PK/UNIQUE must be NONCLUSTERED.
        // The per-table WITH (table_type=...) outranks the session default. An explicit HEAP/ROWSTORE is
        // therefore a real OPT-OUT, not merely "unset" — which is the whole point of having it per table when
        // the session default is columnstore.
        bool cci = !profile.IsWarehouse &&
                   IsClusteredColumnstore(with?.TableType
                       ?? ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName,
                                                                   "mssql_default_table_type"));
        if (cci)
        {
            sb.Append(", INDEX ").Append(Quote(ColumnstoreIndexName(qualified))).Append(" CLUSTERED COLUMNSTORE");
        }
        string keyKind = cci ? " NONCLUSTERED" : "";
        var pk = ParseIndexGroup(primaryKey);
        if (pk.Count > 0)
        {
            sb.Append(", PRIMARY KEY").Append(keyKind).Append(" (").Append(ColumnList(schema, pk)).Append(')');
        }
        foreach (var group in ParseIndexGroups(uniques))
        {
            sb.Append(", UNIQUE").Append(keyKind).Append(" (").Append(ColumnList(schema, group)).Append(')');
        }
        sb.Append(')');

        // Fabric Warehouse / Synapse data-layout clustering: CREATE TABLE x (...) WITH (CLUSTER BY (c1, c2)).
        // Columns come from a native SORTED BY clause (clusterColumns), else the mssql_cluster_by setting.
        // Box SQL Server has no such syntax, so it is emitted ONLY on a warehouse profile (ignored otherwise).
        var cluster = ResolveClusterColumns(clusterColumns);
        if (profile.IsWarehouse && cluster.Count > 0)
        {
            sb.Append(" WITH (CLUSTER BY (");
            for (int i = 0; i < cluster.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(Quote(cluster[i]));
            }
            sb.Append("))");
        }
        return sb.ToString();
    }

    // The effective CLUSTER BY columns: a native SORTED BY clause wins; otherwise the mssql_cluster_by session
    // setting (comma-separated column names). Empty when neither is set.
    private static IReadOnlyList<string> ResolveClusterColumns(IReadOnlyList<string>? sortColumns)
    {
        if (sortColumns is { Count: > 0 })
        {
            return sortColumns;
        }
        var setting = ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_cluster_by");
        if (string.IsNullOrWhiteSpace(setting))
        {
            return System.Array.Empty<string>();
        }
        var list = new List<string>();
        foreach (var part in setting.Split(','))
        {
            var t = part.Trim().Trim('[', ']', '(', ')');
            if (t.Length > 0)
            {
                list.Add(t);
            }
        }
        return list;
    }

    // mssql_default_table_type values that select a clustered columnstore table (case/underscore tolerant).
    private static bool IsClusteredColumnstore(string? tableType)
    {
        var t = (tableType ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ");
        return t is "clustered columnstore" or "columnstore" or "cci";
    }

    // The table identifier out of a quoted "[schema].[table]" (last bracketed segment).
    private static string TableNameFromQualified(string qualified)
    {
        int close = qualified.LastIndexOf(']');
        int open = qualified.LastIndexOf('[', close < 0 ? qualified.Length - 1 : close);
        return (open >= 0 && close > open) ? qualified.Substring(open + 1, close - open - 1) : qualified;
    }

    // Clustered-columnstore index name, schema-qualified for database-wide uniqueness: cc_<schema>_<table>
    // (from the quoted "[schema].[table]"). Falls back to cc_<table> if the schema can't be parsed.
    private static string ColumnstoreIndexName(string qualified)
    {
        int e1 = qualified.IndexOf(']');
        int s1 = qualified.IndexOf('[');
        int s2 = e1 >= 0 ? qualified.IndexOf('[', e1 + 1) : -1;
        int e2 = qualified.LastIndexOf(']');
        if (s1 >= 0 && e1 > s1 && s2 > e1 && e2 > s2)
        {
            string schema = qualified.Substring(s1 + 1, e1 - s1 - 1);
            string table = qualified.Substring(s2 + 1, e2 - s2 - 1);
            return $"cc_{schema}_{table}";
        }
        return "cc_" + TableNameFromQualified(qualified);
    }

    private static string ColumnList(Schema schema, List<int> indices) =>
        string.Join(", ", indices.Select(i => Quote(schema.FieldsList[i].Name)));

    // "0,1" -> [0,1]; null/empty -> [].
    private static List<int> ParseIndexGroup(string? group) =>
        string.IsNullOrEmpty(group)
            ? new List<int>()
            : group.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    // "2;3,4" -> [[2],[3,4]]; null/empty -> [].
    private static List<List<int>> ParseIndexGroups(string? groups) =>
        string.IsNullOrEmpty(groups)
            ? new List<List<int>>()
            : groups.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ParseIndexGroup).ToList();

    // "0 aGVsbG8= 2 -" -> {0:"hello", 2:null}. Payload is base64(value-text), or
    // "-" for DEFAULT NULL. Key present => the column has a default.
    private static Dictionary<int, string?> ParseDefaults(string? spec)
    {
        var map = new Dictionary<int, string?>();
        if (string.IsNullOrEmpty(spec))
        {
            return map;
        }
        var tokens = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            int index = int.Parse(tokens[i]);
            string payload = tokens[i + 1];
            map[index] = payload == "-"
                ? null
                : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        return map;
    }

    // Renders a literal DEFAULT for a column, quoting by Arrow type.
    private static string RenderDefault(IArrowType type, string? value)
    {
        if (value is null)
        {
            return "NULL";
        }
        switch (type.TypeId)
        {
            case ArrowTypeId.Boolean:
                return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("t", StringComparison.OrdinalIgnoreCase) || value == "1"
                    ? "1"
                    : "0";
            case ArrowTypeId.Int8:
            case ArrowTypeId.UInt8:
            case ArrowTypeId.Int16:
            case ArrowTypeId.UInt16:
            case ArrowTypeId.Int32:
            case ArrowTypeId.UInt32:
            case ArrowTypeId.Int64:
            case ArrowTypeId.UInt64:
            case ArrowTypeId.Float:
            case ArrowTypeId.Double:
            case ArrowTypeId.Decimal128:
                return value; // numeric literal, no quoting
            default:
                return "N'" + value.Replace("'", "''") + "'";
        }
    }

    // Arrow type -> SQL Server column type, adapted to the connected engine's ServerProfile:
    //  - text: VARCHAR under a UTF-8 collation (holds full Unicode, lossless; the only option on Fabric),
    //    else NVARCHAR where it exists, else VARCHAR; an explicit mssql_ctas_text_type override still wins.
    //  - datetime2/time fractional scale: 7 (box) vs 6 (Fabric).
    //  - timestamptz: DATETIMEOFFSET where it exists, else UTC DATETIME2 (Fabric has no DATETIMEOFFSET).
    // Box SQL Server (HasNVarchar, HasDatetimeOffset, scale 7) reproduces the previous fixed mapping exactly.
    /// <param name="textTypeOverride">The statement's <c>WITH (text_type=…)</c>, if any. Null keeps the old
    /// behaviour exactly — read <c>mssql_ctas_text_type</c> from the session store.</param>
    /// <param name="varcharLengthOverride">The statement's <c>WITH (varchar_length=…)</c>, if any. Null keeps
    /// reading <c>mssql_default_varchar_length</c>.</param>
    /// <remarks>⚠ Both are OPTIONAL and default to the session settings ON PURPOSE: this mapper serves the
    /// ALTER paths too, and an ALTER carries no <c>WITH</c>. Only the CREATE path passes them.</remarks>
    private static string MapArrowToSqlType(IArrowType type, ServerProfile profile,
                                            string? textTypeOverride = null,
                                            long? varcharLengthOverride = null)
    {
        int scale = profile.MaxDateTime2Scale;
        switch (type.TypeId)
        {
            case ArrowTypeId.Boolean: return "BIT";
            case ArrowTypeId.Int8: return "SMALLINT"; // signed; SQL TINYINT is unsigned
            case ArrowTypeId.UInt8: return "TINYINT";
            case ArrowTypeId.Int16: return "SMALLINT";
            case ArrowTypeId.UInt16: return "INT";
            case ArrowTypeId.Int32: return "INT";
            case ArrowTypeId.UInt32: return "BIGINT";
            case ArrowTypeId.Int64: return "BIGINT";
            case ArrowTypeId.UInt64: return "DECIMAL(20,0)";
            case ArrowTypeId.Float: return "REAL";
            case ArrowTypeId.Double: return "FLOAT";
            case ArrowTypeId.Decimal128:
            {
                var d = (Decimal128Type)type;
                return $"DECIMAL({d.Precision},{d.Scale})";
            }
            case ArrowTypeId.Date32:
            case ArrowTypeId.Date64:
                return "DATE";
            case ArrowTypeId.Time32:
            case ArrowTypeId.Time64:
                return $"TIME({scale})";
            case ArrowTypeId.Timestamp:
                return ((TimestampType)type).Timezone != null && profile.HasDatetimeOffset
                    ? $"DATETIMEOFFSET({scale})"
                    : $"DATETIME2({scale})";
            case ArrowTypeId.Binary:
                return "VARBINARY(MAX)";
            case ArrowTypeId.String:
            default:
                // mssql_ctas_text_type: explicit whole-type override wins. Read from the provider settings
                // store (see docs/settings-architecture.md) — no per-method ABI param. Now also applies to
                // CTAS/COPY (the bulk-create path shares this mapper), not just explicit CREATE TABLE.
                // The per-statement WITH (text_type=...) outranks the session setting, on the same reasoning
                // as the Delta write tuning: the more specific layer wins, and a stray SET must not silently
                // override what a statement asked for by name.
                var textType = textTypeOverride
                    ?? ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_ctas_text_type");
                if (!string.IsNullOrWhiteSpace(textType))
                {
                    return textType!;
                }
                // VARCHAR-vs-NVARCHAR is driven by the COLLATION, not the edition (the §4 principled rule):
                // a UTF-8 collation makes VARCHAR hold full Unicode, so DuckDB's UTF-8 strings round-trip
                // losslessly as VARCHAR (and it's the only option on Fabric). A non-UTF-8 collation makes
                // VARCHAR a legacy single-byte codepage, so Unicode strings MUST go to NVARCHAR where it
                // exists; if NVARCHAR is also unavailable, VARCHAR is the only choice. This also correctly
                // handles a box SQL Server DB that opted into a UTF-8 collation (VARCHAR, not NVARCHAR).
                string baseType = profile.IsUtf8Collation ? "VARCHAR"
                                : profile.HasNVarchar ? "NVARCHAR"
                                : "VARCHAR";
                // mssql_default_varchar_length bounds every text column (unset => MAX). Read straight from
                // the provider settings store (see docs/settings-architecture.md) — no per-method ABI param.
                long? len = varcharLengthOverride
                    ?? ProviderSettingsStore.Instance.GetLong(SqlServerBackend.ProviderName, "mssql_default_varchar_length");
                return len is long n && n > 0 ? $"{baseType}({n})" : $"{baseType}(MAX)";
        }
    }

    public void Dispose()
    {
        // Roll back and release any still-open transactions (e.g. on DETACH mid-txn).
        // ConcurrentDictionary enumeration tolerates the concurrent removals EndTransaction does.
        foreach (var kvp in _txns)
        {
            try
            {
                EndTransaction(kvp.Key, commit: false);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
