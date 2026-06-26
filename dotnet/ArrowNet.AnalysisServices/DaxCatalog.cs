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

    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        // Schemas = the model name(s) in this database (usually one for Power BI Desktop).
        MetadataKind.Schemas => SingleColumn("schema_name", new[] { _modelName }),
        // Tables = the model's tables (TMSCHEMA_TABLES), all under the single model = schema.
        MetadataKind.Tables => DiscoverTables(),
        // Columns = a zero-row stream whose SCHEMA describes the table's columns, resolved by running a
        // zero-row DAX query and reading its schema table (the no-describe approach; real engine types).
        MetadataKind.Columns => DiscoverColumns(table!),
        // Functions / server-info: empty (valid); functions are slice 4.
        MetadataKind.Functions => EmptyStringTable("schema_name", "name", "kind"),
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
        return ThreeColumn("schema_name", schemaCol, "table_name", nameCol, "table_type", typeCol);
    }

    private static bool IsSystemDateTable(string name)
        => name.StartsWith("LocalDateTable_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DateTableTemplate_", StringComparison.OrdinalIgnoreCase);

    private IArrowArrayStream DiscoverColumns(string table)
    {
        // EVALUATE TOPN(0, 'Table') returns the table's data columns (no internal RowNumber) with the
        // engine's result-column types; the schema table gives name + CLR type + nullability.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"EVALUATE TOPN(0, {QuoteDaxTable(table)})";
        using var r = cmd.ExecuteReader();
        var schema = ArrowSchemaFromReader(r);
        return new InMemoryArrayStream(schema, System.Array.Empty<RecordBatch>());
    }

    /// <summary>Builds the Arrow schema for a DAX result set from the reader's schema table — de-bracketed
    /// column names + <see cref="DaxTypeMap"/> types. Shared by column discovery and table scans so a scan's
    /// column names/types match what was discovered.</summary>
    private static Schema ArrowSchemaFromReader(IDataReader reader)
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
        return ScanTableCore(innerExpr);
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
    private IArrowArrayStream ScanTableCore(string innerExpr)
    {
        var conn = new AdomdConnection(_connectionString);
        try
        {
            conn.Open();
            if (_catalog != null)
            {
                conn.ChangeDatabase(_catalog);
            }
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"EVALUATE {innerExpr}";
            var reader = cmd.ExecuteReader();
            var schema = ArrowSchemaFromReader(reader);
            // The stream owns conn/cmd/reader and disposes them at scan teardown.
            return new DaxArrowStream(conn, cmd, reader, schema, batchSize: 2048);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    // ---- functions (later slices) ---------------------------------------------------------------------

    public Schema GetFunctionParamSchema(string schemaName, string functionName)
        => throw new NotSupportedException("dax provider: functions land in slice 4.");

    public Schema GetFunctionReturnSchema(string schemaName, string functionName)
        => throw new NotSupportedException("dax provider: functions land in slice 4.");

    public IArrowArrayStream ExecuteScalar(string schemaName, string functionName, IArrowArrayStream args)
        => throw new NotSupportedException("dax provider: functions land in slice 4.");

    public Schema GetFunctionOutputSchema(string schemaName, string functionName, RecordBatch? args = null)
        => throw new NotSupportedException("dax provider: functions land in slice 4.");

    public IBoundTable TableBind(string schemaName, string functionName, RecordBatch? args)
        => throw new NotSupportedException("dax provider: table functions land in slice 4.");

    public IArrowInOutBinding InOutBind(string schemaName, string functionName, RecordBatch? args, Schema inputSchema)
        => throw new NotSupportedException("dax provider: table-in-out functions land in slice 5.");

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
