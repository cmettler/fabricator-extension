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
    private readonly AdomdConnection _conn;
    private readonly string? _catalog; // the ADOMD database (model db) this connection is bound to
    private readonly string _modelName; // the single Tabular model in this database = the DuckDB schema name

    public DaxCatalog(string connectionString)
    {
        _conn = new AdomdConnection(connectionString);
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
        // engine's result-column types; GetSchemaTable gives name + CLR type + nullability.
        var fields = new List<Field>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"EVALUATE TOPN(0, {QuoteDaxTable(table)})";
            using var r = cmd.ExecuteReader();
            var st = r.GetSchemaTable();
            foreach (System.Data.DataRow row in st!.Rows)
            {
                var rawName = (string)row["ColumnName"];
                var clr = (Type)row["DataType"];
                bool nullable = row["AllowDBNull"] is not bool b || b;
                int? precision = row.Table.Columns.Contains("NumericPrecision") && row["NumericPrecision"] is int p ? p : null;
                int? scale = row.Table.Columns.Contains("NumericScale") && row["NumericScale"] is int s ? s : null;
                fields.Add(new Field(DaxTypeMap.DebracketColumn(rawName), DaxTypeMap.MapClr(clr, precision, scale), nullable));
            }
        }
        return new InMemoryArrayStream(new Schema(fields, null), System.Array.Empty<RecordBatch>());
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

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
        => throw new NotSupportedException("dax provider: table scan lands in slice 3.");

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
