using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;

namespace ArrowNet.Bridge;

/// <summary>
/// The Delta Lake provider backed by <b>engineered-wood</b> (the 3rd <see cref="IBackend"/>, after SQL Server
/// and DAX): a Delta <b>folder</b> is an ATTACH-able catalog root —
/// <c>ATTACH '/lake' AS lake (TYPE arrownet, PROVIDER 'engineeredwooddelta')</c> (or an <c>abfss://…</c>
/// OneLake/ADLS prefix). The provider name is <c>engineeredwooddelta</c> to distinguish it from a future
/// delta-rs/delta-kernel-backed provider; <c>delta</c> and <c>deltalake</c> remain aliases. Tables = subdirs
/// with a <c>_delta_log/</c> (flat <c>main</c> schema, or per-lakehouse schemas on a schema-enabled OneLake
/// lakehouse). Connection-free: all IO goes through DuckDB's FileSystem via the host callbacks (local / az:// /
/// s3:// + DuckDB secrets), reusing <see cref="DeltaReader"/>. Full read + write DML — CREATE/INSERT/CTAS/COPY/
/// DROP, DELETE (copy-on-write or opt-in deletion vectors), UPDATE (copy-on-write), OCC retry for concurrent
/// writers — all reuse the provider-agnostic C++ catalog machinery. See docs/delta-catalog.md.
/// </summary>
public sealed class DeltaBackend : IBackend
{
    public string Name => "engineeredwooddelta";

    // `delta`/`deltalake` stay as aliases so existing ATTACHes keep working; the primary name distinguishes
    // this engineered-wood-backed provider from a future delta-rs production provider.
    public IEnumerable<string> Aliases => new[] { "delta", "deltalake" };

    // The connstr IS the folder root. Data-file IO is via DuckDB FS secrets (the opener). An azure SP secret on
    // a OneLake ATTACH additionally authenticates the Fabric REST API used to list tables (the glob bug
    // workaround) — carry its fields to the catalog as a credential marker on the root (mirrors the DAX provider).
    public string BuildConnectionString(
        string secretType, IReadOnlyDictionary<string, string> fields, string baseConnString)
    {
        if (secretType.Equals("azure", System.StringComparison.OrdinalIgnoreCase)
            && FabricLakehouse.IsOneLake(baseConnString))
        {
            return FabricLakehouse.AppendCredMarker(baseConnString, fields);
        }
        return baseConnString;
    }

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        new DeltaCatalog(connectionString, optionsJson);
}

/// <summary>An ATTACH'd Delta folder catalog. Lazy: holds the root path; all FS access happens during metadata
/// discovery / scan, using the active host-FS opener (<see cref="AmbientOpener"/>, set by the host before each
/// catalog metadata + scan + bulk-write call).</summary>
public sealed class DeltaCatalog : IBackendCatalog
{
    internal const string MainSchema = "main";
    // The stable row-tracking id surfaced as the DuckDB rowid for UPDATE/DELETE (a VIRTUAL column — not part
    // of the user schema). Matches EngineeredWood.DeltaLake.RowTracking.RowTrackingConfig.VirtualRowIdColumn.
    private const string RowIdColumn = "_metadata.row_id";
    // Transient rowid packing — MUST match engineered-wood's DeltaTable.RowIdPositionBits: (fileOrdinal << 40) |
    // rowPositionInFile. Used to recompute a row's rowid during the per-file UPDATE rewrite.
    private const int RowIdPositionBits = 40;
    private readonly string _root; // normalized (forward slashes), no trailing slash
    // For a OneLake root: the Fabric REST API credential (from the ATTACH'd azure SP secret) used to list
    // tables (and, for a schema-enabled lakehouse, an Entra SQL token). Null for local/S3/ADLS (glob discovery)
    // or when no secret was supplied.
    private readonly Azure.Core.TokenCredential? _fabricCredential;
    // Lazily-resolved OneLake shape (schema-enabled flag + discovered tables); null for non-OneLake roots.
    private FabricLakehouse.OneLakeInfo? _oneLake;
    private bool _oneLakeResolved;
    // ATTACH option `deletion_vectors true`: tables CREATED in this catalog enable DV + row tracking (so their
    // DELETEs use deletion vectors). DELETE on ANY table still follows that table's own delta.enableDeletionVectors
    // config, so external DV tables are honored regardless of this flag.
    private readonly bool _deletionVectorsOnCreate;

    public DeltaCatalog(string root) : this(root, "{}") { }

    public DeltaCatalog(string root, string? optionsJson)
    {
        var (clean, credential) = FabricLakehouse.Extract(root);
        _root = Normalize(clean).TrimEnd('/');
        _fabricCredential = credential;
        _deletionVectorsOnCreate = ParseBoolOption(optionsJson, "deletion_vectors");
    }

    private static bool ParseBoolOption(string? optionsJson, string key)
    {
        if (string.IsNullOrEmpty(optionsJson))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var el))
            {
                var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                return string.Equals(s, "true", System.StringComparison.OrdinalIgnoreCase) || s == "1";
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private static string Normalize(string p) => p.Replace('\\', '/');

    /// <summary>Resolves (once) the OneLake lakehouse shape via the Fabric API + (schema-enabled) SQL endpoint.
    /// Null for non-OneLake roots. Network calls; cached for the catalog's lifetime (refreshed on re-ATTACH).</summary>
    private FabricLakehouse.OneLakeInfo? OneLake()
    {
        if (!_oneLakeResolved)
        {
            _oneLake = FabricLakehouse.IsOneLake(_root) ? FabricLakehouse.Resolve(_root, _fabricCredential) : null;
            _oneLakeResolved = true;
        }
        return _oneLake;
    }

    /// <summary>The Delta table folder for a (schema, table). A schema-enabled OneLake lakehouse stores tables at
    /// <c>&lt;root&gt;/&lt;schema&gt;/&lt;table&gt;</c>; everything else is flat <c>&lt;root&gt;/&lt;table&gt;</c>
    /// (the DuckDB schema is then the single "main", ignored).</summary>
    private string TablePath(string schema, string table) =>
        OneLake()?.SchemaEnabled == true ? _root + "/" + schema + "/" + table : _root + "/" + table;

    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        MetadataKind.Schemas => SingleColumn("schema_name", SchemaNames()),
        MetadataKind.Tables => DiscoverTables(),
        // Columns = a zero-row stream whose SCHEMA describes the table's columns (engineered-wood's Delta schema).
        MetadataKind.Columns => new InMemoryArrayStream(
            DeltaReader.GetSchema(AmbientOpener.Current, TablePath(schema!, table!)), System.Array.Empty<RecordBatch>()),
        // RowId: always surface the virtual _metadata.row_id — a TRANSIENT (file, position) rowid computed at
        // scan time (no row-tracking feature needed; works on ANY Delta table). Enables UPDATE/DELETE
        // (rowid-based, mirrors the SQL Server backend); DELETE is copy-on-write (plain add/remove).
        MetadataKind.RowId => SingleColumn("name", new[] { RowIdColumn }),
        // No row-count/NDV stats surfaced, no functions.
        _ => EmptyStringTable("name"),
    };

    /// <summary>The catalog's schemas: the lakehouse schemas for a schema-enabled OneLake lakehouse, else the
    /// single flat "main".</summary>
    private IReadOnlyList<string> SchemaNames()
    {
        var ol = OneLake();
        if (ol?.SchemaEnabled == true)
        {
            var schemas = new SortedSet<string>(System.StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(ol.DefaultSchema))
            {
                schemas.Add(ol.DefaultSchema!); // always expose the default schema (so CREATE works when empty)
            }
            foreach (var (s, _) in ol.Tables)
            {
                schemas.Add(s);
            }
            if (schemas.Count == 0)
            {
                schemas.Add(MainSchema);
            }
            return new List<string>(schemas);
        }
        return new[] { MainSchema };
    }

    /// <summary>Discovers tables = immediate subdirs of the root containing a <c>_delta_log/</c>. Globs the
    /// commit files (<c>&lt;root&gt;/*/_delta_log/*.json</c>) and takes the distinct parent-of-_delta_log
    /// directory name as the table.</summary>
    private IArrowArrayStream DiscoverTables()
    {
        var pairs = new SortedSet<(string Schema, string Table)>();
        var ol = OneLake();
        if (ol is not null)
        {
            // OneLake: DuckDB's azure glob can't recurse a _delta_log tree (PR #174), so tables are listed via the
            // OneLake DFS endpoint directly (GetPaths) — flat (Tables/<table>, schema "main") or schema-enabled
            // (Tables/<schema>/<table>); the schema-enabled flag is from the Fabric API. Resolved in OneLake().
            foreach (var (s, t) in ol.Tables)
            {
                pairs.Add((s, t));
            }
        }
        else
        {
            // Local / S3 / plain ADLS: discover by globbing the commit files <root>/*/_delta_log/*.json.
            var json = HostFs.Glob(AmbientOpener.Current, _root + "/*/_delta_log/*.json");
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var path = Normalize(el.GetProperty("path").GetString() ?? string.Empty);
                // …/<table>/_delta_log/<file>.json  →  the segment before "/_delta_log/".
                int marker = path.IndexOf("/_delta_log/", System.StringComparison.Ordinal);
                if (marker < 0)
                {
                    continue;
                }
                int slash = path.LastIndexOf('/', marker - 1);
                var name = slash < 0 ? path.Substring(0, marker) : path.Substring(slash + 1, marker - slash - 1);
                if (name.Length > 0)
                {
                    pairs.Add((MainSchema, name));
                }
            }
        }
        var schemaCol = new List<string>();
        var nameCol = new List<string>();
        var typeCol = new List<string>();
        foreach (var (s, t) in pairs)
        {
            schemaCol.Add(s);
            nameCol.Add(t);
            typeCol.Add("BASE TABLE");
        }
        return ThreeColumn("schema_name", schemaCol, "table_name", nameCol, "table_type", typeCol);
    }

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        var opener = AmbientOpener.Current;
        var path = TablePath(schemaName, tableName);
        // Push the FILTER into engineered-wood file/row-group skipping (superset-safe; DuckDB re-applies).
        // Projection is left to DuckDB above the scan (the full schema is returned, mapped by name) — same as
        // the global arrownet_delta_scan; column-pruning into parquet would need a projected-schema stream.
        var spec = ScanSpec.Parse(specJson);
        EngineeredWood.Expressions.Predicate? filter = spec?.Filter is { } node
            ? new DeltaFilterBuilder(ReadFilterValues(filterValues)).Build(node)
            : null;
        var userSchema = DeltaReader.GetSchema(opener, path);

        // When the scan requests the virtual rowid (UPDATE/DELETE plans), stream WITH the trailing
        // _metadata.row_id column and advertise it in the schema; DuckDB maps the requested output by name.
        bool wantRowId = spec?.Columns is { } cols && cols.Contains(RowIdColumn);
        if (wantRowId)
        {
            var fields = new List<Field>(userSchema.FieldsList)
            {
                new Field(RowIdColumn, Int64Type.Default, nullable: false),
            };
            var schemaWithRowId = new Schema(fields, userSchema.Metadata);
            return new AsyncEnumerableArrowStream(
                schemaWithRowId, DeltaReader.StreamWithRowIds(opener, path, columns: null, filter, default));
        }

        return new AsyncEnumerableArrowStream(userSchema, DeltaReader.Stream(opener, path, columns: null, filter, default));
    }

    private static IReadOnlyList<object?> ReadFilterValues(IArrowArrayStream? filterValues)
    {
        if (filterValues is null)
        {
            return System.Array.Empty<object?>();
        }
        using (filterValues)
        {
            var batch = filterValues.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                return System.Array.Empty<object?>();
            }
            var values = new object?[batch.ColumnCount];
            for (int i = 0; i < batch.ColumnCount; i++)
            {
                try { values[i] = ArrowValueReader.ReadScalar(batch.Column(i), 0); }
                catch (System.NotSupportedException) { values[i] = null; }
            }
            return values;
        }
    }

    // ---- write surface (INSERT / CTAS / COPY via the streaming bulk path) ----

    /// <summary>Streaming bulk write (INSERT / CTAS / COPY). Runs on the bulk consumer thread; the host-FS
    /// opener was re-established on it by BulkSession. createTable/replace => Overwrite (CTAS/REPLACE: the table
    /// becomes exactly these rows); otherwise Append (INSERT). One Delta commit. Returns rows written.</summary>
    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId)
    {
        var opener = AmbientOpener.Current;
        var (schema, batches, rows) = DeltaWriter.Materialize(data, default);
        var mode = createTable || replace ? DeltaWriteMode.Overwrite : DeltaWriteMode.Append;
        DeltaWriter.Write(opener, TablePath(schemaName, tableName), schema, batches, mode, default,
                          deletionVectors: _deletionVectorsOnCreate);
        return rows;
    }

    /// <summary>Creates an empty Delta table (commit 0 with the schema). Idempotent (OpenOrCreate), so
    /// <paramref name="ifNotExists"/> is satisfied; PK/UNIQUE/DEFAULT are ignored (Delta has no such constraints).</summary>
    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                            string? primaryKey, string? uniques, string? defaults)
        => DeltaWriter.Create(AmbientOpener.Current, TablePath(schemaName, tableName), columns, default,
                              deletionVectors: _deletionVectorsOnCreate);

    public void CreateSchema(string s, bool ie) { } // schemas mirror the lakehouse; CREATE SCHEMA is a no-op
    public void BeginTransaction() { }              // Delta is per-commit (no cross-statement transaction)
    public void CommitTransaction() { }
    public void RollbackTransaction() { }

    // ---- still unsupported in this slice ----
    private static NotSupportedException Unsupported(string what) =>
        new($"delta provider: {what} not supported yet.");

    /// <summary>DROP TABLE = recursively delete the table's <c>&lt;root&gt;/&lt;table&gt;/</c> folder (its _delta_log
    /// + all data files). OneLake goes through the <b>DFS endpoint directly</b>
    /// (<see cref="FabricLakehouse.DeleteDirectory"/>) — DuckDB's azure FileSystem has no RemoveDirectory; local/S3
    /// use the host's recursive directory-delete callback. Idempotent (no error if missing), so
    /// <paramref name="ifExists"/> is satisfied either way.</summary>
    public void DropTable(string schemaName, string tableName, bool ifExists)
    {
        if (FabricLakehouse.IsOneLake(_root))
        {
            FabricLakehouse.DeleteDirectory(TablePath(schemaName, tableName), _fabricCredential);
            return;
        }
        if (!HostFs.CanRemoveDir)
        {
            throw Unsupported("DROP TABLE (host does not provide a recursive directory-delete callback)");
        }
        HostFs.RemoveDir(AmbientOpener.Current, TablePath(schemaName, tableName));
    }

    /// <summary>DELETE = rowid-based via Delta row tracking: <paramref name="keys"/> is a stream whose single
    /// <c>_metadata.row_id</c> Int64 column holds the stable ids of the rows to delete (DuckDB's scan produced
    /// them, applying the WHERE). Collected and applied via deletion vectors (<see cref="DeltaReader.DeleteByRowIds"/>).</summary>
    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys)
    {
        var opener = AmbientOpener.Current;
        var ids = new List<long>();
        using (keys)
        {
            while (keys.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                using (batch)
                {
                    if (batch.Length == 0)
                    {
                        continue;
                    }
                    // The keys batch has exactly the rowid column(s); a virtual rowid is the single Int64
                    // _metadata.row_id (column 0).
                    if (batch.Column(0) is Int64Array idArray)
                    {
                        for (int i = 0; i < idArray.Length; i++)
                        {
                            if (idArray.GetValue(i) is { } id)
                            {
                                ids.Add(id);
                            }
                        }
                    }
                }
            }
        }
        if (ids.Count == 0)
        {
            return 0;
        }
        var path = TablePath(schemaName, tableName);
        // Follow the TABLE's config: deletion-vector tables get the no-rewrite DV delete; everything else is
        // copy-on-write. (Honors external DV tables regardless of this catalog's create-time flag.)
        return DeltaReader.IsDeletionVectorsEnabled(opener, path)
            ? DeltaReader.DeleteByRowIdsViaVectors(opener, path, ids, default)
            : DeltaReader.DeleteByRowIds(opener, path, ids, default);
    }

    public IArrowArrayStream ExecuteQuery(string sql) => throw Unsupported("raw query");
    public long ExecuteNonQuery(string sql) => throw Unsupported("exec");

    /// <summary>UPDATE = rowid-based copy-on-write: <paramref name="data"/> carries the new SET-column values
    /// (columns 0..<paramref name="setColumnCount"/>-1, named by the target column) + the transient
    /// <c>_metadata.row_id</c> (last column). We re-scan the table with rowids, replace the SET columns on the
    /// matched rows (rebuilt as clean Apache.Arrow batches), and OVERWRITE via the proven write path — so the
    /// output is plain Delta + standard-readable (delta-kernel/Spark/Fabric). Returns rows updated.</summary>
    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data)
    {
        var opener = AmbientOpener.Current;
        var path = TablePath(schemaName, tableName);

        // 1. Parse the update stream: rowid -> new SET values (aligned to the SET column order).
        var setColNames = new List<string>();
        var updates = new Dictionary<long, object?[]>();
        using (data)
        {
            while (data.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } b)
            {
                using (b)
                {
                    if (b.Length == 0)
                    {
                        continue;
                    }
                    if (setColNames.Count == 0)
                    {
                        for (int j = 0; j < setColumnCount; j++)
                        {
                            setColNames.Add(b.Schema.FieldsList[j].Name);
                        }
                    }
                    var ridArr = (Int64Array)b.Column(setColumnCount);
                    for (int i = 0; i < b.Length; i++)
                    {
                        if (ridArr.GetValue(i) is not { } rid)
                        {
                            continue;
                        }
                        var vals = new object?[setColumnCount];
                        for (int j = 0; j < setColumnCount; j++)
                        {
                            vals[j] = ArrowValueReader.ReadScalar(b.Column(j), i);
                        }
                        updates[rid] = vals;
                    }
                }
            }
        }
        if (updates.Count == 0)
        {
            return 0;
        }

        // 2. Map SET column names -> user-schema column indices (case-insensitive).
        var userSchema = DeltaReader.GetSchema(opener, path);
        var fields = userSchema.FieldsList;
        var setSlotByColumn = new int[fields.Count];
        for (int c = 0; c < fields.Count; c++)
        {
            setSlotByColumn[c] = -1;
            for (int j = 0; j < setColNames.Count; j++)
            {
                if (string.Equals(fields[c].Name, setColNames[j], System.StringComparison.OrdinalIgnoreCase))
                {
                    setSlotByColumn[c] = j;
                    break;
                }
            }
        }

        // 3. Per-file copy-on-write: engineered-wood rewrites ONLY the files containing a matched row. For each
        //    such file it hands us (fileOrdinal, the file's batches in read order); we rebuild the SET columns
        //    on the matched positions (rowid = (ordinal << RowIdPositionBits) | positionInFile — same encoding
        //    the scan emitted) and return the modified batches. Unaffected files are left untouched.
        DeltaReader.UpdateByRowIds(opener, path, updates.Keys, (ordinal, batches) =>
        {
            // Each batch is the file's USER columns (0..fields.Count-1) + a trailing _metadata.row_id (last) =
            // the ABSOLUTE rowid. Match each row by its rowid (robust even when the file has a deletion vector).
            var outBatches = new List<RecordBatch>(batches.Count);
            foreach (var batch in batches)
            {
                var rids = (Int64Array)batch.Column(batch.ColumnCount - 1);
                var newCols = new IArrowArray[fields.Count];
                for (int c = 0; c < fields.Count; c++)
                {
                    int slot = setSlotByColumn[c];
                    if (slot < 0)
                    {
                        newCols[c] = batch.Column(c); // unchanged column
                        continue;
                    }
                    var values = new List<object?>(batch.Length);
                    for (int i = 0; i < batch.Length; i++)
                    {
                        long rid = rids.GetValue(i) ?? -1;
                        values.Add(updates.TryGetValue(rid, out var nv)
                            ? nv[slot]
                            : ArrowValueReader.ReadScalar(batch.Column(c), i));
                    }
                    newCols[c] = BuildArray(fields[c].DataType, values);
                }
                outBatches.Add(new RecordBatch(userSchema, newCols, batch.Length));
            }
            return outBatches;
        }, default);

        return updates.Count; // each distinct rowid is one updated row
    }

    /// <summary>Builds an Arrow array of <paramref name="type"/> from boxed CLR values (the inverse of
    /// <see cref="ArrowValueReader.ReadScalar"/>) — used to rebuild a SET column during UPDATE. Covers the types
    /// DuckDB↔Delta exchanges; an unsupported SET-column type throws (the UPDATE fails cleanly).</summary>
    private static IArrowArray BuildArray(Apache.Arrow.Types.IArrowType type, List<object?> values)
    {
        switch (type)
        {
            case BooleanType: { var b = new BooleanArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((bool)v); } return b.Build(); }
            case Int8Type: { var b = new Int8Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((sbyte)v); } return b.Build(); }
            case Int16Type: { var b = new Int16Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((short)v); } return b.Build(); }
            case Int32Type: { var b = new Int32Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((int)v); } return b.Build(); }
            case Int64Type: { var b = new Int64Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((long)v); } return b.Build(); }
            case UInt8Type: { var b = new UInt8Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((byte)v); } return b.Build(); }
            case UInt16Type: { var b = new UInt16Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((ushort)v); } return b.Build(); }
            case UInt32Type: { var b = new UInt32Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((uint)v); } return b.Build(); }
            case UInt64Type: { var b = new UInt64Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((ulong)v); } return b.Build(); }
            case FloatType: { var b = new FloatArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((float)v); } return b.Build(); }
            case DoubleType: { var b = new DoubleArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((double)v); } return b.Build(); }
            case Decimal128Type d: { var b = new Decimal128Array.Builder(d); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((decimal)v); } return b.Build(); }
            case StringType: { var b = new StringArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((string)v); } return b.Build(); }
            case Date32Type: { var b = new Date32Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(System.DateOnly.FromDateTime((System.DateTime)v)); } return b.Build(); }
            case TimestampType ts: { var b = new TimestampArray.Builder(ts); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(v is System.DateTimeOffset dto ? dto : new System.DateTimeOffset(System.DateTime.SpecifyKind((System.DateTime)v, System.DateTimeKind.Utc))); } return b.Build(); }
            default: throw new NotSupportedException($"delta UPDATE: unsupported SET column type {type.TypeId}");
        }
    }

    public IArrowArrayStream InsertReturning(string s, string t, IArrowArrayStream r) => throw Unsupported("INSERT ... RETURNING");
    public void DropSchema(string s, bool ie) => throw Unsupported("DROP SCHEMA");
    /// <summary>Schema evolution. Only <c>ADD COLUMN</c> is supported on Delta — a metadata-only commit appending
    /// a nullable column (no file rewrite; old rows read back NULL). RENAME/DROP/TYPE need column mapping or a
    /// full rewrite (clean error). <paramref name="a1"/> = the new column's name, <paramref name="c"/> carries its
    /// Arrow type + nullability.</summary>
    public void AlterTable(int k, string s, string t, string? a1, string? a2, Field? c, int f)
    {
        if (k != AlterKind.AddColumn)
            throw Unsupported("ALTER TABLE (only ADD COLUMN is supported on Delta)");

        var col = c ?? throw new System.InvalidOperationException("delta ADD COLUMN requires a column definition.");
        string name = a1 ?? col.Name;
        var field = string.Equals(name, col.Name, System.StringComparison.Ordinal)
            ? col
            : new Field(name, col.DataType, col.IsNullable);
        DeltaReader.AddColumn(AmbientOpener.Current, TablePath(s, t), field, default);
    }

    public Schema GetFunctionParamSchema(string s, string f) => throw NoFunctions();
    public Schema GetFunctionReturnSchema(string s, string f) => throw NoFunctions();
    public IArrowArrayStream ExecuteScalar(string s, string f, IArrowArrayStream a) => throw NoFunctions();
    public Schema GetFunctionOutputSchema(string s, string f, RecordBatch? a = null) => throw NoFunctions();
    public IBoundTable TableBind(string s, string f, RecordBatch? a) => throw NoFunctions();
    public IArrowInOutBinding InOutBind(string s, string f, RecordBatch? a, Schema input) => throw NoFunctions();
    public IAggregateSession AggOpen(string s, string f) => throw NoFunctions();
    private static NotSupportedException NoFunctions() => new("delta provider: no catalog functions.");

    public void Dispose() { }

    // ---- Arrow metadata-stream helpers (mirror DaxCatalog) ----
    private static IArrowArrayStream SingleColumn(string name, IReadOnlyList<string> values)
    {
        var schema = new Schema(new[] { new Field(name, StringType.Default, nullable: true) }, null);
        var b = new StringArray.Builder();
        foreach (var v in values) { b.Append(v); }
        return new InMemoryArrayStream(schema, new[] { new RecordBatch(schema, new IArrowArray[] { b.Build() }, values.Count) });
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
        static IArrowArray Build(IReadOnlyList<string> vals)
        {
            var b = new StringArray.Builder();
            foreach (var v in vals) { b.Append(v); }
            return b.Build();
        }
        return new InMemoryArrayStream(schema,
            new[] { new RecordBatch(schema, new[] { Build(c0), Build(c1), Build(c2) }, c0.Count) });
    }

    private static IArrowArrayStream EmptyStringTable(params string[] columns)
    {
        var builder = new Schema.Builder();
        foreach (var c in columns) { builder.Field(new Field(c, StringType.Default, nullable: true)); }
        return new InMemoryArrayStream(builder.Build(), System.Array.Empty<RecordBatch>());
    }
}
