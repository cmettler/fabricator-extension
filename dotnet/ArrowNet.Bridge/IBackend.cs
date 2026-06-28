using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// A data backend behind the bridge (SQL Server, later Analysis Services/DAX).
/// The bridge resolves the active backend, opens catalogs, and turns the
/// backend's results into Arrow streams for the C++ host. Phase 0 ships only
/// <see cref="StubBackend"/>; Phase 1 adds the SqlClient-based implementation in
/// the ArrowNet.SqlServer assembly.
/// </summary>
public interface IBackend
{
    /// <summary>
    /// Canonical provider name this backend registers under (case-insensitive), e.g.
    /// <c>"sqlserver"</c>. One binary can host several providers; <see cref="BackendRegistry"/>
    /// keys backends by this name (and <see cref="Aliases"/>) so a connection can be routed to the
    /// right provider.
    /// </summary>
    string Name { get; }

    /// <summary>Additional names this backend also answers to (e.g. <c>"mssql"</c>). Empty by default.</summary>
    IEnumerable<string> Aliases => System.Array.Empty<string>();

    /// <summary>
    /// The settings this provider declares (default none). The host registers each as a DuckDB extension
    /// option at load (so <c>SET</c> / <c>duckdb_settings()</c> work) and pushes value changes into
    /// <see cref="ProviderSettingsStore"/>, which the provider reads in C# — keeping setting names out of the
    /// provider-agnostic C++ core and off the per-method ABI. See docs/settings-architecture.md.
    /// </summary>
    IEnumerable<ProviderSetting> Settings => System.Array.Empty<ProviderSetting>();

    /// <summary>
    /// The DuckDB secret type this provider registers (used in <c>CREATE SECRET (TYPE …)</c>), e.g.
    /// <c>"mssql_net"</c>. Empty (default) =&gt; the provider has no secret type. Declared here so the host
    /// registers the secret type + its <see cref="SecretFields"/> generically — the C++ core names no secret
    /// type or field. See docs/provider-extensibility.md §2.
    /// </summary>
    string SecretType => string.Empty;

    /// <summary>
    /// The fields of <see cref="SecretType"/> (the <c>CREATE SECRET</c> named parameters). Empty when the
    /// provider has no secret type. The host registers these generically (via <c>list_secret_fields</c>) and
    /// stores supplied values; this provider reads them in <see cref="BuildConnectionString"/>.
    /// </summary>
    IEnumerable<SecretField> SecretFields => System.Array.Empty<SecretField>();

    /// <summary>
    /// Connection-free GLOBAL scalar functions the provider contributes — registered at extension load as bare
    /// <c>fn(...)</c> (no ATTACH), unioned across providers by <see cref="GlobalFunctions"/>. Empty by default;
    /// a provider opts in (e.g. a template engine). See docs/global-functions.md.
    /// </summary>
    IEnumerable<IScalarFunction> GlobalScalarFunctions => System.Array.Empty<IScalarFunction>();

    /// <summary>Connection-free GLOBAL table-in-out functions (streaming exchange), registered at load as bare
    /// <c>fn(&lt;input&gt;)</c>. Empty by default. See docs/global-functions.md.</summary>
    IEnumerable<IInOutFunction> GlobalInOutFunctions => System.Array.Empty<IInOutFunction>();

    /// <summary>Connection-free GLOBAL collector (pipeline-breaker) functions, registered at load as bare
    /// <c>fn(&lt;input&gt;)</c>. Empty by default. See docs/global-functions.md.</summary>
    IEnumerable<ICollectorTableFunction> GlobalCollectorFunctions => System.Array.Empty<ICollectorTableFunction>();

    /// <summary>Connection-free GLOBAL table functions, registered at load as a bare <c>fn(args)</c> (output
    /// schema resolved per-call from the args via the v29 table session). Empty by default. See
    /// docs/global-functions.md.</summary>
    IEnumerable<ITableFunction> GlobalTableFunctions => System.Array.Empty<ITableFunction>();

    /// <summary>Connection-free GLOBAL aggregate functions (UDAF), registered at load as a bare <c>fn(args)</c>
    /// usable in <c>GROUP BY</c> / <c>OVER</c> / parallel. Empty by default. See docs/global-functions.md.</summary>
    IEnumerable<IAggregateFunction> GlobalAggregateFunctions => System.Array.Empty<IAggregateFunction>();

    /// <summary>
    /// Builds a provider connection string from a secret's fields (the host reads the DuckDB secret and
    /// passes its key/values here). Keeps all provider connection-string / auth formatting in the backend —
    /// the C++ side has no knowledge of the provider's connstr dialect. The result is passed to
    /// <see cref="OpenCatalog"/>.
    /// </summary>
    /// <param name="secretType">
    /// The DuckDB secret type the fields came from (e.g. <c>"mssql_net"</c> = this provider's own secret;
    /// <c>"azure"</c> = a foreign secret reused for auth). Lets the backend interpret the fields per type —
    /// e.g. map an azure service-principal/managed-identity secret to Entra auth. See
    /// docs/provider-extensibility.md §2.
    /// </param>
    /// <param name="fields">The secret's key/values (case-insensitive lookups recommended).</param>
    /// <param name="baseConnString">
    /// The ATTACH connection target (e.g. <c>Server=…;Database=…</c> or a <c>mssql://</c> URI), empty when
    /// none. Used when a foreign secret carries only AUTH (e.g. azure) and the server/database must come from
    /// the ATTACH target; ignored for this provider's own full secret.
    /// </param>
    string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields, string baseConnString);

    /// <summary>
    /// Open a catalog/connection for the given connection string. <paramref name="optionsJson"/> is the
    /// provider-owned ATTACH options as a flat JSON object of strings (e.g.
    /// <c>{"schema_filter":"…","table_filter":"…","isolation_level":"…"}</c>; empty/null => none). The C++
    /// core forwards every ATTACH option except the two it handles itself (PROVIDER / SECRET), so the
    /// provider parses the keys it knows. See docs/provider-extensibility.md §3.
    /// </summary>
    IBackendCatalog OpenCatalog(string connectionString, string optionsJson);
}

/// <summary>
/// A bound table-function call (Phase 5 session model): resolves its output schema once and runs the scan
/// possibly many times (once per execution). For a discovered SQL Server TVF the scan pushes projection +
/// filter into the SELECT (<see cref="SupportsPushdown"/> = true); a stored proc / custom function returns
/// its full result and DuckDB projects + filters above the scan. Disposed via the host's table_close.
/// </summary>
public interface IBoundTable : IDisposable
{
    /// <summary>The function's output columns (may depend on the bound constant args).</summary>
    Schema OutputSchema { get; }

    /// <summary>True if <see cref="Execute"/> honors the pushdown spec (a discovered TVF); false otherwise.</summary>
    bool SupportsPushdown { get; }

    /// <summary>
    /// Runs the scan. <paramref name="specJson"/> (null => SELECT *) + <paramref name="filterValues"/>
    /// (null => no filter) carry projection + filter pushdown, honored only when <see cref="SupportsPushdown"/>
    /// is true. Returns the result rows; the stream owns the provider connection (released by the host at
    /// scan teardown).
    /// </summary>
    IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues);
}

/// <summary>An opened connection/catalog. Disposed when the native side closes the handle.</summary>
public interface IBackendCatalog : IDisposable
{
    /// <summary>Execute a query and return its result as an Arrow stream.</summary>
    IArrowArrayStream ExecuteQuery(string sql);

    /// <summary>Execute a non-query statement (DML/DDL); returns rows affected.</summary>
    long ExecuteNonQuery(string sql);

    /// <summary>
    /// Bulk-loads an Arrow stream into a table. If <paramref name="createTable"/>,
    /// the target is created from the stream's Arrow schema (mapping Arrow types
    /// to provider types); <paramref name="replace"/> drops it first. When
    /// <paramref name="checkConstraints"/> is set, CHECK / FOREIGN KEY constraints
    /// are validated during the load (INSERT semantics); otherwise they are skipped
    /// for bulk-load speed (COPY/CTAS) — SqlBulkCopy skips them by default. Returns
    /// rows written. This is the provider-specific bulk path (e.g. SqlBulkCopy).
    /// </summary>
    long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable, bool replace,
                    bool checkConstraints, long txnId);

    /// <summary>
    /// rowid-based DELETE. <paramref name="keys"/> columns (named by Arrow field)
    /// are the key values to delete. Returns rows affected.
    /// </summary>
    long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys);

    /// <summary>
    /// rowid-based UPDATE. The first <paramref name="setColumnCount"/> columns of
    /// <paramref name="data"/> are the SET values; the rest are the key values.
    /// Returns rows affected.
    /// </summary>
    long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data);

    /// <summary>
    /// Discovers catalog metadata as an Arrow stream. <paramref name="kind"/> is a
    /// <see cref="MetadataKind"/>; <paramref name="schema"/>/<paramref name="table"/>
    /// are supplied when the kind needs them. The backend owns all provider SQL
    /// (e.g. <c>sys.*</c>, primary-key / unique-index discovery). For
    /// <see cref="MetadataKind.Columns"/> the stream carries zero rows and its
    /// schema describes the table's columns.
    /// </summary>
    IArrowArrayStream GetMetadata(int kind, string? schema, string? table);

    /// <summary>
    /// Scans a table, returning its rows as an Arrow stream. The backend builds
    /// the provider SELECT (keeping read-path SQL out of the C++ host).
    /// <paramref name="specJson"/> (null => SELECT *) carries projection + filter
    /// pushdown: <c>{ "columns": [...], "filter": &lt;tree&gt; }</c>. Filter-tree
    /// constants are referenced by index into <paramref name="filterValues"/>, a
    /// one-batch Arrow stream of the typed constant values (null => no filter).
    /// </summary>
    IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson, IArrowArrayStream? filterValues);

    /// <summary>
    /// The Arrow schema describing a function's input parameters (one field per parameter, in order). Used
    /// to register the DuckDB function's argument types. Exported to the host as a bare <c>ArrowSchema</c>.
    /// </summary>
    Schema GetFunctionParamSchema(string schemaName, string functionName);

    /// <summary>The Arrow schema whose single field is the scalar function's return type.</summary>
    Schema GetFunctionReturnSchema(string schemaName, string functionName);

    /// <summary>
    /// Applies a scalar function over an input batch: <paramref name="args"/> columns are the argument
    /// values (in parameter order); returns one column of per-row results, typed as the return type.
    /// </summary>
    IArrowArrayStream ExecuteScalar(string schemaName, string functionName, IArrowArrayStream args);

    /// <summary>
    /// The Arrow schema describing a table-returning function's output columns.
    /// <paramref name="args"/> (null =&gt; none) is a 1-row batch of the constant call arguments — a custom
    /// table function's output schema may depend on them (bound via <see cref="ICatalogTableFunction.Bind"/>);
    /// discovered SQL TVFs/procs read their schema from metadata and ignore it.
    /// </summary>
    Schema GetFunctionOutputSchema(string schemaName, string functionName, RecordBatch? args = null);

    /// <summary>
    /// Binds one table-function call (Phase 5 session model — the successor to the removed
    /// <c>execute_table</c>/<c>execute_proc</c>). <paramref name="args"/> (nullable) is a 1-row batch of the
    /// constant call arguments. Returns an <see cref="IBoundTable"/> whose <see cref="IBoundTable.OutputSchema"/>
    /// is the function's output columns (a custom function's may depend on the args) and which executes the
    /// scan (possibly many times); the managed side classifies the function (discovered TVF / stored proc /
    /// custom). The binding is reused across (prepared) re-executions and disposed via the host's table_close.
    /// </summary>
    IBoundTable TableBind(string schemaName, string functionName, RecordBatch? args);

    /// <summary>
    /// Binds one streaming table-in-out call (Phase 6 exchange path) for every <c>_each</c> form.
    /// <paramref name="args"/> (nullable) is a 1-row batch of the constant "cost" arguments;
    /// <paramref name="inputSchema"/> is the input table's schema. Returns a binding whose
    /// <see cref="IArrowInOutBinding.OutputSchema"/> is the full output (input echo ++ the function's own
    /// columns) and whose <c>DoExchange</c> streams the transform — a discovered TVF (CROSS APPLY on a
    /// read-only connection), a stored proc (per-row EXEC on DuckDB's pinned write transaction), or a custom
    /// C# in-out. The gate-based exchange operator drives it.
    /// </summary>
    IArrowInOutBinding InOutBind(string schemaName, string functionName, RecordBatch? args, Schema inputSchema);

    /// <summary>
    /// Opens a custom-aggregate session for <c>schema.func</c> (a provider-authored
    /// <see cref="IAggregateFunction"/>). The session maps DuckDB's per-group <c>int64</c> state ids
    /// to live C# accumulators; the C++ aggregate callbacks marshal ids + argument columns through it. See
    /// <see cref="IAggregateSession"/>.
    /// </summary>
    IAggregateSession AggOpen(string schemaName, string functionName);

    /// <summary>
    /// Creates a table whose columns are described by <paramref name="columns"/>
    /// (a non-nullable field maps to NOT NULL). The backend maps Arrow types to
    /// provider types and runs the provider CREATE TABLE. When
    /// <paramref name="ifNotExists"/> is set, creation is skipped if it exists.
    /// <paramref name="primaryKey"/> is one comma-separated group of 0-based
    /// column indices for the PRIMARY KEY (null/empty if none);
    /// <paramref name="uniques"/> is ';'-separated groups of comma-separated
    /// indices, one UNIQUE constraint per group. <paramref name="defaults"/>
    /// carries literal column DEFAULTs as space-separated "<index> <payload>"
    /// pairs, payload = base64(value-text) or "-" for DEFAULT NULL. The text-column
    /// SQL type (mssql_ctas_text_type override / mssql_default_varchar_length) is read
    /// from the provider settings store, not passed here (see docs/settings-architecture.md).
    /// </summary>
    void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists, string? primaryKey,
                     string? uniques, string? defaults);

    /// <summary>Drops a table; <paramref name="ifExists"/> suppresses the missing-table error.</summary>
    void DropTable(string schemaName, string tableName, bool ifExists);

    /// <summary>Creates a schema; <paramref name="ifNotExists"/> guards creation.</summary>
    void CreateSchema(string schemaName, bool ifNotExists);

    /// <summary>Drops a schema; <paramref name="ifExists"/> suppresses the missing-schema error.</summary>
    void DropSchema(string schemaName, bool ifExists);

    /// <summary>
    /// Enters transaction mode for this catalog. The backend pins a connection +
    /// provider transaction (lazily, on the first write); subsequent DML runs on
    /// it until <see cref="CommitTransaction"/> / <see cref="RollbackTransaction"/>.
    /// </summary>
    void BeginTransaction();

    /// <summary>Commits the pinned transaction (no-op if none was opened).</summary>
    void CommitTransaction();

    /// <summary>Rolls back the pinned transaction (no-op if none was opened).</summary>
    void RollbackTransaction();

    /// <summary>
    /// Inserts the rows in <paramref name="rows"/> (its Arrow field names are the
    /// target column list) and returns the inserted rows — all table columns in
    /// table order, including generated identity/default values — as an Arrow
    /// stream (SQL Server <c>OUTPUT INSERTED.*</c>). Backs INSERT … RETURNING.
    /// </summary>
    IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows);

    /// <summary>
    /// Alters a table. <paramref name="alterKind"/> is an <see cref="AlterKind"/>;
    /// <paramref name="arg1"/>/<paramref name="arg2"/> are names (per kind). For
    /// <see cref="AlterKind.AddColumn"/> / <see cref="AlterKind.ColumnType"/> the
    /// new column's type is carried by <paramref name="column"/>. <paramref name="flags"/>
    /// bit 0 (<see cref="AlterKind.FlagIfExists"/>) is the if-(not-)exists guard.
    /// </summary>
    void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2, Field? column,
                    int flags);
}

/// <summary>
/// Process-wide registry of backends, **keyed by provider name** so one binary can host several
/// providers (SQL Server, later Power BI/DAX, …). On first use it loads the provider assemblies named
/// by <c>ARROWNET_BACKEND_ASSEMBLY</c> (comma-separated; default <c>ArrowNet.SqlServer</c>), finds every
/// <see cref="IBackend"/> implementation, and registers each under its <see cref="IBackend.Name"/> +
/// <see cref="IBackend.Aliases"/> (case-insensitive). If none are found it falls back to
/// <see cref="StubBackend"/> so the bridge still works standalone.
/// <para>
/// <see cref="Resolve"/> picks a backend by provider name; <see cref="Active"/> returns the default
/// (the sole backend, or the one named by <c>ARROWNET_DEFAULT_PROVIDER</c>) for call sites that don't
/// yet carry a provider — preserving single-provider behaviour until provider selection is wired through
/// the ABI.
/// </para>
/// </summary>
public static class BackendRegistry
{
    private static readonly object Gate = new();
    private static Dictionary<string, IBackend>? _byName; // name/alias (case-insensitive) -> backend
    private static string? _defaultProvider;              // canonical name of the default backend

    /// <summary>
    /// Explicitly registers a backend (e.g. from a host or test) under its name + aliases. The first
    /// registered backend becomes the default unless <c>ARROWNET_DEFAULT_PROVIDER</c> overrides it.
    /// </summary>
    public static void Register(IBackend backend)
    {
        lock (Gate)
        {
            _byName ??= NewMap();
            Add(_byName, backend);
            _defaultProvider ??= Environment.GetEnvironmentVariable("ARROWNET_DEFAULT_PROVIDER") ?? backend.Name;
        }
    }

    /// <summary>
    /// Resolves a backend by provider name or alias (case-insensitive). A null/empty provider yields the
    /// default (see <see cref="Active"/>). Throws when the provider is unknown.
    /// </summary>
    public static IBackend Resolve(string? provider)
    {
        var map = Map();
        if (string.IsNullOrWhiteSpace(provider))
        {
            return Default(map);
        }
        if (map.TryGetValue(provider.Trim(), out var backend))
        {
            return backend;
        }
        var known = string.Join(", ", map.Values.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        throw new ArgumentException($"mssql_net: unknown provider '{provider}'. Registered providers: {known}.");
    }

    /// <summary>
    /// The default backend, for call sites that don't yet carry a provider (the sole registered backend,
    /// or the one named by <c>ARROWNET_DEFAULT_PROVIDER</c> / the first registered).
    /// </summary>
    public static IBackend Active => Default(Map());

    /// <summary>All distinct registered backends — for host-side enumeration (e.g. listing every provider's
    /// declared settings at load).</summary>
    public static IEnumerable<IBackend> All() => Map().Values.Distinct();

    private static IBackend Default(Dictionary<string, IBackend> map)
    {
        if (_defaultProvider != null && map.TryGetValue(_defaultProvider, out var named))
        {
            return named;
        }
        // Exactly one backend (the common single-provider case) => it. Otherwise the first by name.
        return map.Values.Distinct().First();
    }

    private static Dictionary<string, IBackend> Map()
    {
        lock (Gate)
        {
            return _byName ??= Discover();
        }
    }

    private static Dictionary<string, IBackend> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    private static void Add(Dictionary<string, IBackend> map, IBackend backend)
    {
        map[backend.Name] = backend;
        foreach (var alias in backend.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                map[alias] = backend;
            }
        }
    }

    private static Dictionary<string, IBackend> Discover()
    {
        var map = NewMap();
        var names = Environment.GetEnvironmentVariable("ARROWNET_BACKEND_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(names))
        {
            // Default to every shipped provider; a missing/unloadable assembly is skipped below, so listing
            // ArrowNet.AnalysisServices here is harmless when only the SqlServer provider is published.
            names = "ArrowNet.SqlServer,ArrowNet.AnalysisServices";
        }
        foreach (var assemblyName in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var assembly = System.Reflection.Assembly.Load(assemblyName);
                foreach (var type in assembly.GetTypes())
                {
                    if (!type.IsAbstract && typeof(IBackend).IsAssignableFrom(type) &&
                        type.GetConstructor(Type.EmptyTypes) != null)
                    {
                        var backend = (IBackend)Activator.CreateInstance(type)!;
                        Add(map, backend);
                        _defaultProvider ??= Environment.GetEnvironmentVariable("ARROWNET_DEFAULT_PROVIDER") ?? backend.Name;
                    }
                }
            }
            catch
            {
                // Assembly missing/unloadable — skip it; fall back to the stub below if nothing registered.
            }
        }
        if (map.Count == 0)
        {
            Add(map, new StubBackend());
        }
        return map;
    }
}
