// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.C;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.Adbc;

/// <summary>
/// The <c>adbc</c> provider: <c>ATTACH 'driver=&lt;library&gt;;&lt;option&gt;=&lt;value&gt;;…' AS a (TYPE fabricator,
/// PROVIDER 'adbc')</c>. <c>driver</c> names the native ADBC driver library (and <c>entrypoint</c> its init function,
/// <c>AdbcDriverInit</c> by default); every other key is a database option handed to the driver as it is.
/// </summary>
public sealed class AdbcProvider : IProvider
{
    public string Name => "adbc";

    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) => baseConnString;

    public IProviderCatalog OpenCatalog(string connectionString, string optionsJson) => new AdbcCatalog(connectionString, optionsJson);
}

internal sealed class AdbcCatalog : IProviderCatalog
{
    private readonly AdbcDriver _driver;
    private readonly AdbcDatabase _database;
    private readonly object _connectGate = new();
    private readonly string? _catalogName;
    private readonly string? _defaultSchema;

    // Plans the host pushed, by the handle the host runs them through (TranslateRemotePlan -> ExecuteQuery).
    private readonly ConcurrentDictionary<string, PushedPlan> _plans = new(StringComparer.Ordinal);

    private sealed record PushedPlan(byte[] Plan, Schema Schema, bool Writes = false);

    // The driver connection of each DuckDB transaction that wrote (AdbcTransaction).
    private readonly TransactionManager<AdbcTransaction> _txns = new(id => new AdbcTransaction(id));

    private const string PlanPrefix = "adbc-substrait-plan:";

    /// <summary>
    /// The longest IN list a filter sends as a list (ATTACH option <c>max_in_list</c>, default 1000); a longer one goes
    /// as its range (integers and temporals) or not at all. A driver writes the list into its statement: one that
    /// renders a long list as a relation (as the T-SQL emitter does, with OPENJSON) takes far more than one that writes
    /// literals — and a statement too large for the server fails only when it runs, after Prepare accepted it.
    /// </summary>
    internal int MaxInList { get; } = 1000;

    /// <summary>
    /// ATTACH option <c>consistent_reads</c> (default false): the user's promise that one statement reads the same rows
    /// the same way twice (SQL Server with READ_COMMITTED_SNAPSHOT, a store without concurrent writers). Without it a
    /// plan that reads the same rows more than once is declined: the host inlined a CTE into two reads, and under
    /// concurrent writes they can differ (docs/duckdb-2.0-remote-execution.md §4.16). A driver cannot say so itself.
    /// </summary>
    internal bool ConsistentReads { get; }

    /// <summary>DuckDB's own row id, the virtual row identity of a DuckDB server table with no key.</summary>
    internal const string RowId = "rowid";

    /// <summary>The driver reads a scan's columns by name (a DuckDB server).</summary>
    internal bool ReadsByName => _duckDbServer;

    public AdbcCatalog(string connectionString, string? optionsJson = null)
    {
        if (!string.IsNullOrWhiteSpace(optionsJson))
        {
            using var attach = System.Text.Json.JsonDocument.Parse(optionsJson);
            if (attach.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && attach.RootElement.TryGetProperty("max_in_list", out var max))
            {
                string text = max.ValueKind == System.Text.Json.JsonValueKind.String ? max.GetString()! : max.GetRawText();
                MaxInList = int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0
                    ? n
                    : throw new ArgumentException($"adbc: max_in_list must be a positive integer, not '{text}'");
            }
            if (attach.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && attach.RootElement.TryGetProperty("consistent_reads", out var consistent))
            {
                string text = consistent.ValueKind == System.Text.Json.JsonValueKind.String
                    ? consistent.GetString()! : consistent.GetRawText();
                ConsistentReads = text.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                    : text.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                    : throw new ArgumentException($"adbc: consistent_reads must be true or false, not '{text}'");
            }
        }
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        string? library = null;
        string entryPoint = "AdbcDriverInit";
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in builder.Keys)
        {
            string value = builder[key]?.ToString() ?? string.Empty;
            switch (key)
            {
                case "driver": library = value; break;
                case "entrypoint": entryPoint = value; break;
                default: options[key] = value; break;
            }
        }
        if (string.IsNullOrEmpty(library))
            throw new ArgumentException("adbc: the connection string needs 'driver=<path of the ADBC driver library>'");

        _driver = CAdbcDriverImporter.Load(library, entryPoint);
        _database = _driver.Open(options);
        using var connection = Connect();
        _catalogName = Option(connection, "adbc.connection.catalog");
        _defaultSchema = Option(connection, "adbc.connection.db_schema");
        _duckDbServer = VendorName(connection) == "DuckDB";
        if (_duckDbServer)
            _duckDbSettings = ReadDuckDbSettings(connection);
    }

    // The driver's server is DuckDB (GetInfo vendor_name; quack-net's driver for a quack server): its driver writes our
    // plans as DuckDB SQL, so DuckDB's own functions may go by name (capability substrait_native_functions), and the
    // same text means the same thing there only while the two sessions agree on the settings it depends on - checked
    // here, since only this side knows the calling session.
    private readonly bool _duckDbServer;
    private readonly DuckDbSessionSettings _duckDbSettings = DuckDbSessionSettings.Default;

    /// <summary>The driver's GetInfo vendor_name; null when it has none.</summary>
    private static string? VendorName(AdbcConnection connection)
    {
        try
        {
            using var stream = connection.GetInfo(new List<AdbcInfoCode> { AdbcInfoCode.VendorName });
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                using (batch)
                {
                    if (batch.Length == 0 || batch.Column(1) is not UnionArray values)
                        continue;
                    int offset = values is DenseUnionArray dense ? dense.ValueOffsets[0] : 0;
                    var child = values.Fields[values.TypeIds[0]];
                    return child is StringArray text ? text.GetString(offset) : null;
                }
            }
        }
        catch (Exception ex) when (ex is AdbcException or NotImplementedException or NotSupportedException)
        {
        }
        return null;
    }

    /// <summary>The server's settings and version (DuckDbSettings.Compared), read once at ATTACH.</summary>
    private static DuckDbSessionSettings ReadDuckDbSettings(AdbcConnection connection)
    {
        using var statement = connection.CreateStatement();
        statement.SqlQuery = DuckDbSessionSettings.Query;
        using var stream = statement.ExecuteQuery().Stream!;
        return DuckDbSessionSettings.Read(stream);
    }

    /// <summary>
    /// The calling session's settings, read through the host (as that session, so its SETs count). A plan is written
    /// for the server only when they match the server's: the same text means the same thing only then.
    /// </summary>
    private void CheckSessionSettings()
    {
        var host = FabricatorServices.Get<IHostQuery>()
            ?? throw new NotSupportedException(
                "adbc: the host's settings cannot be read here (no IHostQuery), so the server's cannot be compared");
        DuckDbSessionSettings local;
        using (var stream = host.Query(DuckDbSessionSettings.Query, inheritSession: true))
            local = DuckDbSessionSettings.Read(stream);
        if (_duckDbSettings.Difference(local) is { } difference)
            throw new NotSupportedException("adbc: DuckDB server: " + difference);
    }

    /// <summary>Puts a plan on a statement.</summary>
    private static void SetPlan(AdbcStatement statement, byte[] plan) => statement.SubstraitPlan = plan;

    /// <summary>
    /// Whether the plan may go: the driver's Prepare refuses one it cannot run, before anything executes; and for a
    /// DuckDB server the sessions' settings must agree. Every plan carrying expressions passes here first (a pushed
    /// query, a scan's filters); what goes without it is a plain read, which no setting changes.
    /// </summary>
    private void CheckPlan(AdbcStatement statement, byte[] plan)
    {
        if (_duckDbServer)
            CheckSessionSettings();
        SetPlan(statement, plan);
        statement.Prepare();
    }

    private static bool IsRefusal(Exception ex) =>
        ex is AdbcException or NotImplementedException or NotSupportedException;

    private AdbcConnection Connect()
    {
        AdbcConnection connection;
        lock (_connectGate)
            connection = _database.Connect(new Dictionary<string, string>());
        if (_duckDbServer)
        {
            // results and table schemas in DuckDB's lossless Arrow forms: the default ones read a HUGEINT column as
            // DECIMAL(38,0), a UUID as VARCHAR, a TIMETZ as TIME
            try
            {
                connection.SetOption("quack.arrow_lossless_conversion", "true");
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        return connection;
    }

    private static string? Option(AdbcConnection connection, string key)
    {
        try
        {
            return connection.GetOption(key);
        }
        catch (AdbcException)
        {
            return null;
        }
    }

    // superset_filter_pushdown: the host hands a scan (and a pushed query) its filters, a hash join's runtime keys
    // among them, and re-applies them, so the driver may return more rows than they select.
    public string CapabilitiesJson =>
        "{\"remote_select\":true,\"superset_filter_pushdown\":true"
        + (_duckDbServer ? ",\"substrait_native_functions\":true,\"lossless_arrow_boundary\":true,\"dml_returning\":true" : "") + (_defaultSchema is null ? "" : ",\"default_schema\":" +
            System.Text.Json.JsonSerializer.Serialize(_defaultSchema)) + "}";

    // ── discovery ────────────────────────────────────────────────────────────────────────────────────────────

    private List<(string Schema, string Table, string Type)> Objects()
    {
        using var connection = Connect();
        using var stream = connection.GetObjects(AdbcConnection.GetObjectsDepth.Tables, _catalogName, null, null,
            null, null);
        var result = new List<(string, string, string)>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
        {
            using (batch)
            {
                var schemas = (ListArray)batch.Column("catalog_db_schemas");
                for (int c = 0; c < batch.Length; c++)
                {
                    if (schemas.IsNull(c))
                        continue;
                    var schemaRows = (StructArray)schemas.GetSlicedValues(c);
                    var schemaNames = (StringArray)schemaRows.Fields[schemaRows.Data.DataType is StructType st
                        ? st.GetFieldIndex("db_schema_name") : 0];
                    var tables = (ListArray)schemaRows.Fields[((StructType)schemaRows.Data.DataType).GetFieldIndex("db_schema_tables")];
                    for (int s = 0; s < schemaRows.Length; s++)
                    {
                        string schemaName = schemaNames.GetString(s) ?? string.Empty;
                        if (tables.IsNull(s))
                        {
                            result.Add((schemaName, string.Empty, string.Empty));
                            continue;
                        }
                        var tableRows = (StructArray)tables.GetSlicedValues(s);
                        var tableType = (StructType)tableRows.Data.DataType;
                        var tableNames = (StringArray)tableRows.Fields[tableType.GetFieldIndex("table_name")];
                        var tableTypes = (StringArray)tableRows.Fields[tableType.GetFieldIndex("table_type")];
                        if (tableRows.Length == 0)
                            result.Add((schemaName, string.Empty, string.Empty));
                        for (int t = 0; t < tableRows.Length; t++)
                            result.Add((schemaName, tableNames.GetString(t) ?? string.Empty, tableTypes.GetString(t) ?? "TABLE"));
                    }
                }
            }
        }
        return result;
    }

    public IArrowArrayStream GetSchemas()
    {
        var names = new StringArray.Builder();
        foreach (string schema in Objects().Select(o => o.Schema).Distinct(StringComparer.Ordinal))
            names.Append(schema);
        var array = names.Build();
        var schemaOut = new Schema(new[] { new Field("schema_name", StringType.Default, false) }, null);
        return new InMemoryArrayStream(schemaOut, new[] { new RecordBatch(schemaOut, new IArrowArray[] { array }, array.Length) });
    }

    public IArrowArrayStream GetTables()
    {
        var schemas = new StringArray.Builder();
        var tables = new StringArray.Builder();
        var types = new StringArray.Builder();
        int n = 0;
        foreach (var o in Objects().Where(o => o.Table.Length > 0))
        {
            schemas.Append(o.Schema);
            tables.Append(o.Table);
            types.Append(o.Type);
            n++;
        }
        var schemaOut = new Schema(new[]
        {
            new Field("schema_name", StringType.Default, false),
            new Field("table_name", StringType.Default, false),
            new Field("table_type", StringType.Default, false),
        }, null);
        return new InMemoryArrayStream(schemaOut, new[]
        {
            new RecordBatch(schemaOut, new IArrowArray[] { schemas.Build(), tables.Build(), types.Build() }, n),
        });
    }

    public IEnumerable<FunctionDeclaration> GetFunctions() => System.Array.Empty<FunctionDeclaration>();
    public IEnumerable<CatalogMacroDefinition> GetMacros() => System.Array.Empty<CatalogMacroDefinition>();
    public IEnumerable<ViewDefinition> GetViews() => System.Array.Empty<ViewDefinition>();

    public IArrowArrayStream GetServerInfo()
    {
        var schema = new Schema(new[]
        {
            new Field("property", StringType.Default, true),
            new Field("value", StringType.Default, true),
        }, null);
        var keys = new StringArray.Builder().Append("catalog").Append("default_schema").Build();
        var values = new StringArray.Builder().Append(_catalogName).Append(_defaultSchema).Build();
        return new InMemoryArrayStream(schema, new[] { new RecordBatch(schema, new IArrowArray[] { keys, values }, 2) });
    }

    public ITable GetTable(string schemaName, string tableName) => new AdbcTable(this, schemaName, tableName);

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        using var binding = GetTable(schemaName, tableName).Bind(null);
        return binding.Scan(specJson, filterValues);
    }

    internal Schema TableSchema(string schemaName, string tableName)
    {
        try
        {
            if (Pinned() is { } txn)
            {
                lock (txn.Gate)
                    return txn.Connection!.GetTableSchema(_catalogName, schemaName, tableName);
            }
            using var connection = Connect();
            return connection.GetTableSchema(_catalogName, schemaName, tableName);
        }
        catch (AdbcException ex) when (ex.Status == AdbcStatusCode.NotFound)
        {
            // the driver says the table is not there (DuckDB also asks for spellings the server does not have: its
            // names are case-insensitive, Kusto's are not) — absence, not a failure
            throw new ObjectNotFoundException("table", schemaName + "." + tableName, ex);
        }
    }

    private const short RowCountStatistic = 6; // ADBC_STATISTIC_ROW_COUNT_KEY

    /// <summary>
    /// The table's row count from the driver's statistics (ADBC <c>GetStatistics</c>, approximate allowed); null when
    /// the driver has none. Without it DuckDB takes the table for one row and builds a join's hash table over it, so a
    /// local table's join keys never reach the driver.
    /// </summary>
    /// <summary>
    /// A DuckDB server's table key — its PRIMARY KEY, else its first UNIQUE constraint — as the row identity a MERGE
    /// matches by; empty for any other server, or a table with neither.
    /// </summary>
    internal IReadOnlyList<string> KeyColumns(string schemaName, string tableName)
    {
        if (!_duckDbServer)
            return System.Array.Empty<string>();
        static string Text(string value) => "'" + value.Replace("'", "''") + "'";
        string sql = "SELECT CAST(unnest(constraint_column_names) AS VARCHAR) FROM (SELECT constraint_column_names "
                     + "FROM duckdb_constraints() WHERE database_name = current_database() AND schema_name = "
                     + Text(schemaName) + " AND table_name = " + Text(tableName)
                     + " AND constraint_type IN ('PRIMARY KEY', 'UNIQUE') "
                     + "ORDER BY constraint_type <> 'PRIMARY KEY', constraint_index LIMIT 1)";
        var columns = new List<string>();
        using (var stream = RunSql(sql))
        {
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                using (batch)
                {
                    var names = (StringArray)batch.Column(0);
                    for (int i = 0; i < batch.Length; i++)
                        columns.Add(names.GetString(i)!);
                }
            }
        }
        return columns;
    }

    internal long? RowCount(string schemaName, string tableName)
    {
        try
        {
            using var connection = Connect();
            // the names are patterns (LIKE): match the rows exactly below
            using var stream = connection.GetStatistics(_catalogName, schemaName, tableName, approximate: true);
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                using (batch)
                {
                    if (RowCountIn(batch, schemaName, tableName) is { } rows)
                        return rows;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is AdbcException or NotImplementedException or NotSupportedException)
        {
            return null;
        }
    }

    private static long? RowCountIn(RecordBatch batch, string schemaName, string tableName)
    {
        // catalog_db_schemas: list<struct<db_schema_name, db_schema_statistics: list<struct<table_name, column_name,
        // statistic_key, statistic_value: union<int64, uint64, float64, binary>, statistic_is_approximate>>>>
        var schemas = (ListArray)batch.Column("catalog_db_schemas");
        var schemaRows = (StructArray)schemas.Values;
        var schemaType = (StructType)schemaRows.Data.DataType;
        var schemaNames = (StringArray)schemaRows.Fields[schemaType.GetFieldIndex("db_schema_name")];
        var statistics = (ListArray)schemaRows.Fields[schemaType.GetFieldIndex("db_schema_statistics")];
        var rows = (StructArray)statistics.Values;
        var rowType = (StructType)rows.Data.DataType;
        var tables = (StringArray)rows.Fields[rowType.GetFieldIndex("table_name")];
        var columns = rows.Fields[rowType.GetFieldIndex("column_name")];
        var keys = (Int16Array)rows.Fields[rowType.GetFieldIndex("statistic_key")];
        var values = (UnionArray)rows.Fields[rowType.GetFieldIndex("statistic_value")];
        for (int c = 0; c < batch.Length; c++)
        {
            if (schemas.IsNull(c))
                continue;
            for (int s = schemas.ValueOffsets[c]; s < schemas.ValueOffsets[c + 1]; s++)
            {
                if (statistics.IsNull(s) || !string.Equals(schemaNames.GetString(s), schemaName, StringComparison.OrdinalIgnoreCase))
                    continue;
                for (int r = statistics.ValueOffsets[s]; r < statistics.ValueOffsets[s + 1]; r++)
                {
                    if (keys.GetValue(r) == RowCountStatistic && columns.IsNull(r)
                        && string.Equals(tables.GetString(r), tableName, StringComparison.OrdinalIgnoreCase)
                        && UnionValue(values, r) is { } count)
                        return count;
                }
            }
        }
        return null;
    }

    private static long? UnionValue(UnionArray union, int index)
    {
        int child = System.Array.IndexOf(((UnionType)union.Data.DataType).TypeIds, (int)union.TypeIds[index]);
        if (child < 0)
            return null;
        int offset = union is DenseUnionArray dense ? dense.ValueOffsets[index] : index;
        return union.Fields[child] switch
        {
            Int64Array a when !a.IsNull(offset) => a.GetValue(offset),
            UInt64Array a when !a.IsNull(offset) => (long)Math.Min(a.GetValue(offset)!.Value, long.MaxValue),
            DoubleArray a when !a.IsNull(offset) => (long)Math.Round(a.GetValue(offset)!.Value),
            _ => null,
        };
    }

    // ── reads ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a Substrait plan; the result columns are renamed to <paramref name="names"/> by position.</summary>
    internal IArrowArrayStream RunPlan(byte[] plan, IReadOnlyList<string> names) =>
        RunFiltered(_ => plan, System.Array.Empty<ProtoWriter>(), new SubstraitFunctions(), names);

    /// <summary>
    /// Runs the plan <paramref name="build"/> makes for a filter, with as many of <paramref name="conjuncts"/> as the
    /// driver translates: all of them when it can, else those it translates one by one, else none. Leaving one out
    /// only widens the result, which the host filters again. <paramref name="select"/> picks result columns by
    /// position (after the rename); null keeps them all.
    /// </summary>
    /// <param name="pin">Run in the transaction's own connection, opening it if this is its first use.</param>
    internal IArrowArrayStream RunFiltered(Func<ProtoWriter?, byte[]> build, IReadOnlyList<ProtoWriter> conjuncts,
                                           SubstraitFunctions functions, IReadOnlyList<string> names,
                                           IReadOnlyList<int>? select = null, bool pin = false)
    {
        if ((pin ? WriteTransaction() : Pinned()) is { } txn)
        {
            lock (txn.Gate)
            {
                var pinned = txn.Connection!.CreateStatement();
                SetPlan(pinned, ChoosePlan(pinned, build, conjuncts, functions));
                return Drained(new OwningStream(pinned.ExecuteQuery().Stream!, names, pinned, null, select));
            }
        }
        var connection = Connect();
        AdbcStatement? statement = null;
        try
        {
            statement = connection.CreateStatement();
            SetPlan(statement, ChoosePlan(statement, build, conjuncts, functions));
            var result = statement.ExecuteQuery();
            return new OwningStream(result.Stream!, names, statement, connection, select);
        }
        catch
        {
            statement?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    private byte[] ChoosePlan(AdbcStatement statement, Func<ProtoWriter?, byte[]> build,
                              IReadOnlyList<ProtoWriter> conjuncts, SubstraitFunctions functions)
    {
        if (conjuncts.Count == 0)
            return build(null);
        // Prepare translates the plan without running it (a driver that cannot refuses there)
        bool Translates(byte[] plan)
        {
            try
            {
                CheckPlan(statement, plan);
                return true;
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                Debug("the driver refused a filter: " + ex.Message);
                return false;
            }
        }
        var all = build(SubstraitFilter.And(conjuncts, functions));
        if (Translates(all))
        {
            Debug($"filter: all {conjuncts.Count} conjuncts sent, plan {all.Length} bytes");
            return all;
        }
        if (conjuncts.Count > 1)
        {
            var kept = conjuncts.Where(c => Translates(build(c))).ToList();
            if (kept.Count > 0)
            {
                var plan = build(SubstraitFilter.And(kept, functions));
                if (Translates(plan))
                {
                    Debug($"filter: {kept.Count} of {conjuncts.Count} conjuncts sent, plan {plan.Length} bytes");
                    return plan;
                }
            }
        }
        Debug($"filter: none of {conjuncts.Count} conjuncts sent");
        return build(null);
    }

    private static void Debug(string message)
    {
        try
        {
            var log = FabricatorServices.Get<IHostLog>()?.GetLogger("Fabricator.Adbc");
            if (log?.IsEnabled(HostLogLevel.Debug) == true)
                log.Log(HostLogLevel.Debug, "adbc " + message);
        }
        catch
        {
            // logging must never fail a scan
        }
    }

    /// <summary>The single batch of filter constants the host sends with a scan; null when there are none.</summary>
    internal static RecordBatch? ReadValues(IArrowArrayStream? values)
    {
        if (values is null)
            return null;
        using (values)
            return values.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Whether the plan's root relation is a write (Plan.relations → PlanRel.root → RelRoot.input → Rel.write).</summary>
    internal static bool IsWritePlan(byte[] plan)
    {
        var reader = new ProtoReader(plan);
        while (reader.Next(out int field, out int wire, out _))
        {
            if (field != 3 || wire != 2) // Plan.relations
            {
                reader.Skip(wire);
                continue;
            }
            var relation = reader.Message();
            while (relation.Next(out int rf, out int rw, out _))
            {
                if (rf != 2 || rw != 2) // PlanRel.root
                {
                    relation.Skip(rw);
                    continue;
                }
                var root = relation.Message();
                while (root.Next(out int of, out int ow, out _))
                {
                    if (of != 1 || ow != 2) // RelRoot.input
                    {
                        root.Skip(ow);
                        continue;
                    }
                    var rel = root.Message();
                    return rel.Next(out int kind, out _, out _) && kind == 19; // Rel.write
                }
            }
        }
        return false;
    }

    /// <summary>The index of a column by name: exact first, then ignoring case (DuckDB names are case-insensitive).</summary>
    internal static int? IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return i;
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return null;
    }

    public RemoteQuery? TranslateRemotePlan(byte[] substraitPlan)
    {
        // hints the plan needs honoured and a driver does not read: DuckDB computes such a query itself
        // a DuckDB server honours both hints itself: one DuckDB statement reads one snapshot, and its driver writes
        // the any-row single join
        var hints = SubstraitPlanFilter.Optimizations(substraitPlan);
        if (!_duckDbServer && hints.Contains("fabricator.repeated_reads") && !ConsistentReads)
            return RemoteQuery.Declined("adbc: the query reads the same rows more than once, and the driver cannot "
                                        + "promise one statement reads them alike (ATTACH option consistent_reads)");
        if (!_duckDbServer && hints.Contains("fabricator.single_join_any_row"))
            return RemoteQuery.Declined("adbc: a scalar subquery may take any of several rows here, which the driver "
                                        + "would refuse instead");
        bool writes = IsWritePlan(substraitPlan);
        Schema schema;
        try
        {
            using var connection = Connect();
            using var statement = connection.CreateStatement();
            CheckPlan(statement, substraitPlan); // a driver that cannot run the plan refuses here, before anything executes
            // a write has no result to describe: it answers its affected-row count
            schema = writes ? new Schema(System.Array.Empty<Field>(), null) : statement.ExecuteSchema();
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            Debug("declined: " + ex.Message);
            return RemoteQuery.Declined("adbc driver: " + ex.Message);
        }
        string handle = PlanPrefix + Guid.NewGuid().ToString("N");
        if (writes)
        {
            _plans[handle] = new PushedPlan(substraitPlan, schema, Writes: true);
            return new RemoteQuery(handle, 1, System.Array.Empty<bool>()) { ChangesRows = true };
        }
        var renamed = Renamed(schema, Enumerable.Range(0, schema.FieldsList.Count).Select(i => "c" + i).ToList());
        _plans[handle] = new PushedPlan(substraitPlan, renamed);
        return new RemoteQuery(handle, schema.FieldsList.Count, System.Array.Empty<bool>());
    }

    public Schema? DescribeQuery(string sql) =>
        _plans.TryGetValue(sql, out var pushed) ? pushed.Schema : null;

    public IArrowArrayStream ExecuteQuery(string sql)
    {
        if (_plans.TryGetValue(sql, out var pushed))
            return RunPlan(pushed.Plan, pushed.Schema.FieldsList.Select(f => f.Name).ToList());
        return RunSql(sql);
    }

    /// <summary>
    /// A pushed query under the host's table filters (ABI v99) — above all a hash join's runtime keys: the plan's
    /// root goes inside a <c>FilterRel</c> carrying them, and the result keeps the columns the spec projects.
    /// </summary>
    public IArrowArrayStream ExecuteQuery(string sql, RecordBatch? parameters, string? scanSpec,
                                          IArrowArrayStream? filterValues)
    {
        using var values = ReadValues(filterValues);
        if (parameters is not null)
            throw new NotSupportedException("adbc: query parameters (the 'params' argument) are not supported");
        var spec = ScanSpec.Parse(scanSpec);
        if (!_plans.TryGetValue(sql, out var pushed))
            return RunSql(sql, spec?.Columns);
        var names = pushed.Schema.FieldsList.Select(f => f.Name).ToList();
        IReadOnlyList<int>? selected = spec?.Columns is { Count: > 0 } columns
            ? columns.Select(c => IndexOf(names, c) ?? throw new InvalidOperationException(
                $"adbc: the pushed query has no column '{c}'")).ToList()
            : null;
        var functions = SubstraitPlanFilter.FunctionsFor(pushed.Plan);
        var conjuncts = spec?.Filter is { } filter
            ? SubstraitFilter.Conjuncts(filter, name => IndexOf(names, name), values, functions, MaxInList)
            : new List<ProtoWriter>();
        return RunFiltered(
            condition => condition is null ? pushed.Plan : SubstraitPlanFilter.Wrap(pushed.Plan, condition, functions),
            conjuncts, functions, names, selected);
    }

    /// <summary>Runs SQL text; <paramref name="columns"/> picks result columns by name (null or empty: all).</summary>
    private IArrowArrayStream RunSql(string sql, IReadOnlyList<string>? columns = null)
    {

        var connection = Connect();
        AdbcStatement? statement = null;
        try
        {
            statement = connection.CreateStatement();
            statement.SqlQuery = sql;
            var result = statement.ExecuteQuery();
            var stream = result.Stream!;
            var names = stream.Schema.FieldsList.Select(f => f.Name).ToList();
            IReadOnlyList<int>? select = columns is { Count: > 0 }
                ? columns.Select(c => IndexOf(names, c) ?? throw new InvalidOperationException(
                    $"adbc: the query has no column '{c}'")).ToList()
                : null;
            return new OwningStream(stream, names, statement, connection, select);
        }
        catch
        {
            statement?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    public long ExecuteNonQuery(string sql)
    {
        if (_plans.TryGetValue(sql, out var pushed) && pushed.Writes)
        {
            // a pushed write: on the transaction's connection, committed with the DuckDB transaction
            var txn = WriteTransaction();
            if (txn is not null)
            {
                lock (txn.Gate)
                {
                    using var pinned = txn.Connection!.CreateStatement();
                    SetPlan(pinned, pushed.Plan);
                    return pinned.ExecuteUpdate().AffectedRows;
                }
            }
            using var own = Connect();
            using var write = own.CreateStatement();
            SetPlan(write, pushed.Plan);
            return write.ExecuteUpdate().AffectedRows;
        }
        using var connection = Connect();
        using var statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        return statement.ExecuteUpdate().AffectedRows;
    }

    internal static Schema Renamed(Schema schema, IReadOnlyList<string> names)
    {
        var fields = schema.FieldsList.Select((f, i) =>
            new Field(i < names.Count ? names[i] : f.Name, f.DataType, f.IsNullable, f.Metadata));
        return new Schema(fields, schema.Metadata);
    }

    // ── writes: not in this slice ────────────────────────────────────────────────────────────────────────────

    private static NotSupportedException ReadOnly(string what) =>
        new($"adbc: {what} is not supported yet (the adbc provider is read-only)");

    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists, string? primaryKey,
                            string? uniques, string? defaults, IReadOnlyList<string>? partitionColumns,
                            IReadOnlyList<string>? sortColumns, IReadOnlyList<string>? identityColumns, string? optionsJson)
    {
        RequireDuckDb("CREATE TABLE");
        if (partitionColumns is { Count: > 0 } || sortColumns is { Count: > 0 } || identityColumns is { Count: > 0 }
            || !string.IsNullOrEmpty(optionsJson))
            throw new NotSupportedException("adbc: CREATE TABLE with PARTITIONED BY, SORTED BY, an identity or WITH options is not supported");
        ExecuteDdl(DuckDbDdl.CreateTable(schemaName, tableName, columns, ifNotExists, replace: false, primaryKey, uniques, defaults));
    }

    public void DropTable(string schemaName, string tableName, bool ifExists)
    {
        RequireDuckDb("DROP TABLE");
        ExecuteDdl("DROP TABLE " + (ifExists ? "IF EXISTS " : "") + DuckDbDdl.Table(schemaName, tableName));
    }

    public void CreateSchema(string schemaName, bool ifNotExists)
    {
        RequireDuckDb("CREATE SCHEMA");
        ExecuteDdl("CREATE SCHEMA " + (ifNotExists ? "IF NOT EXISTS " : "") + DuckDbDdl.Quote(schemaName));
    }

    public void DropSchema(string schemaName, bool ifExists)
    {
        RequireDuckDb("DROP SCHEMA");
        ExecuteDdl("DROP SCHEMA " + (ifExists ? "IF EXISTS " : "") + DuckDbDdl.Quote(schemaName));
    }

    public void AlterTable(AlterTableSpec spec, string schemaName, string tableName, Field? column)
    {
        RequireDuckDb("ALTER TABLE");
        if (spec.Kind == AlterTableKind.SetDefault && column is null && spec.Column is { } name)
        {
            // SET DEFAULT carries no type: the column's own, so a number is written as one (DuckDB stores it as given)
            var schema = TableSchema(schemaName, tableName);
            column = schema.FieldsList.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        ExecuteDdl(DuckDbDdl.Alter(spec, schemaName, tableName, column));
    }

    private void RequireDuckDb(string what)
    {
        if (!_duckDbServer)
            throw ReadOnly(what);
    }

    /// <summary>DDL in the DuckDB transaction (its pinned connection), so it commits or rolls back with it.</summary>
    private void ExecuteDdl(string sql)
    {
        var txn = WriteTransaction();
        if (txn is null)
        {
            using var own = Connect();
            using var statement = own.CreateStatement();
            statement.SqlQuery = sql;
            statement.ExecuteUpdate();
            return;
        }
        lock (txn.Gate)
        {
            using var statement = txn.Connection!.CreateStatement();
            statement.SqlQuery = sql;
            statement.ExecuteUpdate();
        }
    }

    public void BeginTransaction(bool isExplicit)
    {
        if (isExplicit && AmbientTransaction.Current != 0)
            _txns.GetOrCreate(AmbientTransaction.Current).IsExplicit = true;
    }

    public void CommitTransaction() => EndTransaction(commit: true);

    public void RollbackTransaction() => EndTransaction(commit: false);

    private void EndTransaction(bool commit)
    {
        // removed before completed: nothing resolves a transaction that is being completed
        using var txn = _txns.Remove(AmbientTransaction.Current);
        txn?.Complete(commit);
    }

    /// <summary>This transaction's pinned connection once it wrote; null otherwise (a read then takes its own).</summary>
    private AdbcTransaction? Pinned() =>
        AmbientTransaction.Current is var id and not 0 && _txns.TryGet(id) is { Connection: not null } txn ? txn : null;

    /// <summary>The transaction a write runs in, its connection opened (autocommit off) by the first write.</summary>
    private AdbcTransaction? WriteTransaction() => WriteTransaction(AmbientTransaction.Current);

    /// <param name="id">The DuckDB transaction (a bulk load runs on its own thread and is handed the id).</param>
    private AdbcTransaction? WriteTransaction(long id)
    {
        if (id == 0)
            return null;
        var txn = _txns.GetOrCreate(id);
        lock (txn.Gate)
        {
            if (txn.Connection is null)
            {
                var connection = Connect();
                try
                {
                    connection.AutoCommit = false;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
                txn.Connection = connection;
            }
        }
        return txn;
    }

    /// <summary>A result read whole, so the pinned connection is free for the next statement.</summary>
    private static IArrowArrayStream Drained(IArrowArrayStream stream)
    {
        using (stream)
        {
            var batches = new List<RecordBatch>();
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
                batches.Add(batch);
            return new InMemoryArrayStream(stream.Schema, batches);
        }
    }

    public IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows)
    {
        RequireDuckDb("INSERT … RETURNING");
        var txn = WriteTransaction();
        if (txn is null)
        {
            using var own = Connect();
            return InsertReturning(own, schemaName, tableName, rows);
        }
        lock (txn.Gate)
            return InsertReturning(txn.Connection!, schemaName, tableName, rows);
    }

    /// <summary>
    /// The rows go to a temporary table of the session (ADBC ingest), and from there into the target with
    /// <c>INSERT … RETURNING *</c>, read whole; the temporary table is dropped after.
    /// </summary>
    private static IArrowArrayStream InsertReturning(AdbcConnection connection, string schemaName, string tableName,
                                                     IArrowArrayStream rows) =>
        ThroughTemporary(connection, rows, (temp, columns) =>
        {
            using var insert = connection.CreateStatement();
            insert.SqlQuery = "INSERT INTO " + DuckDbDdl.Table(schemaName, tableName) + " (" + columns + ") SELECT "
                              + columns + " FROM " + temp + " RETURNING *";
            return Drained(insert.ExecuteQuery().Stream!);
        });

    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId,
                           IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode,
                           bool partitionOverwrite, string? optionsJson)
    {
        RequireDuckDb("INSERT");
        if (partitionColumns is { Count: > 0 } || sortColumns is { Count: > 0 } || !string.IsNullOrEmpty(schemaMode)
            || partitionOverwrite || !string.IsNullOrEmpty(optionsJson))
            throw new NotSupportedException("adbc: a load with partitions, sorting, a schema mode or WITH options is not supported");
        // the rows arrive in DuckDB's lossless Arrow forms; the driver's ingest maps them back to the same types
        var txn = WriteTransaction(txnId);
        if (txn is null)
        {
            using var own = Connect();
            return Load(own, schemaName, tableName, data, createTable, replace);
        }
        lock (txn.Gate)
            return Load(txn.Connection!, schemaName, tableName, data, createTable, replace);
    }

    private static long Load(AdbcConnection connection, string schemaName, string tableName, IArrowArrayStream data,
                             bool createTable, bool replace)
    {
        if (createTable)
        {
            using var create = connection.CreateStatement();
            create.SqlQuery = DuckDbDdl.CreateTable(schemaName, tableName, data.Schema, ifNotExists: false, replace,
                                                    primaryKey: null, uniques: null);
            create.ExecuteUpdate();
        }
        else
        {
            // the rows may name only some of the target's columns (the rest take their defaults), which an ingest
            // into the table cannot express: they go to a temporary table first, then INSERT … SELECT by name
            return ThroughTemporary(connection, data, (temp, columns) =>
            {
                using var insert = connection.CreateStatement();
                insert.SqlQuery = "INSERT INTO " + DuckDbDdl.Table(schemaName, tableName) + " (" + columns + ") SELECT "
                                  + columns + " FROM " + temp;
                return insert.ExecuteUpdate().AffectedRows;
            });
        }
        using var ingest = connection.CreateStatement();
        ingest.SetOption("adbc.ingest.target_table", tableName);
        if (!string.IsNullOrEmpty(schemaName))
            ingest.SetOption("adbc.ingest.target_db_schema", schemaName);
        ingest.SetOption("adbc.ingest.mode", "adbc.ingest.mode.append");
        ingest.BindStream(data);
        return ingest.ExecuteUpdate().AffectedRows;
    }

    /// <summary>Ingests the rows into a temporary table of the session, runs <paramref name="use"/> with its name and
    /// the quoted column list, and drops it.</summary>
    private static T ThroughTemporary<T>(AdbcConnection connection, IArrowArrayStream rows, Func<string, string, T> use)
    {
        string name = "__fabricator_rows_" + Guid.NewGuid().ToString("N");
        string columns = string.Join(", ", rows.Schema.FieldsList.Select(f => DuckDbDdl.Quote(f.Name)));
        using (var ingest = connection.CreateStatement())
        {
            ingest.SetOption("adbc.ingest.target_table", name);
            ingest.SetOption("adbc.ingest.temporary", "true");
            ingest.SetOption("adbc.ingest.mode", "adbc.ingest.mode.create");
            ingest.BindStream(rows);
            ingest.ExecuteUpdate();
        }
        try
        {
            return use("temp." + DuckDbDdl.Quote(name), columns);
        }
        finally
        {
            using var drop = connection.CreateStatement();
            drop.SqlQuery = "DROP TABLE IF EXISTS temp." + DuckDbDdl.Quote(name);
            drop.ExecuteUpdate();
        }
    }

    // ── the key path: a DELETE / UPDATE the host could not push, by the rows' keys (the table's PRIMARY KEY / UNIQUE) ──

    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys) =>
        KeyDml(schemaName, tableName, keys, setColumnCount: -1, returning: false, (c, sql) =>
        {
            using var statement = c.CreateStatement();
            statement.SqlQuery = sql;
            return statement.ExecuteUpdate().AffectedRows;
        });

    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data) =>
        KeyDml(schemaName, tableName, data, setColumnCount, returning: false, (c, sql) =>
        {
            using var statement = c.CreateStatement();
            statement.SqlQuery = sql;
            return statement.ExecuteUpdate().AffectedRows;
        });

    public IArrowArrayStream ExecuteDeleteReturning(IReadOnlyList<string> schemaPath, string tableName, IArrowArrayStream keys) =>
        KeyDml(Leaf(schemaPath), tableName, keys, setColumnCount: -1, returning: true, Returned);

    public IArrowArrayStream ExecuteUpdateReturning(IReadOnlyList<string> schemaPath, string tableName, int setColumnCount,
                                                    IArrowArrayStream data) =>
        KeyDml(Leaf(schemaPath), tableName, data, setColumnCount, returning: true, Returned);

    private static string Leaf(IReadOnlyList<string> path) => path.Count == 0 ? string.Empty : path[path.Count - 1];

    private static IArrowArrayStream Returned(AdbcConnection connection, string sql)
    {
        using var statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        return Drained(statement.ExecuteQuery().Stream!);
    }

    /// <summary>
    /// A key-path DELETE (<paramref name="setColumnCount"/> &lt; 0: the rows are keys) or UPDATE (the first
    /// <paramref name="setColumnCount"/> columns are the new values, named by the columns they set, the rest the keys):
    /// the rows go to a temporary table of the session under positional names (a key column may also be set), and ONE
    /// statement changes the target's rows whose keys are there — in the DuckDB transaction.
    /// </summary>
    private T KeyDml<T>(string schemaName, string tableName, IArrowArrayStream rows, int setColumnCount, bool returning,
                        Func<AdbcConnection, string, T> run)
    {
        if (!_duckDbServer)
        {
            rows.Dispose();
            throw ReadOnly(setColumnCount < 0 ? "DELETE" : "UPDATE");
        }
        var names = rows.Schema.FieldsList.Select(f => f.Name).ToList();
        var positional = new RenamedStream(rows, Enumerable.Range(0, names.Count).Select(i => "_" + i).ToList());
        string target = DuckDbDdl.Table(schemaName, tableName);
        int firstKey = setColumnCount < 0 ? 0 : setColumnCount;
        T Statement(AdbcConnection connection) => ThroughTemporary(connection, positional, (temp, _) =>
        {
            string match = string.Join(" AND ", Enumerable.Range(firstKey, names.Count - firstKey)
                .Select(i => "T." + DuckDbDdl.Quote(names[i]) + " IS NOT DISTINCT FROM S._" + i));
            string sql = setColumnCount < 0
                ? "DELETE FROM " + target + " AS T WHERE EXISTS (SELECT 1 FROM " + temp + " AS S WHERE " + match + ")"
                : "UPDATE " + target + " AS T SET " + string.Join(", ", Enumerable.Range(0, setColumnCount)
                      .Select(i => DuckDbDdl.Quote(names[i]) + " = S._" + i)) + " FROM " + temp + " AS S WHERE " + match;
            return run(connection, returning ? sql + " RETURNING T.*" : sql);
        });
        var txn = WriteTransaction();
        if (txn is null)
        {
            using var own = Connect();
            return Statement(own);
        }
        lock (txn.Gate)
            return Statement(txn.Connection!);
    }

    /// <summary>A stream whose columns are renamed by position.</summary>
    private sealed class RenamedStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;

        public RenamedStream(IArrowArrayStream inner, IReadOnlyList<string> names)
        {
            _inner = inner;
            Schema = Renamed(inner.Schema, names);
        }

        public Schema Schema { get; }

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            return batch is null ? null : new RecordBatch(Schema, Enumerable.Range(0, batch.ColumnCount).Select(batch.Column), batch.Length);
        }

        public void Dispose() => _inner.Dispose();
    }

    public void Dispose()
    {
        _database.Dispose();
        _driver.Dispose();
    }

    /// <summary>A driver's result stream that owns its statement and connection, and renames the columns.</summary>
    private sealed class OwningStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private readonly AdbcStatement _statement;
        private readonly AdbcConnection? _connection;

        private readonly IReadOnlyList<int> _select;

        /// <param name="connection">The connection to close with the stream; null for a pinned one.</param>
        public OwningStream(IArrowArrayStream inner, IReadOnlyList<string> names, AdbcStatement statement,
                            AdbcConnection? connection, IReadOnlyList<int>? select = null)
        {
            _inner = inner;
            _statement = statement;
            _connection = connection;
            var renamed = Renamed(inner.Schema, names);
            _select = select ?? Enumerable.Range(0, renamed.FieldsList.Count).ToList();
            Schema = new Schema(_select.Select(i => renamed.FieldsList[i]), renamed.Metadata);
        }

        public Schema Schema { get; }

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch is null)
                return null;
            var columns = _select.Select(batch.Column).ToArray();
            return new RecordBatch(Schema, columns, batch.Length);
        }

        public void Dispose()
        {
            _inner.Dispose();
            _statement.Dispose();
            _connection?.Dispose();
        }
    }
}

internal sealed class AdbcTable : ITable
{
    private readonly AdbcCatalog _catalog;

    public AdbcTable(AdbcCatalog catalog, string schemaName, string tableName)
    {
        _catalog = catalog;
        SchemaName = schemaName;
        TableName = tableName;
    }

    public string SchemaName { get; }
    public string TableName { get; }

    public ITableBinding Bind(ITransaction? transaction, TableAt? at = null)
    {
        if (at is not null)
            throw new NotSupportedException("adbc: AT (time travel) is not supported");
        return new AdbcTableBinding(_catalog, this, _catalog.TableSchema(SchemaName, TableName));
    }
}

internal sealed class AdbcTableBinding : ITableBinding
{
    private readonly AdbcCatalog _catalog;
    private readonly AdbcTable _table;

    public AdbcTableBinding(AdbcCatalog catalog, AdbcTable table, Schema schema)
    {
        _catalog = catalog;
        _table = table;
        Schema = schema;
    }

    public Schema Schema { get; }
    /// <summary>
    /// The table's PRIMARY KEY / UNIQUE; for a DuckDB server table with neither, DuckDB's own <c>rowid</c> as a
    /// VIRTUAL row identity (BIGINT, read by the scan — <see cref="Scan"/> pins it to the transaction, so the rows it
    /// names are the rows the DELETE / UPDATE then changes). Not when the table has a column of that name.
    /// </summary>
    public IReadOnlyList<string> RowIdColumns()
    {
        var keys = _catalog.KeyColumns(_table.SchemaName, _table.TableName);
        if (keys.Count > 0 || !_catalog.ReadsByName)
            return keys;
        return Schema.FieldsList.Any(f => string.Equals(f.Name, AdbcCatalog.RowId, StringComparison.OrdinalIgnoreCase))
            ? keys
            : new[] { AdbcCatalog.RowId };
    }
    public IReadOnlyList<VirtualColumn> VirtualColumns() => System.Array.Empty<VirtualColumn>();
    public long? ApproximateRowCount() => _catalog.RowCount(_table.SchemaName, _table.TableName);
    public IReadOnlyList<NdvEntry> ColumnNdv() => System.Array.Empty<NdvEntry>();

    // The projection, and the filters as the read's best-effort filter: DuckDB applies every filter (and the limit)
    // again above the scan, so the driver may leave out what it cannot translate.
    public IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues)
    {
        using var values = AdbcCatalog.ReadValues(filterValues);
        var spec = ScanSpec.Parse(specJson);
        if (spec?.SchemaOnly == true)
        {
            // the bind's describe: the schema is known (GetTableSchema) — running the scan would read the table
            return new InMemoryArrayStream(Schema, System.Array.Empty<RecordBatch>());
        }
        // DuckDB's rowid (the virtual row identity, RowIdColumns): an extra BIGINT column of the read
        bool rowId = _catalog.ReadsByName && spec?.Columns is { } wanted
            && wanted.Any(c => string.Equals(c, AdbcCatalog.RowId, StringComparison.OrdinalIgnoreCase))
            && !Schema.FieldsList.Any(f => string.Equals(f.Name, AdbcCatalog.RowId, StringComparison.OrdinalIgnoreCase));
        var schema = rowId
            ? new Schema(Schema.FieldsList.Append(new Field(AdbcCatalog.RowId, Int64Type.Default, nullable: false)), Schema.Metadata)
            : Schema;
        var projection = spec?.Columns is { Count: > 0 } columns
            ? columns.Select(c => AdbcCatalog.IndexOf(schema.FieldsList.Select(f => f.Name).ToList(), c)
                ?? throw new InvalidOperationException($"adbc: the table has no column '{c}'")).ToList()
            : Enumerable.Range(0, Schema.FieldsList.Count).ToList();
        var columnNames = schema.FieldsList.Select(f => f.Name).ToList();
        var functions = new SubstraitFunctions();
        var conjuncts = spec?.Filter is { } filter
            ? SubstraitFilter.Conjuncts(filter, name => AdbcCatalog.IndexOf(columnNames, name), values, functions, _catalog.MaxInList)
            : new List<ProtoWriter>();
        var path = new[] { _table.SchemaName, _table.TableName };
        // a read of the rowid is for a DELETE / UPDATE by it: in the transaction, so the rowids stay the rows they name
        return _catalog.RunFiltered(condition => SubstraitReadPlan.Build(path, schema, projection, condition, functions,
                                                                          _catalog.ReadsByName),
            conjuncts, functions, projection.Select(i => schema.FieldsList[i].Name).ToList(), pin: rowId);
    }

    public void Dispose()
    {
    }
}
