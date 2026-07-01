using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ArrowNet.Bridge;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Data.SqlClient;

namespace ArrowNet.SqlServer;

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
            const string compat = "mssql_net compatibility setting";
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
                Str("mssql_ctas_text_type"),
                Str("mssql_isolation_level", "mssql_net: SQL transaction isolation level for table-in-out sessions"),
                Str("mssql_mars", "mssql_net: MARS mode — auto (default, per engine) | true | false"),
                Str("mssql_default_table_type",
                    "mssql_net: created-table storage — '' (rowstore, default) | 'clustered columnstore' " +
                    "(CCI, box/Azure SQL; Fabric/Synapse tables are columnstore already so it is a no-op there)"),
                Str("mssql_cluster_by",
                    "mssql_net: comma-separated columns for a Fabric Warehouse / Synapse WITH (CLUSTER BY (cols)) " +
                    "layout on created tables (fallback for a native SORTED BY clause; no-op on box SQL Server)"),
                Bool("mssql_add_identity",
                    "mssql_net: auto-add a BIGINT IDENTITY surrogate key (<table>_id) to created tables; overrides " +
                    "the per-catalog add_identity ATTACH option (SET false to skip for fact tables)"),
                Long("mssql_insert_batch_size", "mssql_net: max rows per INSERT statement", 2000L, 1),
                Long("mssql_insert_max_rows_per_statement", "mssql_net: hard cap on rows per statement", 2000L, 1),
                Long("mssql_insert_max_sql_bytes", "mssql_net: max SQL statement size in bytes", 8388608L, 1),
                Long("mssql_default_varchar_length",
                     "mssql_net: default VARCHAR/NVARCHAR length for created text columns (unset => MAX)", null, 1),
                Bool("mssql_insert_use_returning_output", "mssql_net: use OUTPUT INSERTED for RETURNING", true),
                Bool("mssql_exec_invalidate_cache",
                     "mssql_net: invalidate the catalog cache after DDL run via mssql_net_exec()", false),
            };
        }
    }

    // The DuckDB secret type + its CREATE SECRET fields, declared here so the host registers them generically
    // (the C++ core names no field). Names mirror the C++ mssql secret for cross-compat. password / access_token
    // are redacted. port is INTEGER, use_encrypt / catalog are BOOLEAN, the rest VARCHAR. Connection-string
    // assembly + validation live in BuildConnectionString. See docs/provider-extensibility.md §2.
    public string SecretType => "mssql_net";

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
    // (e.g. the arrownet_render template engine) ride along on the always-present default provider.
    public IEnumerable<IScalarFunction> GlobalScalarFunctions => CustomFunctions.GlobalScalar;
    public IEnumerable<IInOutFunction> GlobalInOutFunctions => CustomFunctions.GlobalInOut;
    public IEnumerable<ICollectorTableFunction> GlobalCollectorFunctions => CustomFunctions.GlobalCollector;
    public IEnumerable<ITableFunction> GlobalTableFunctions => CustomFunctions.GlobalTable;
    public IEnumerable<IAggregateFunction> GlobalAggregateFunctions => CustomFunctions.GlobalAggregate;

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
        // Dispatch by the DuckDB secret type the fields came from. Our own secret is a full connstr; a foreign
        // azure secret supplies only Entra auth, merged onto the ATTACH target. See docs/provider-extensibility.md §2.
        if (string.IsNullOrEmpty(secretType) || secretType.Equals("mssql_net", StringComparison.OrdinalIgnoreCase))
        {
            return BuildMssqlConnectionString(fields);
        }
        if (secretType.Equals("azure", StringComparison.OrdinalIgnoreCase))
        {
            return BuildAzureEntraConnectionString(fields, baseConnString);
        }
        throw new ArgumentException(
            $"mssql_net: a '{secretType}' secret can't be used by the mssql_net provider — use an mssql_net secret, " +
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
                "mssql_net: an azure secret supplies only auth — give the server/database in the ATTACH target, " +
                "e.g. ATTACH 'Server=...;Database=...' AS d (TYPE mssql_net, SECRET <azure_secret>)");
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
            throw new ArgumentException($"mssql_net: invalid ATTACH target for an azure secret: {ex.Message}");
        }
        if (string.IsNullOrEmpty(builder.DataSource))
        {
            throw new ArgumentException("mssql_net: the ATTACH target for an azure secret must include a Server");
        }

        var azProvider = F("provider").ToLowerInvariant();
        var clientId = F("client_id");
        var clientSecret = F("client_secret");
        if (azProvider == "credential_chain" ||
            (clientId.Length == 0 && clientSecret.Length == 0))
        {
            throw new ArgumentException(
                "mssql_net: this azure secret has no reusable SQL credential (credential_chain tokens are " +
                "storage-scoped and fetched lazily). Use authentication='Active Directory Default' on an " +
                "mssql_net secret/connstr instead — SqlClient runs the same credential chain, scoped for SQL.");
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
            builder["Encrypt"] = true; // TLS on by default, like the mssql_net path
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
            throw new ArgumentException("mssql_net secret: missing required field 'host'");
        }
        if (string.IsNullOrEmpty(Field("database")))
        {
            throw new ArgumentException("mssql_net secret: missing required field 'database'");
        }
        var portStr = Field("port");
        if (!string.IsNullOrEmpty(portStr))
        {
            if (!long.TryParse(portStr, out var p))
            {
                throw new ArgumentException($"mssql_net secret: port must be a valid integer. Got: {portStr}");
            }
            if (p < 1 || p > 65535)
            {
                throw new ArgumentException($"mssql_net secret: port must be between 1 and 65535. Got: {p}");
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
            _ => throw new ArgumentException($"mssql_net secret: unsupported authentication '{raw}'"),
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
    // Non-standard trailing segment used by SqlServerBackend.BuildConnectionString to carry an Azure
    // Entra access token (not a valid SqlClient connection-string keyword); it is stripped here and
    // applied via SqlConnection.AccessToken.
    internal const string AccessTokenKeyword = ";ArrowNetAccessToken=";

    // Provider-authored custom scalar functions, keyed "schema.name" (case-insensitive). Surfaced into
    // the catalog like discovered functions (see GetMetadata) but dispatched to C# (see ExecuteScalar /
    // GetFunctionParamSchema / GetFunctionReturnSchema) instead of generating SQL.
    private static readonly IReadOnlyDictionary<string, ICatalogScalarFunction> CustomScalar =
        CustomFunctions.Scalar.ToDictionary(f => $"{f.SchemaName}.{f.Name}", StringComparer.OrdinalIgnoreCase);

    // Provider-authored custom table functions, keyed "schema.name" (case-insensitive). Surfaced like
    // discovered TVFs but dispatched to C# (see TableBind / GetFunctionParamSchema / GetFunctionOutputSchema).
    private static readonly IReadOnlyDictionary<string, ICatalogTableFunction> CustomTable =
        CustomFunctions.Table.ToDictionary(f => $"{f.SchemaName}.{f.Name}", StringComparer.OrdinalIgnoreCase);

    // Provider-authored custom table-in-out functions (ICatalogInOutFunction), keyed "schema.name"
    // (case-insensitive). Surfaced as `kind='inout'` (see FunctionsMetadataSql) so the C++ catalog registers
    // them as a {TABLE}-param table function under the bare name, resolved by InOutBind on the streaming
    // exchange (Bind(args, inputSchema) -> the per-call binding). The output is the binding's full declared
    // schema (no input echo, unlike a discovered TVF's `_each`). Authors implement ICatalogInOutFunction (or its
    // fixed-schema convenience base StaticInOutFunction) and write DoExchange.
    private static readonly IReadOnlyDictionary<string, ICatalogInOutFunction> CustomInOut =
        CustomFunctions.InOut.ToDictionary(f => $"{f.SchemaName}.{f.Name}", StringComparer.OrdinalIgnoreCase);

    // Provider-authored custom COLLECTOR table-in-out functions (ICatalogCollectorTableFunction), keyed
    // "schema.name" (case-insensitive). Surfaced as `kind='collector'` (see FunctionsMetadataSql) so the C++
    // catalog registers them as a {TABLE}-param table function routed to the Sink+Source pipeline-breaker
    // operator (NOT the streaming exchange). Resolved by InOutBind (which wraps the IArrowCollectorBinding in a
    // CollectorInOutBinding so it flows through the shared inout_bind/inout_exchange_open marshaling). A
    // collector sees ALL input before emitting (whole-table semantics) — no single-chunk cap.
    private static readonly IReadOnlyDictionary<string, ICatalogCollectorTableFunction> CustomCollector =
        CustomFunctions.Collector.ToDictionary(f => $"{f.SchemaName}.{f.Name}", StringComparer.OrdinalIgnoreCase);

    // Provider-authored custom aggregate functions (UDAF), keyed "schema.name" (case-insensitive). Surfaced
    // as `kind='aggregate'` (see FunctionsMetadataSql) so the C++ catalog registers them as an
    // AggregateFunctionCatalogEntry; dispatched to C# (see AggOpen / GetFunctionParamSchema /
    // GetFunctionReturnSchema). The function object is a singleton (CreateState() mints the per-group state).
    private static readonly IReadOnlyDictionary<string, ICatalogAggregateFunction> CustomAgg =
        CustomFunctions.Aggregate.ToDictionary(f => $"{f.SchemaName}.{f.Name}", StringComparer.OrdinalIgnoreCase);

    private readonly string _baseConnectionString;   // user connstr (no MARS); basis for the finalized string
    private readonly string? _accessToken;
    // Server capability profile, detected lazily on the first connection (see docs/warehouse-support.md).
    // Probed on a NON-MARS connection (Synapse/Fabric reject a MARS connection outright), after which the
    // working connection string re-enables MARS only when the engine supports it.
    private volatile ServerProfile? _profile;
    private string? _connectionString;               // finalized in EnsureProfile (MARS per the resolved mode)
    private bool _marsEnabled;                        // resolved in EnsureProfile (mssql_mars ?? profile.SupportsMars)
    private readonly object _profileLock = new();

    // Provider-owned ATTACH options (parsed from open_catalog's options_json; docs/provider-extensibility.md §3).
    // schema_filter/table_filter (icase regex, substring match) are applied in GetMetadata so discovery returns
    // only matches; _isolationLevel is this catalog's default SQL isolation for table-in-out sessions (a SET
    // mssql_isolation_level overrides it, resolved in InOutBind).
    private readonly Regex? _schemaFilter;
    private readonly Regex? _tableFilter;
    private readonly string _isolationLevel = "";
    // ATTACH option `add_identity true`: created tables get an auto BIGINT IDENTITY surrogate key (<table>_id)
    // when none is otherwise specified. The mssql_add_identity SET setting overrides this per session (turn OFF
    // for fact tables that don't need a surrogate key). Resolved by ResolveAddIdentity().
    private readonly bool _addIdentityOnCreate;

    public SqlServerCatalog(string connectionString, string optionsJson)
    {
        // Empty connection string is rejected early with a clear message.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("mssql_net: empty connection string", nameof(connectionString));
        }
        // A `mssql://[user[:password]@]host[:port]/database[?encrypt=..&trustservercertificate=..]`
        // URI is translated to a SqlClient connection string (SqlClient can't parse it).
        if (connectionString.StartsWith("mssql://", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = ParseMssqlUri(connectionString);
        }

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
                    case "isolation_level": _isolationLevel = val; break;
                    case "add_identity":
                        _addIdentityOnCreate = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
                        break;
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
            throw new ArgumentException($"mssql_net: invalid {key} regex '{pattern}': {ex.Message}");
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

    // Detect the server profile on first use and finalize the working connection string. Probed on a
    // NON-MARS connection so Synapse/Fabric (which reject a MARS connection) can be classified; MARS is
    // then re-enabled in _connectionString only when the engine supports it. One-time per catalog.
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
            bool mars = ResolveMarsMode(profile);
            _connectionString =
                new SqlConnectionStringBuilder(_baseConnectionString) { MultipleActiveResultSets = mars }.ConnectionString;
            _marsEnabled = mars;
            _profile = profile; // volatile write last → publishes _connectionString + _marsEnabled to fast-path readers
        }
    }

    // mssql_mars: "auto"/empty => the engine default (profile.SupportsMars); "true"/"false" force it.
    // A genuinely unknown value throws. When MARS is off, reads never reuse the pinned write connection
    // (no read-your-writes) — they take a fresh pooled connection (see ExecuteQuery), which is what makes
    // a non-MARS warehouse (Fabric/Synapse) work: an open scan reader and DML can't coexist on one
    // non-MARS connection. See docs/transactions.md + docs/warehouse-support.md.
    private static bool ResolveMarsMode(ServerProfile profile)
    {
        var v = ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_mars");
        switch ((v ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "auto":
                return profile.SupportsMars;
            case "true":
            case "on":
            case "1":
            case "yes":
                return true;
            case "false":
            case "off":
            case "0":
            case "no":
                return false;
            default:
                throw new ArgumentException($"mssql_net: invalid mssql_mars '{v}' (expected auto | true | false)");
        }
    }

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
        return connection;
    }

    // Creates a connection on the finalized (profile-aware) connection string, detecting the server
    // profile on first use.
    private SqlConnection OpenConnection()
    {
        EnsureProfile();
        return OpenRaw(_connectionString!);
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
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TxnState> _txns = new();

    // begin is a no-op: the connection + provider transaction are pinned lazily on the first write
    // (BeginWrite), keyed by the ambient transaction id. A read-only transaction never creates state.
    public void BeginTransaction()
    {
    }

    public void CommitTransaction() => EndTransaction(AmbientTransaction.Current, commit: true);

    public void RollbackTransaction() => EndTransaction(AmbientTransaction.Current, commit: false);

    private void EndTransaction(long txnId, bool commit)
    {
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
            // Raw mssql_net_exec: join the active transaction's connection ONLY if one already exists (a
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
            var state = _txns.GetOrAdd(txnId, _ => new TxnState());
            // One thread at a time touches a given transaction (DuckDB serializes a transaction's
            // statements), so locking the single state is enough; distinct transactions use distinct states.
            lock (state)
            {
                if (state.Connection is null)
                {
                    var conn = OpenConnection();
                    conn.Open();
                    // Warehouse engines run the write transaction at SNAPSHOT (Fabric's only isolation
                    // level); box SQL Server keeps the connection/server default (Unspecified). Profile is
                    // already detected (OpenConnection ran EnsureProfile).
                    var level = ParseIsolationLevel(_profile!.DefaultWriteIsolation);
                    state.Connection = conn;
                    state.Transaction = level == IsolationLevel.Unspecified
                        ? conn.BeginTransaction()
                        : conn.BeginTransaction(level);
                }
                return (state.Connection, state.Transaction, false);
            }
        }
        var connection = OpenConnection();
        connection.Open();
        return (connection, null, true);
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

    public IArrowArrayStream ExecuteQuery(string sql, IReadOnlyList<SqlParameter>? parameters, bool readYourWrites)
    {
        // Inside a transaction that has a pinned connection (a write has happened), read on that connection
        // so the query sees uncommitted changes (read-your-writes). Borrowed: the stream must not dispose the
        // connection. For a data SCAN this is gated on MARS — an open scan reader and the transaction's DML
        // can only coexist on one connection under MARS, so with MARS off (Fabric/Synapse, or mssql_mars=false)
        // scans take a fresh pooled connection (documented warehouse trade-off — docs/transactions.md §5.1).
        // A METADATA read (readYourWrites) is exempt from the MARS gate: it fully drains immediately (no held
        // reader), and on MARS-off the pinned connection never carries a concurrent scan reader, so reusing it
        // is safe — and REQUIRED so a just-created table's metadata is visible (else the self-healing cache
        // would evict the table the CREATE just made; see ArrowNetSchemaEntry::CreateTable).
        SqlConnection? pinned = null;
        SqlTransaction? pinnedTransaction = null;
        long txnId = AmbientTransaction.Current;
        if (txnId != 0 && _txns.TryGetValue(txnId, out var state))
        {
            lock (state)
            {
                if (state.Connection is not null && (_marsEnabled || readYourWrites))
                {
                    pinned = state.Connection;
                    pinnedTransaction = state.Transaction;
                }
            }
        }
        if (pinned is not null)
        {
            var pinnedCommand = pinned.CreateCommand();
            pinnedCommand.CommandText = sql;
            pinnedCommand.CommandType = CommandType.Text;
            pinnedCommand.Transaction = pinnedTransaction;
            AddParameters(pinnedCommand, parameters);
            var pinnedReader = pinnedCommand.ExecuteReader();
            return new DbDataReaderArrowStream(pinned, pinnedCommand, pinnedReader, ownsConnection: false);
        }

        SqlConnection? connection = null;
        SqlCommand? command = null;
        try
        {
            connection = OpenConnection();
            connection.Open();
            command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            AddParameters(command, parameters);
            var reader = command.ExecuteReader();
            return new DbDataReaderArrowStream(connection, command, reader);
        }
        catch
        {
            command?.Dispose();
            connection?.Dispose();
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
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            command.Transaction = transaction;
            // ExecuteNonQuery returns -1 for statements that don't affect rows
            // (DDL, SET, ...); report 0 for those (matches the C++ mssql extension).
            return Math.Max(0, command.ExecuteNonQuery());
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
                           IReadOnlyList<string>? sortColumns)
    {
        // partitionColumns is a Delta/lakehouse concept; SQL Server table partitioning is out of scope here — ignored.
        // sortColumns (native SORTED BY) becomes a Fabric Warehouse WITH (CLUSTER BY (cols)) on the created table.
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
                                                      null, ResolveAddIdentity());
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

            using var reader = new ArrowDataReader(data);
            using var bulk = new SqlBulkCopy(connection, options, transaction)
                { DestinationTableName = qualified, BulkCopyTimeout = 0 };
            // Map by name (case-insensitive) so source/target column order need not match.
            foreach (var field in data.Schema.FieldsList)
            {
                bulk.ColumnMappings.Add(field.Name, field.Name);
            }
            bulk.WriteToServer(reader);
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
            total += cmd.ExecuteNonQuery();
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
            total += cmd.ExecuteNonQuery();
        }
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
        MetadataKind.Schemas => _schemaFilter is null ? ExecuteMetadataQuery(SchemasSql) : FilteredSchemas(),
        MetadataKind.Tables => _schemaFilter is null && _tableFilter is null
                                   ? ExecuteMetadataQuery(TablesSql)
                                   : FilteredTables(),
        // Zero-row result whose Arrow schema describes the table's columns; the
        // C++ host reads that schema to learn the DuckDB column types.
        MetadataKind.Columns => ExecuteMetadataQuery($"SELECT * FROM {Quote(Require(schema, table).schema)}." +
                                             $"{Quote(Require(schema, table).table)} WHERE 1 = 0"),
        MetadataKind.RowId => ExecuteMetadataQuery(RowIdSql(Require(schema, table).schema, Require(schema, table).table, Profile)),
        MetadataKind.RowCount => ExecuteMetadataQuery(RowCountSql(Require(schema, table).schema, Require(schema, table).table)),
        MetadataKind.ColumnNdv => ExecuteMetadataQuery(ColumnNdvSql(Require(schema, table).schema, Require(schema, table).table)),
        MetadataKind.Functions => ExecuteMetadataQuery(FunctionsMetadataSql()),
        // The detected capability profile as (property, value) rows — the mssql_server_info() diagnostic.
        // Built from the in-memory profile (not a re-query), so it surfaces the derived flags.
        MetadataKind.ServerInfo => ServerInfoStream(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "mssql_net: unknown metadata kind"),
    };

    // Builds a two-column (property, value) stream from the detected ServerProfile. Accessing Profile
    // detects it (via the non-MARS probe) on first use; for an attached catalog it is already cached.
    private IArrowArrayStream ServerInfoStream()
    {
        var rows = Profile.Properties();
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

    // schema_filter applied: only schema names matching the icase regex (substring) are returned.
    private IArrowArrayStream FilteredSchemas()
    {
        var schema = new Schema(new[] { new Field("name", StringType.Default, nullable: false) }, metadata: null);
        var names = new StringArray.Builder();
        int n = 0;
        foreach (var row in ReadMetadataRows(SchemasSql, 1))
        {
            if (row[0] is { } name && _schemaFilter!.IsMatch(name))
            {
                names.Append(name);
                n++;
            }
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

    // Discovered SQL Server routines + the provider's custom scalar/table functions, appended via
    // UNION ALL so the C++ catalog discovers + registers them uniformly (then dispatches custom ones to C#).
    private static string FunctionsMetadataSql()
    {
        if (CustomScalar.Count == 0 && CustomTable.Count == 0 && CustomInOut.Count == 0 && CustomAgg.Count == 0 &&
            CustomCollector.Count == 0)
        {
            return FunctionsSql;
        }
        static string Esc(string s) => s.Replace("'", "''");
        // FunctionsSql ends with ORDER BY; strip it so the UNION ALL is valid (the diagnostic /
        // discovery callers sort themselves, so dropping the ordering here is harmless).
        int orderIdx = FunctionsSql.LastIndexOf(" ORDER BY ", StringComparison.Ordinal);
        var sb = new StringBuilder(orderIdx >= 0 ? FunctionsSql[..orderIdx] : FunctionsSql);
        foreach (var f in CustomScalar.Values)
        {
            sb.Append(" UNION ALL SELECT '").Append(Esc(f.SchemaName)).Append("', '").Append(Esc(f.Name))
              .Append("', 'scalar', ").Append(f.Parameters.FieldsList.Count).Append(", '")
              .Append(Esc(f.Result.DataType.Name)).Append('\'');
        }
        foreach (var f in CustomTable.Values)
        {
            sb.Append(" UNION ALL SELECT '").Append(Esc(f.SchemaName)).Append("', '").Append(Esc(f.Name))
              .Append("', 'table', ").Append(f.Parameters.FieldsList.Count).Append(", ''");
        }
        foreach (var f in CustomInOut.Values)
        {
            sb.Append(" UNION ALL SELECT '").Append(Esc(f.SchemaName)).Append("', '").Append(Esc(f.Name))
              .Append("', 'inout', ").Append(f.InputSchema.FieldsList.Count).Append(", ''");
        }
        foreach (var f in CustomCollector.Values)
        {
            sb.Append(" UNION ALL SELECT '").Append(Esc(f.SchemaName)).Append("', '").Append(Esc(f.Name))
              .Append("', 'collector', ").Append(f.InputSchema.FieldsList.Count).Append(", ''");
        }
        foreach (var f in CustomAgg.Values)
        {
            // 'aggregate' (fast in-memory id-based) vs 'aggregate_spill' (state serialized into DuckDB's blob
            // so external GROUP BY can spill it) — the C++ side picks the callback set from this kind.
            sb.Append(" UNION ALL SELECT '").Append(Esc(f.SchemaName)).Append("', '").Append(Esc(f.Name))
              .Append(f.SupportsSpill ? "', 'aggregate_spill', " : "', 'aggregate', ")
              .Append(f.Parameters.FieldsList.Count).Append(", '")
              .Append(Esc(f.Result.DataType.Name)).Append('\'');
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
        "ORDER BY s.name, o.name";

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
        return ScanFromSource(qualified, System.Array.Empty<SqlParameter>(), specJson, filterValues);
    }

    // Builds + runs a projected/filtered SELECT over an arbitrary FROM <source> — a table
    // (`[s].[t]`) or a parameterized TVF call (`[s].[f](@a0, ...)`). `sourceParams` are
    // bound for the source (TVF args, named @a* so they never collide with the filter's
    // @p*). Projection / TOP / ORDER BY / filter come from the scan spec; the filter is
    // best-effort — on any failure we fall back to no WHERE (DuckDB re-applies every
    // predicate, so correctness holds).
    internal IArrowArrayStream ScanFromSource(string source, IReadOnlyList<SqlParameter> sourceParams, string? specJson,
                                             IArrowArrayStream? filterValues)
    {
        var spec = ScanSpec.Parse(specJson);

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
                    $"mssql_net: AT ({at.Unit} => ...) time travel is not supported by the SQL Server provider; " +
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
                return ExecuteQuery($"SELECT {top}{columns} FROM {source} WHERE {where}{orderBy}{optionClause}", allParams);
            }
            catch
            {
                // Fall through to an unfiltered scan; correctness preserved by DuckDB.
            }
        }

        filterValues?.Dispose();
        return ExecuteQuery($"SELECT {top}{columns} FROM {source}{orderBy}{optionClause}",
                            sourceParams.Count > 0 ? sourceParams : null);
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

    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists, string? primaryKey,
                            string? uniques, string? defaults, IReadOnlyList<string>? partitionColumns,
                            IReadOnlyList<string>? sortColumns, IReadOnlyList<string>? identityColumns)
    {
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
                                                   identityColumns, ResolveAddIdentity());
                cmd.ExecuteNonQuery();
                foreach (var alter in WarehouseConstraintAlters(qualified, tableName, columns, pk, uniqueGroups))
                {
                    cmd.CommandText = alter;
                    cmd.ExecuteNonQuery();
                }
                return;
            }

            string create = BuildCreateTable(qualified, columns, Profile, primaryKey, uniques, defaults, sortColumns,
                                              identityColumns, ResolveAddIdentity());
            using var cmd0 = connection.CreateCommand();
            cmd0.Transaction = transaction;
            cmd0.CommandText = ifNotExists
                ? $"IF OBJECT_ID({ObjectLiteral(schemaName, tableName)}, 'U') IS NULL {create}"
                : create;
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
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        ExecuteNonQuery((ifExists ? "DROP TABLE IF EXISTS " : "DROP TABLE ") + qualified);
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
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        bool ifFlag = (flags & AlterKind.FlagIfExists) != 0;
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
                throw new ArgumentOutOfRangeException(nameof(alterKind), alterKind, "mssql_net: unknown alter kind");
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
            throw new ArgumentException($"mssql_net: column '{columnName}' not found on {schemaName}.{tableName}");
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

    public Schema GetFunctionParamSchema(string schemaName, string functionName)
    {
        var key = $"{schemaName}.{functionName}";
        if (CustomScalar.TryGetValue(key, out var customScalar))
        {
            return customScalar.Parameters;
        }
        if (CustomTable.TryGetValue(key, out var customTable))
        {
            return customTable.Parameters;
        }
        if (CustomAgg.TryGetValue(key, out var customAgg))
        {
            return customAgg.Parameters;
        }
        using var s = RoutineParamSchemaQuery(schemaName, functionName);
        return s.Schema;
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
        if (CustomScalar.TryGetValue($"{schemaName}.{functionName}", out var custom))
        {
            return new Schema(new[] { custom.Result }, null);
        }
        if (CustomAgg.TryGetValue($"{schemaName}.{functionName}", out var customAgg))
        {
            return new Schema(new[] { customAgg.Result }, null);
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
            throw new ArgumentException($"mssql_net: '{schemaName}.{functionName}' is not a scalar function");
        }
        return ExecuteQuery($"SELECT CAST(NULL AS {ret[0].sqlType}) AS result WHERE 1 = 0");
    }

    // Resolves a scalar function to its IScalarFunction implementation: a provider-authored custom
    // function if registered, else a SqlServerScalarFunction wrapping the discovered SQL UDF. One uniform
    // dispatch for ExecuteScalar (created on demand — the wrapper just holds the catalog + name). Returns the
    // base IScalarFunction — execution needs only Invoke/Parameters/Result, not the catalog SchemaName.
    private IScalarFunction ResolveScalar(string schemaName, string functionName) =>
        CustomScalar.TryGetValue($"{schemaName}.{functionName}", out var custom)
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
        if (CustomTable.TryGetValue($"{schemaName}.{functionName}", out var customTable))
        {
            // The output schema may depend on the constant args (bound per call). `args` is null only for the
            // in-out `_each` base-schema probe (which doesn't apply to a pure-C# table function); a static
            // function ignores it.
            using var binding = customTable.Bind(args!);
            return binding.OutputSchema;
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
        if (CustomTable.TryGetValue($"{schemaName}.{functionName}", out var custom))
        {
            return new BindingBoundTable(custom.Bind(args!), supportsPushdown: true);
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
        IArrowInOutBinding binding;
        if (CustomCollector.TryGetValue($"{schemaName}.{functionName}", out var collector))
        {
            // A custom collector (pipeline breaker): wrap its IArrowCollectorBinding as an IArrowInOutBinding so
            // it flows through the shared exchange marshaling. The C++ side registered it on the Sink+Source
            // collector operator (kind='collector'), which feeds a NON-gated buffered input stream — so Collect
            // reading all input before yielding (no sentinels) is safe.
            binding = new CollectorInOutBinding(collector.Bind(args, inputSchema));
        }
        else if (CustomInOut.TryGetValue($"{schemaName}.{functionName}", out var custom))
        {
            binding = custom.Bind(args, inputSchema);
        }
        else if (FunctionOutputColumns(schemaName, functionName).Count > 0)
        {
            binding = new SqlServerTvfEach(this, schemaName, functionName, inputSchema);
        }
        else
        {
            binding = new SqlServerProcEach(this, schemaName, functionName, inputSchema);
        }
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
    // DuckDB-aggregate to discover), so this requires a registered CustomAgg entry.
    public IAggregateSession AggOpen(string schemaName, string functionName)
    {
        if (CustomAgg.TryGetValue($"{schemaName}.{functionName}", out var fn))
        {
            return new AggregateSession(fn); // the session impl lives in ArrowNet.Bridge (shared with globals)
        }
        throw new ArgumentException($"mssql_net: '{schemaName}.{functionName}' is not a custom aggregate function");
    }

    // (AggregateSession — the id->accumulator UDAF session — now lives in ArrowNet.Bridge, shared by
    // catalog-bound aggregates (AggOpen above) and connection-free global aggregates.)

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
                    $"mssql_net: unknown isolation level '{level}' (expected read uncommitted / read committed / " +
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
        throw new ArgumentException("mssql_net: SET DEFAULT requires a literal value");
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
        string.IsNullOrEmpty(value) ? throw new ArgumentException($"mssql_net: ALTER TABLE missing {what}") : value;

    private static Field RequireField(Field? field, string what) =>
        field ?? throw new ArgumentException($"mssql_net: ALTER TABLE missing {what}");

    private static string ObjectLiteral(string schemaName, string tableName) =>
        "N'" + (schemaName + "." + tableName).Replace("'", "''") + "'";

    private static (string schema, string table) Require(string? schema, string? table)
    {
        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(table))
        {
            throw new ArgumentException("mssql_net: metadata kind requires schema and table names");
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
                                           IReadOnlyList<string>? identityColumns = null, bool addIdentity = false)
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
            sb.Append(Quote(field.Name)).Append(' ').Append(MapArrowToSqlType(field.DataType, profile))
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
        bool cci = !profile.IsWarehouse &&
                   IsClusteredColumnstore(ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName,
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
    private static string MapArrowToSqlType(IArrowType type, ServerProfile profile)
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
                var textType = ProviderSettingsStore.Instance.GetString(SqlServerBackend.ProviderName, "mssql_ctas_text_type");
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
                long? len = ProviderSettingsStore.Instance.GetLong(SqlServerBackend.ProviderName, "mssql_default_varchar_length");
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
