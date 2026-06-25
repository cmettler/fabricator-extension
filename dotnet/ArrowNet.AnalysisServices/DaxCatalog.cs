using System.Data;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using ArrowNet.Bridge;
using ArrowNet.Bridge.Conversion;
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
        var projection = ParseProjection(specJson);
        // Inner table expression (no leading EVALUATE) so we can both probe its schema and fetch its data.
        string innerExpr = projection is { Count: > 0 }
            ? $"SELECTCOLUMNS({QuoteDaxTable(tableName)}, " +
              string.Join(", ", projection.Select(c =>
                  $"\"{c.Replace("\"", "\"\"")}\", {QuoteDaxTable(tableName)}[{c}]")) + ")"
            : QuoteDaxTable(tableName);
        return ScanTableCore(innerExpr);
    }

    /// <summary>
    /// Streams a DAX result lazily as Arrow (at most one batch buffered). The schema (with exact numeric
    /// precision/scale) is resolved from a separate zero-row probe (<c>EVALUATE TOPN(0, …)</c>); the data
    /// reader is then streamed via <see cref="DaxArrowStream"/> without ever calling <c>GetSchemaTable</c> on
    /// it. The host pulls batches on demand and disposes the stream (and its connection) at scan teardown.
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

            // Schema (exact types incl. decimal precision/scale) from a zero-row probe.
            Schema schema;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = $"EVALUATE TOPN(0, {innerExpr})";
                using var pr = probe.ExecuteReader();
                schema = ArrowSchemaFromReader(pr);
            }

            // Data via AdomdDataAdapter.Fill — a single NON-CHUNKED fetch of the whole rowset, materialized
            // into a DataTable. We deliberately do NOT use the streaming AdomdDataReader: its chunked-rowset
            // protocol (the response is split into ~2048-row chunks the client reads on demand) is unreliable
            // under the in-process CoreCLR host — reading the 2nd chunk raises AdomdUnknownResponseException
            // ("the server sent an unrecognizable response"). Extensive investigation (docs/dax-provider.md)
            // ruled out thread affinity, GC, pause-between-chunks, and query count individually, with
            // contradictory results — i.e. the chunked reader is fragile here, while Fill (one whole-rowset
            // response, no chunk continuations) is reliable. Trade-off: full materialization per scan (fine
            // for typically-aggregated DAX results; true incremental streaming needs an out-of-process
            // reader — the Airport model).
            var table = new System.Data.DataTable();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"EVALUATE {innerExpr}";
                using var adapter = new AdomdDataAdapter((AdomdCommand)cmd);
                adapter.Fill(table);
            }
            return new InMemoryArrayStream(schema, BuildBatches(table, schema, batchSize: 2048));
        }
        finally
        {
            conn.Dispose();
        }
    }

    /// <summary>Builds Arrow record batches from a fully-materialized <see cref="System.Data.DataTable"/>
    /// using the probed <paramref name="schema"/> (column order matches the DataTable).</summary>
    private static List<RecordBatch> BuildBatches(System.Data.DataTable table, Schema schema, int batchSize)
    {
        var batches = new List<RecordBatch>();
        int ncols = schema.FieldsList.Count;
        int total = table.Rows.Count;
        for (int start = 0; start < total; start += batchSize)
        {
            int count = Math.Min(batchSize, total - start);
            var appenders = new ColumnAppender[ncols];
            for (int c = 0; c < ncols; c++)
            {
                appenders[c] = ColumnAppender.Create(schema.FieldsList[c].DataType);
            }
            for (int r = 0; r < count; r++)
            {
                var row = table.Rows[start + r];
                for (int c = 0; c < ncols; c++)
                {
                    var v = row[c];
                    if (v is null or DBNull) { appenders[c].AppendNull(); } else { appenders[c].Append(v); }
                }
            }
            var arrays = new IArrowArray[ncols];
            for (int c = 0; c < ncols; c++)
            {
                arrays[c] = appenders[c].Build();
            }
            batches.Add(new RecordBatch(schema, arrays, count));
        }
        return batches;
    }

    /// <summary>Extracts the projected DuckDB column names from the scan spec (<c>{"columns":[...]}</c>);
    /// null/absent => full table.</summary>
    private static List<string>? ParseProjection(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
        {
            return null;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(specJson);
        if (!doc.RootElement.TryGetProperty("columns", out var cols) ||
            cols.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<string>();
        foreach (var c in cols.EnumerateArray())
        {
            var name = c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() : null;
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name!);
            }
        }
        return result.Count > 0 ? result : null;
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
