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

    private sealed record PushedPlan(byte[] Plan, Schema Schema);

    private const string PlanPrefix = "adbc-substrait-plan:";

    /// <summary>
    /// The longest IN list a filter sends as a list (ATTACH option <c>max_in_list</c>, default 1000); a longer one goes
    /// as its range (integers and temporals) or not at all. A driver writes the list into its statement: one that
    /// renders a long list as a relation (as the T-SQL emitter does, with OPENJSON) takes far more than one that writes
    /// literals — and a statement too large for the server fails only when it runs, after Prepare accepted it.
    /// </summary>
    internal int MaxInList { get; } = 1000;

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
    }

    private AdbcConnection Connect()
    {
        lock (_connectGate)
            return _database.Connect(new Dictionary<string, string>());
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
        "{\"remote_select\":true,\"superset_filter_pushdown\":true" + (_defaultSchema is null ? "" : ",\"default_schema\":" +
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
        using var connection = Connect();
        try
        {
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
    internal IArrowArrayStream RunFiltered(Func<ProtoWriter?, byte[]> build, IReadOnlyList<ProtoWriter> conjuncts,
                                           SubstraitFunctions functions, IReadOnlyList<string> names,
                                           IReadOnlyList<int>? select = null)
    {
        var connection = Connect();
        AdbcStatement? statement = null;
        try
        {
            statement = connection.CreateStatement();
            statement.SubstraitPlan = ChoosePlan(statement, build, conjuncts, functions);
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

    private static byte[] ChoosePlan(AdbcStatement statement, Func<ProtoWriter?, byte[]> build,
                                     IReadOnlyList<ProtoWriter> conjuncts, SubstraitFunctions functions)
    {
        if (conjuncts.Count == 0)
            return build(null);
        // Prepare translates the plan without running it (a driver that cannot refuses there)
        bool Translates(byte[] plan)
        {
            try
            {
                statement.SubstraitPlan = plan;
                statement.Prepare();
                return true;
            }
            catch (Exception ex) when (ex is AdbcException or NotImplementedException or NotSupportedException)
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
        Schema schema;
        try
        {
            using var connection = Connect();
            using var statement = connection.CreateStatement();
            statement.SubstraitPlan = substraitPlan;
            statement.Prepare(); // a driver that cannot run the plan refuses here, before anything executes
            schema = statement.ExecuteSchema();
        }
        catch (Exception ex) when (ex is AdbcException or NotImplementedException or NotSupportedException)
        {
            return RemoteQuery.Declined("adbc driver: " + ex.Message);
        }
        string handle = PlanPrefix + Guid.NewGuid().ToString("N");
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

    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                            string? primaryKey, string? uniques, string? defaults,
                            IReadOnlyList<string>? partitionColumns, IReadOnlyList<string>? sortColumns,
                            IReadOnlyList<string>? identityColumns, string? optionsJson) => throw ReadOnly("CREATE TABLE");

    public void DropTable(string schemaName, string tableName, bool ifExists) => throw ReadOnly("DROP TABLE");
    public void CreateSchema(string schemaName, bool ifNotExists) => throw ReadOnly("CREATE SCHEMA");
    public void DropSchema(string schemaName, bool ifExists) => throw ReadOnly("DROP SCHEMA");
    public void AlterTable(AlterTableSpec spec, string schemaName, string tableName, Field? column) => throw ReadOnly("ALTER TABLE");

    public void BeginTransaction(bool isExplicit)
    {
    }

    public void CommitTransaction()
    {
    }

    public void RollbackTransaction()
    {
    }

    public IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows) =>
        throw ReadOnly("INSERT");

    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId,
                           IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode,
                           bool partitionOverwrite, string? optionsJson) => throw ReadOnly("INSERT");

    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys) => throw ReadOnly("DELETE");

    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data) =>
        throw ReadOnly("UPDATE");

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
        private readonly AdbcConnection _connection;

        private readonly IReadOnlyList<int> _select;

        public OwningStream(IArrowArrayStream inner, IReadOnlyList<string> names, AdbcStatement statement,
                            AdbcConnection connection, IReadOnlyList<int>? select = null)
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
            _connection.Dispose();
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
    public IReadOnlyList<string> RowIdColumns() => System.Array.Empty<string>();
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
        var projection = spec?.Columns is { Count: > 0 } columns
            ? columns.Select(c => Schema.GetFieldIndex(c)).ToList()
            : Enumerable.Range(0, Schema.FieldsList.Count).ToList();
        var columnNames = Schema.FieldsList.Select(f => f.Name).ToList();
        var functions = new SubstraitFunctions();
        var conjuncts = spec?.Filter is { } filter
            ? SubstraitFilter.Conjuncts(filter, name => AdbcCatalog.IndexOf(columnNames, name), values, functions, _catalog.MaxInList)
            : new List<ProtoWriter>();
        var path = new[] { _table.SchemaName, _table.TableName };
        return _catalog.RunFiltered(condition => SubstraitReadPlan.Build(path, Schema, projection, condition, functions),
            conjuncts, functions, projection.Select(i => Schema.FieldsList[i].Name).ToList());
    }

    public void Dispose()
    {
    }
}
