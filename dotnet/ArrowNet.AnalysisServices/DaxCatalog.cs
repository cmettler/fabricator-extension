using System.Data;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using ArrowNet.Bridge;
using Microsoft.AnalysisServices.AdomdClient;

namespace ArrowNet.AnalysisServices;

/// <summary>
/// An opened ADOMD connection to one semantic-model database. Slice 1: connects, sets the current catalog
/// (model database), and serves metadata so <c>ATTACH</c> validates — schemas = the model name(s) from the
/// <c>$SYSTEM.TMSCHEMA_MODEL</c> DMV. Table/column discovery, scans, and the DAX functions land in later
/// slices (the read/scan/function methods throw <see cref="NotSupportedException"/> for now). See
/// docs/dax-provider.md.
/// </summary>
internal sealed class DaxCatalog : IBackendCatalog
{
    private readonly string _connectionString;
    private readonly AdomdConnection _conn;
    private readonly string? _catalog; // the ADOMD database (model db) this connection is bound to
    private readonly string _modelName; // the single Tabular model in this database = the DuckDB schema name

    public DaxCatalog(string connectionString)
    {
        _connectionString = connectionString;
        _conn = new AdomdConnection(_connectionString);
        _conn.Open();
        _catalog = DiscoverDefaultCatalog(_conn);
        if (_catalog != null)
        {
            _conn.ChangeDatabase(_catalog);
        }
        _modelName = DiscoverModelNames()[0];
    }


    // ---- metadata --------------------------------------------------------------------------------------

    // The DuckDB schema that exposes the curated VertiPaq/$SYSTEM DMVs as tables, in the same catalog.
    private const string SystemSchema = "system";

    // Curated $SYSTEM DMVs surfaced under the "system" schema. Each is queryable with a bare
    // `SELECT * FROM $SYSTEM.<name>` (the DMV SQL is very limited; DuckDB applies projection/filter above the
    // scan — no pushdown). Only DMVs that work WITHOUT a restriction WHERE belong here; extend as needed.
    private static readonly string[] SystemTables =
    {
        "TMSCHEMA_MODEL", "TMSCHEMA_TABLES", "TMSCHEMA_COLUMNS", "TMSCHEMA_MEASURES",
        "TMSCHEMA_RELATIONSHIPS", "TMSCHEMA_PARTITIONS", "TMSCHEMA_HIERARCHIES",
        "DBSCHEMA_TABLES", "DBSCHEMA_COLUMNS",
        "DISCOVER_STORAGE_TABLES", "DISCOVER_STORAGE_TABLE_COLUMNS",
        "DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS", "DISCOVER_CALC_DEPENDENCY",
        "DISCOVER_OBJECT_MEMORY_USAGE",
    };

    private static bool IsSystem(string? schema) =>
        string.Equals(schema, SystemSchema, StringComparison.OrdinalIgnoreCase);

    // Resolves a system table name to its $SYSTEM DMV (validated against the curated list — DuckDB only scans
    // tables we declared, but guard defensively so a stray name can't reach the DMV endpoint).
    private static string SystemDmv(string table)
    {
        foreach (var t in SystemTables)
        {
            if (string.Equals(t, table, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }
        throw new NotSupportedException($"dax provider: unknown system table '{table}'");
    }

    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        // Schemas = the model name(s) + the "system" schema (curated $SYSTEM DMVs).
        MetadataKind.Schemas => SingleColumn("schema_name", new[] { _modelName, SystemSchema }),
        // Tables = the model's tables (TMSCHEMA_TABLES) + the curated system DMVs.
        MetadataKind.Tables => DiscoverTables(),
        // Columns = a zero-row stream whose SCHEMA describes the table's columns (the no-describe approach;
        // real engine types). Model: EVALUATE TOPN(0,'T'); system: bare SELECT * FROM $SYSTEM.<dmv>.
        MetadataKind.Columns => DiscoverColumns(schema, table!),
        // Functions (under the model schema): daxeval(expression) — table function evaluating an arbitrary
        // DAX query; daxevaltable(<input>, expression := …) — in-out, injects the input table as a DAX
        // DATATABLE the expression references (slice 5).
        MetadataKind.Functions => ThreeColumn(
            "schema_name", new[] { _modelName, _modelName, _modelName },
            "name", new[] { DaxEvalName, DaxEvalTableName, DaxEachName },
            // daxeval is a 'proc' (not 'table') so its args register as NAMED parameters — it takes an
            // optional `params` JSON arg alongside `expression`, which a positional table fn can't express.
            "kind", new[] { "proc", "inout", "inout" }),
        MetadataKind.ServerInfo => EmptyStringTable("property", "value"),
        _ => EmptyStringTable("name"),
    };

    private IArrowArrayStream DiscoverTables()
    {
        var schemaCol = new List<string>();
        var nameCol = new List<string>();
        var typeCol = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT [Name] FROM $SYSTEM.TMSCHEMA_TABLES";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.IsDBNull(0) ? null : r.GetValue(0)?.ToString();
                if (string.IsNullOrEmpty(name) || IsSystemDateTable(name!))
                {
                    continue; // skip Power BI's auto-generated date tables (noise)
                }
                schemaCol.Add(_modelName);
                nameCol.Add(name!);
                typeCol.Add("BASE TABLE");
            }
        }
        // Curated $SYSTEM DMVs under the "system" schema (same catalog).
        foreach (var sysTable in SystemTables)
        {
            schemaCol.Add(SystemSchema);
            nameCol.Add(sysTable);
            typeCol.Add("SYSTEM TABLE");
        }
        return ThreeColumn("schema_name", schemaCol, "table_name", nameCol, "table_type", typeCol);
    }

    private static bool IsSystemDateTable(string name)
        => name.StartsWith("LocalDateTable_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DateTableTemplate_", StringComparison.OrdinalIgnoreCase);

    private IArrowArrayStream DiscoverColumns(string? schema, string table)
    {
        // Model: EVALUATE TOPN(0,'T') returns the data columns (no internal RowNumber) with engine types.
        // System: a bare SELECT * FROM $SYSTEM.<dmv> — GetSchemaTable reads NO rows, so it's cheap even
        // without a TOPN-style cap (the DMV SQL has no TOPN/EVALUATE). The schema table gives name + CLR type.
        string cmdText = IsSystem(schema)
            ? $"SELECT * FROM $SYSTEM.{SystemDmv(table)}"
            : $"EVALUATE TOPN(0, {QuoteDaxTable(table)})";
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = cmdText;
        using var r = cmd.ExecuteReader();
        var arrowSchema = ArrowSchemaFromReader(r);
        return new InMemoryArrayStream(arrowSchema, System.Array.Empty<RecordBatch>());
    }

    /// <summary>Builds the Arrow schema for a DAX result set from the reader's schema table — de-bracketed
    /// column names + <see cref="DaxTypeMap"/> types. Shared by column discovery and table scans so a scan's
    /// column names/types match what was discovered.</summary>
    internal static Schema ArrowSchemaFromReader(IDataReader reader)
    {
        var fields = new List<Field>();
        var st = reader.GetSchemaTable();
        bool hasPrecision = st!.Columns.Contains("NumericPrecision");
        bool hasScale = st.Columns.Contains("NumericScale");
        foreach (System.Data.DataRow row in st.Rows)
        {
            var rawName = (string)row["ColumnName"];
            var clr = (Type)row["DataType"];
            bool nullable = row["AllowDBNull"] is not bool b || b;
            int? precision = hasPrecision && row["NumericPrecision"] is int p ? p : null;
            int? scale = hasScale && row["NumericScale"] is int s ? s : null;
            fields.Add(new Field(DaxTypeMap.DebracketColumn(rawName), DaxTypeMap.MapClr(clr, precision, scale), nullable));
        }
        return new Schema(fields, null);
    }

    /// <summary>Quotes a table name for DAX (single quotes; embedded quotes doubled).</summary>
    internal static string QuoteDaxTable(string table) => "'" + table.Replace("'", "''") + "'";

    private List<string> DiscoverModelNames()
    {
        var names = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT [Name] FROM $SYSTEM.TMSCHEMA_MODEL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.IsDBNull(0) ? null : r.GetValue(0)?.ToString();
            names.Add(string.IsNullOrEmpty(name) ? "Model" : name!);
        }
        if (names.Count == 0)
        {
            names.Add("Model"); // a model with no TMSCHEMA_MODEL row still gets a usable schema name
        }
        return names;
    }

    private static string? DiscoverDefaultCatalog(AdomdConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.IsDBNull(0) ? null : r.GetValue(0)?.ToString()) : null;
    }

    // ---- read / scan (later slices) -------------------------------------------------------------------

    public IArrowArrayStream ExecuteQuery(string sql)
        => throw new NotSupportedException("dax provider: raw query not supported yet (slice 1).");

    /// <summary>
    /// Scans a model table: projects the requested columns via <c>EVALUATE SELECTCOLUMNS('T', "Col",
    /// 'T'[Col], …)</c> (no projection => <c>EVALUATE 'T'</c>), runs it on a fresh connection, and streams
    /// the <see cref="AdomdDataReader"/> as Arrow. Filter pushdown is not done — DAX has no general SQL WHERE
    /// here — so any pushed filter is ignored and DuckDB re-applies it (never-erase: a superset is safe).
    /// </summary>
    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        // $SYSTEM DMVs: a bare SELECT (the DMV SQL is very limited; no pushdown — DuckDB projects/filters
        // above the scan). Ignore + dispose any pushed filter values.
        if (IsSystem(schemaName))
        {
            filterValues?.Dispose();
            return ScanTableCore($"SELECT * FROM $SYSTEM.{SystemDmv(tableName)}");
        }

        var spec = ScanSpec.Parse(specJson);
        string tableRef = QuoteDaxTable(tableName);

        // Filter pushdown (best-effort, superset-safe — DuckDB re-applies every predicate). Wrap the table in
        // FILTER('T', <pred>); VertiPaq pushes the storage-engine-friendly parts down and iterates the rest.
        // If nothing safe is pushable (or anything fails), scan unfiltered and let DuckDB filter.
        string tableExpr = tableRef;
        if (spec?.Filter is not null && filterValues is not null)
        {
            try
            {
                var values = ReadFilterValues(filterValues);
                filterValues = null; // consumed + disposed by ReadFilterValues
                var pred = new DaxFilterBuilder(values, tableRef).Render(spec.Filter);
                if (!string.IsNullOrEmpty(pred))
                {
                    tableExpr = $"FILTER({tableRef}, {pred})";
                }
            }
            catch
            {
                // Fall through to an unfiltered scan; correctness preserved by DuckDB.
            }
        }
        filterValues?.Dispose(); // dispose if not consumed above (host hands us ownership)

        // Projection (absent/empty => whole table). Column refs stay qualified by the base table so they
        // resolve in FILTER's row context.
        string innerExpr = spec?.Columns is { Count: > 0 } cols
            ? $"SELECTCOLUMNS({tableExpr}, " +
              string.Join(", ", cols.Select(c =>
                  $"\"{c.Replace("\"", "\"\"")}\", {tableRef}[{c}]")) + ")"
            : tableExpr;
        return ScanTableCore($"EVALUATE {innerExpr}");
    }

    // Reads the one-row filter value batch (column i == value i) into CLR values; disposes the stream.
    private static List<object?> ReadFilterValues(IArrowArrayStream stream)
    {
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

    /// <summary>
    /// Lazy, true-streaming scan (one batch per host pull, ≤1 batch buffered) — open one connection, one
    /// ExecuteReader, hand the open <see cref="AdomdDataReader"/> to <see cref="DaxArrowStream"/>. The schema
    /// comes from the data reader itself (one query per connection). Streams arbitrarily large results
    /// (validated to 10.5M rows). See <see cref="DaxArrowStream"/> for the one subtlety that makes it work:
    /// never call <c>Read()</c> past end-of-data (ADOMD throws on it).
    /// </summary>
    private IArrowArrayStream ScanTableCore(string commandText) => StreamCommand(commandText, null);

    /// <summary>Opens a fresh ADOMD connection bound to this catalog's model database.</summary>
    internal AdomdConnection OpenConnection()
    {
        var conn = new AdomdConnection(_connectionString);
        conn.Open();
        if (_catalog != null)
        {
            conn.ChangeDatabase(_catalog);
        }
        return conn;
    }

    /// <summary>Runs <paramref name="commandText"/> (DAX or a $SYSTEM DMV SELECT) on a fresh connection and
    /// returns its rows as a lazy Arrow stream (the stream owns conn/cmd/reader). The output schema is taken
    /// from <paramref name="knownSchema"/> when given (e.g. resolved at bind), else from the data reader.</summary>
    internal IArrowArrayStream StreamCommand(string commandText, Schema? knownSchema,
                                             IReadOnlyList<KeyValuePair<string, object?>>? daxParams = null)
    {
        var conn = OpenConnection();
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = commandText;
            BindDaxParams(cmd, daxParams);
            var reader = cmd.ExecuteReader();
            var schema = knownSchema ?? ArrowSchemaFromReader(reader);
            return new DaxArrowStream(conn, cmd, reader, schema, batchSize: 2048);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Resolves a command's output schema without fetching rows (ExecuteReader + GetSchemaTable) —
    /// the no-describe approach used to bind a daxeval call's output columns.</summary>
    internal Schema ProbeSchema(string commandText,
                                IReadOnlyList<KeyValuePair<string, object?>>? daxParams = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        BindDaxParams(cmd, daxParams);
        using var reader = cmd.ExecuteReader();
        return ArrowSchemaFromReader(reader);
    }

    // ---- functions -----------------------------------------------------------------------------------
    // daxeval(expression) — a table function that evaluates an arbitrary DAX query (a complete EVALUATE /
    // DEFINE…EVALUATE statement) against the model and returns its result table. ADOMD has no result-set
    // describe, so the output schema is resolved at bind by executing the query + reading GetSchemaTable (no
    // rows fetched); the scan re-executes + streams. Registered under the model schema.

    private const string DaxEvalName = "daxeval";
    private const string DaxEvalTableName = "daxevaltable";
    private const string DaxEachName = "daxeach";

    private static bool IsDaxEval(string functionName) =>
        string.Equals(functionName, DaxEvalName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDaxEvalTable(string functionName) =>
        string.Equals(functionName, DaxEvalTableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDaxEach(string functionName) =>
        string.Equals(functionName, DaxEachName, StringComparison.OrdinalIgnoreCase);

    // Reads the named arg <paramref name="name"/> (case-insensitive) from the 1-row args batch, or null if
    // absent. Args arrive as a batch whose columns are the SUPPLIED named parameters (order not guaranteed),
    // so read by field name rather than position.
    private static object? ReadArgByName(RecordBatch? args, string name)
    {
        if (args is null)
        {
            return null;
        }
        var fields = args.Schema.FieldsList;
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return ArrowValueReader.ReadScalar(args.Column(i), 0);
            }
        }
        return null;
    }

    private static string DaxEvalExpression(RecordBatch? args)
    {
        var expr = ReadArgByName(args, "expression")?.ToString();
        if (string.IsNullOrWhiteSpace(expr))
        {
            throw new ArgumentException(
                "dax provider: a non-empty 'expression' is required, e.g. daxeval(expression := 'EVALUATE …')");
        }
        return expr!;
    }

    // daxeval's optional `params` arg = a JSON object of DAX parameter values; each becomes an ADOMD
    // parameter the expression references as @<name> (e.g. params := '{"p": 5, "q": "x"}' -> @p, @q).
    private static IReadOnlyList<KeyValuePair<string, object?>> DaxParams(RecordBatch? args)
        => ParseDaxParams(ReadArgByName(args, "params")?.ToString());

    private static IReadOnlyList<KeyValuePair<string, object?>> ParseDaxParams(string? json)
    {
        var result = new List<KeyValuePair<string, object?>>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new ArgumentException("dax provider: daxeval 'params' must be a JSON object, e.g. '{\"p\": 5}'");
        }
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            result.Add(new KeyValuePair<string, object?>(p.Name, JsonScalar(p.Value)));
        }
        return result;
    }

    private static object? JsonScalar(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        System.Text.Json.JsonValueKind.String => e.GetString(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => null,
        _ => e.GetRawText(), // array/object — pass the raw JSON text (unusual for a DAX scalar param)
    };

    private static void BindDaxParams(AdomdCommand cmd, IReadOnlyList<KeyValuePair<string, object?>>? daxParams)
    {
        if (daxParams is null)
        {
            return;
        }
        foreach (var kv in daxParams)
        {
            cmd.Parameters.Add(new AdomdParameter(kv.Key, kv.Value ?? DBNull.Value));
        }
    }

    public Schema GetFunctionParamSchema(string schemaName, string functionName)
    {
        // daxeval takes `expression` (required) + an optional `params` JSON arg — both NAMED parameters
        // (daxeval registers as a 'proc'). daxevaltable / daxeach take only `expression` (a named param
        // that coexists with their {TABLE} input): daxevaltable(<input>, expression := '…').
        if (IsDaxEval(functionName))
        {
            return new Schema(
                new[]
                {
                    new Field("expression", StringType.Default, nullable: false),
                    new Field("params", StringType.Default, nullable: true),
                }, null);
        }
        if (IsDaxEvalTable(functionName) || IsDaxEach(functionName))
        {
            return new Schema(new[] { new Field("expression", StringType.Default, nullable: false) }, null);
        }
        throw new NotSupportedException($"dax provider: unknown function '{functionName}'");
    }

    public Schema GetFunctionOutputSchema(string schemaName, string functionName, RecordBatch? args = null)
    {
        if (IsDaxEval(functionName))
        {
            // arg-dependent: the DAX result's columns (resolved with any params bound).
            return ProbeSchema(DaxEvalExpression(args), DaxParams(args));
        }
        throw new NotSupportedException($"dax provider: unknown function '{functionName}'");
    }

    public IBoundTable TableBind(string schemaName, string functionName, RecordBatch? args)
    {
        if (IsDaxEval(functionName))
        {
            return new DaxEvalBoundTable(this, DaxEvalExpression(args), DaxParams(args));
        }
        throw new NotSupportedException($"dax provider: unknown table function '{functionName}'");
    }

    public Schema GetFunctionReturnSchema(string schemaName, string functionName)
        => throw new NotSupportedException("dax provider: no scalar functions.");

    public IArrowArrayStream ExecuteScalar(string schemaName, string functionName, IArrowArrayStream args)
        => throw new NotSupportedException("dax provider: no scalar functions.");

    public IArrowInOutBinding InOutBind(string schemaName, string functionName, RecordBatch? args, Schema inputSchema)
    {
        // args carries the named "expression" param (1-row, column 0); inputSchema = the input table.
        if (IsDaxEvalTable(functionName))
        {
            return new DaxEvalTableBinding(this, DaxEvalExpression(args), inputSchema);
        }
        if (IsDaxEach(functionName))
        {
            return new DaxEachBinding(this, DaxEvalExpression(args), inputSchema);
        }
        throw new NotSupportedException($"dax provider: unknown table-in-out function '{functionName}'");
    }

    /// <summary>A bound <c>daxeval(expression)</c> call: the output schema is resolved once (at bind, via a
    /// row-less GetSchemaTable probe), and each <see cref="Execute"/> re-runs the DAX and streams the result.
    /// No pushdown (an arbitrary DAX query can't be wrapped) — DuckDB projects/filters above the scan.</summary>
    private sealed class DaxEvalBoundTable : IBoundTable
    {
        private readonly DaxCatalog _catalog;
        private readonly string _dax;
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _params;

        public DaxEvalBoundTable(DaxCatalog catalog, string dax,
                                 IReadOnlyList<KeyValuePair<string, object?>> daxParams)
        {
            _catalog = catalog;
            _dax = dax;
            _params = daxParams;
            OutputSchema = catalog.ProbeSchema(dax, daxParams);
        }

        public Schema OutputSchema { get; }
        public bool SupportsPushdown => false;

        public IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues)
        {
            filterValues?.Dispose(); // no pushdown — DuckDB re-applies; just release the (usually null) batch
            return _catalog.StreamCommand(_dax, OutputSchema, _params);
        }

        public void Dispose() { }
    }

    public IAggregateSession AggOpen(string schemaName, string functionName)
        => throw new NotSupportedException("dax provider: no aggregate functions.");

    // ---- write paths: read-only provider --------------------------------------------------------------

    public long ExecuteNonQuery(string sql) => throw ReadOnly();
    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId) => throw ReadOnly();
    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys) => throw ReadOnly();
    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data) => throw ReadOnly();
    public IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows) => throw ReadOnly();
    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                            string? primaryKey, string? uniques, string? defaults) => throw ReadOnly();
    public void DropTable(string schemaName, string tableName, bool ifExists) => throw ReadOnly();
    public void CreateSchema(string schemaName, bool ifNotExists) => throw ReadOnly();
    public void DropSchema(string schemaName, bool ifExists) => throw ReadOnly();
    public void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2,
                           Field? column, int flags) => throw ReadOnly();

    // Transactions: ADOMD/DAX is read-only — accept BEGIN/COMMIT/ROLLBACK as no-ops so a wrapping
    // DuckDB transaction over read-only DAX queries doesn't fail.
    public void BeginTransaction() { }
    public void CommitTransaction() { }
    public void RollbackTransaction() { }

    public void Dispose()
    {
        try { _conn.Dispose(); } catch { /* best-effort close */ }
    }

    private static NotSupportedException ReadOnly()
        => new("dax provider is read-only: semantic models cannot be modified through DAX.");

    // ---- Arrow helpers ---------------------------------------------------------------------------------

    private static IArrowArrayStream SingleColumn(string name, IReadOnlyList<string> values)
    {
        var schema = new Schema(new[] { new Field(name, StringType.Default, nullable: true) }, null);
        var builder = new StringArray.Builder();
        foreach (var v in values)
        {
            builder.Append(v);
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { builder.Build() }, values.Count);
        return new InMemoryArrayStream(schema, new[] { batch });
    }

    private static IArrowArrayStream ThreeColumn(string n0, IReadOnlyList<string> c0, string n1,
                                                 IReadOnlyList<string> c1, string n2, IReadOnlyList<string> c2)
    {
        var schema = new Schema(new[]
        {
            new Field(n0, StringType.Default, nullable: true),
            new Field(n1, StringType.Default, nullable: true),
            new Field(n2, StringType.Default, nullable: true),
        }, null);
        IArrowArray Build(IReadOnlyList<string> vals)
        {
            var b = new StringArray.Builder();
            foreach (var v in vals) b.Append(v);
            return b.Build();
        }
        var batch = new RecordBatch(schema, new[] { Build(c0), Build(c1), Build(c2) }, c0.Count);
        return new InMemoryArrayStream(schema, new[] { batch });
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
}
