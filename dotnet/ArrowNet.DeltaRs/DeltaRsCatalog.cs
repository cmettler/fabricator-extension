using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using ArrowNet.Bridge;
using DeltaLake.Extensions;
using DeltaLake.Interfaces;
using DeltaLake.Kernel.Core;
using DeltaLake.Table;

namespace ArrowNet.DeltaRs;

/// <summary>
/// A Delta Lake catalog over delta-rs (delta-dotnet). delta-dotnet is single-table (<see cref="IEngine"/> +
/// <see cref="ITable"/>), so this class supplies the catalog layer: discovery (local FS in v1), per-table
/// open, and the mapping of each <see cref="IBackendCatalog"/> operation to a delta-dotnet call. delta-rs does
/// its own object_store IO, so — unlike the engineered-wood provider — this does NOT use the host-FS bridge.
///
/// v1 scope: read (scan, streamed via DataFusion), CREATE/INSERT/CTAS/COPY (append/overwrite), metadata
/// (schemas/tables/columns), time travel (version + timestamp), snapshots (history), change data feed.
/// DEFERRED: UPDATE/DELETE (delta-rs's predicate/SQL DML doesn't map to DuckDB's rowid model — no low-level
/// remove/position API; see docs/delta-rs-provider.md "The DML crux"), ALTER (no add-column API), functions.
/// Cloud discovery (abfss/s3) is deferred — v1 discovers local roots only.
/// </summary>
public sealed class DeltaRsCatalog : IBackendCatalog
{
    private const string MainSchema = "main";

    private readonly DeltaEngine _engine = new(EngineOptions.Default);
    private readonly string _root;                              // ATTACH target (path or URI), forward slashes
    private readonly Dictionary<string, string> _storage;      // delta-rs object_store options (from a secret)
    private readonly bool _schemas;                             // two-level <root>/<schema>/<table> layout

    public DeltaRsCatalog(string connectionString, string? optionsJson)
    {
        var (target, storage) = StorageOptionsCodec.Decode(connectionString);
        _root = target.Replace('\\', '/').TrimEnd('/');
        _storage = storage;
        _schemas = ParseBoolOption(optionsJson, "schemas");
    }

    // ---- path / uri helpers ----

    private bool RootIsLocal =>
        !_root.Contains("://", StringComparison.Ordinal) ||
        _root.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    private string LocalRootDir =>
        _root.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(_root).LocalPath : _root;

    private string LocalTableDir(string schema, string table)
    {
        var rel = _schemas ? Path.Combine(schema, table) : table;
        return Path.Combine(LocalRootDir, rel);
    }

    /// <summary>The table location as a URI delta-dotnet accepts (file:// for a local root, else the cloud URI).</summary>
    private string TableUri(string schema, string table)
    {
        var rel = _schemas ? $"{schema}/{table}" : table;
        if (RootIsLocal)
        {
            return new Uri(Path.GetFullPath(LocalTableDir(schema, table))).AbsoluteUri;
        }
        return _root + "/" + rel;
    }

    private ITable Open(string schema, string table, ulong? version = null) =>
        Run(_engine.LoadTableAsync(
            new TableOptions { TableLocation = TableUri(schema, table), StorageOptions = _storage, Version = version },
            default));

    // ---- metadata ----

    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        MetadataKind.Schemas => SingleColumn("schema_name", SchemaNames()),
        MetadataKind.Tables => TablesStream(),
        MetadataKind.Columns => ColumnsStream(schema!, table!),
        // No rowid: delta-rs has no low-level position/remove API, so UPDATE/DELETE are unsupported in v1
        // (DuckDB sees no rowid and won't plan them). See docs/delta-rs-provider.md.
        MetadataKind.RowId => SingleColumn("name", System.Array.Empty<string>()),
        MetadataKind.Snapshots => SnapshotsStream(schema, table),
        MetadataKind.Changes => ChangesStream(schema, table),
        _ => SingleColumn("name", System.Array.Empty<string>()),
    };

    private IReadOnlyList<string> SchemaNames()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal) { MainSchema };
        foreach (var (s, _) in DiscoverPairs())
        {
            set.Add(s);
        }
        return set.ToList();
    }

    private IArrowArrayStream TablesStream()
    {
        var schemaCol = new List<string>();
        var nameCol = new List<string>();
        var typeCol = new List<string>();
        foreach (var (s, t) in DiscoverPairs())
        {
            schemaCol.Add(s);
            nameCol.Add(t);
            typeCol.Add("BASE TABLE");
        }
        return ThreeColumn("schema_name", schemaCol, "table_name", nameCol, "table_type", typeCol);
    }

    private IArrowArrayStream ColumnsStream(string schema, string table)
    {
        using var t = Open(schema, table);
        // A zero-row stream whose SCHEMA describes the table's columns.
        return new InMemoryArrayStream(t.Schema(), System.Array.Empty<RecordBatch>());
    }

    /// <summary>Local-FS table discovery: subdirs containing a <c>_delta_log</c>. Cloud discovery is deferred
    /// (delta-dotnet exposes no directory listing; a cloud v2 would reuse the FabricLakehouse/DFS path).</summary>
    private IEnumerable<(string Schema, string Table)> DiscoverPairs()
    {
        if (!RootIsLocal || !Directory.Exists(LocalRootDir))
        {
            yield break;
        }
        if (_schemas)
        {
            foreach (var schemaDir in Directory.EnumerateDirectories(LocalRootDir))
            {
                var schemaName = Path.GetFileName(schemaDir);
                foreach (var tableDir in Directory.EnumerateDirectories(schemaDir))
                {
                    if (Directory.Exists(Path.Combine(tableDir, "_delta_log")))
                    {
                        yield return (schemaName, Path.GetFileName(tableDir));
                    }
                }
            }
        }
        else
        {
            foreach (var tableDir in Directory.EnumerateDirectories(LocalRootDir))
            {
                if (Directory.Exists(Path.Combine(tableDir, "_delta_log")))
                {
                    yield return (MainSchema, Path.GetFileName(tableDir));
                }
            }
        }
    }

    // ---- read (scan) ----

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        // Time travel is DEFERRED in v1: delta-dotnet reads only the latest snapshot via the Delta Kernel, and
        // any versioned load disables the kernel (isKernelSupported=false) with no non-kernel read path
        // ("not supported without using the Delta Kernel"). VERSION/TIMESTAMP travel needs the kernel wired to
        // snapshot at a version (or a delta-rs read-at-version) — see docs/delta-rs-provider.md.
        var spec = ScanSpec.Parse(specJson);
        if (spec?.At is not null)
        {
            throw Unsupported("time travel (delta-dotnet reads only the latest snapshot in v1)");
        }

        OwnedArrowTable owned;
        using (var table = Open(schemaName, tableName))
        {
            // v1: read the whole table into an owned Apache.Arrow.Table (materialized into managed memory,
            // independent of the ITable handle). Its Schema is EXACTLY the bound table.Schema() — using
            // DataFusion's SELECT * instead risks a schema divergence that SIGSEGVs arrow_ingest. DuckDB applies
            // projection + filter above the scan (correct superset). Streaming + SQL pushdown is a follow-up.
            owned = Run(table.ReadAsArrowTableAsync(default));
        }
        return new AsyncEnumerableArrowStream(owned.Table.Schema, TableBatches(owned.Table), owner: owned);
    }

    // Yields an Apache.Arrow.Table as record batches, one per chunk. A Table built from a single read has all
    // columns chunked identically (one chunk per source batch), so chunk i of every column shares a length. The
    // batches share the Table's arrays (no copy) and stay valid until the owner (OwnedArrowTable) is disposed at
    // stream close.
    private static async System.Collections.Generic.IAsyncEnumerable<RecordBatch> TableBatches(Table table)
    {
        await Task.CompletedTask;
        int columnCount = table.ColumnCount;
        if (columnCount == 0)
        {
            yield break;
        }
        int chunkCount = table.Column(0).Data.ArrayCount;
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            var arrays = new IArrowArray[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                arrays[c] = table.Column(c).Data.Array(chunk);
            }
            int length = arrays[0].Length;
            yield return new RecordBatch(table.Schema, arrays, length);
        }
    }

    // ---- write (INSERT / CTAS / COPY) ----

    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId, IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode)
    {
        // sortColumns (SORTED BY) is a warehouse CLUSTER BY concept — Delta doesn't cluster; ignored.
        bool overwriteSchema = string.Equals(schemaMode, "overwrite", StringComparison.OrdinalIgnoreCase);
        var schema = data.Schema;
        ITable table;
        if (createTable)
        {
            var create = new TableCreateOptions(TableUri(schemaName, tableName), schema)
            {
                StorageOptions = _storage,
                PartitionBy = (partitionColumns ?? new List<string>()).ToList(),
                SaveMode = replace ? SaveMode.Overwrite : SaveMode.ErrorIfExists,
            };
            table = Run(_engine.CreateTableAsync(create, default));
        }
        else
        {
            table = Open(schemaName, tableName);
        }

        using (table)
        {
            var (batches, rows) = ReadAll(data);
            try
            {
                if (batches.Count > 0)
                {
                    var save = (!createTable && replace) || overwriteSchema ? SaveMode.Overwrite : SaveMode.Append;
                    Run(table.InsertAsync(batches, schema,
                        new InsertOptions { SaveMode = save, OverwriteSchema = overwriteSchema }, default));
                }
                return rows;
            }
            finally
            {
                foreach (var b in batches)
                {
                    b.Dispose();
                }
            }
        }
    }

    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists, string? primaryKey,
                            string? uniques, string? defaults, IReadOnlyList<string>? partitionColumns,
                            IReadOnlyList<string>? sortColumns, IReadOnlyList<string>? identityColumns)
    {
        // Delta has no PK/UNIQUE/DEFAULT/IDENTITY — those args are ignored (as in the engineered-wood provider).
        var create = new TableCreateOptions(TableUri(schemaName, tableName), columns)
        {
            StorageOptions = _storage,
            PartitionBy = (partitionColumns ?? new List<string>()).ToList(),
            SaveMode = ifNotExists ? SaveMode.Ignore : SaveMode.ErrorIfExists,
        };
        using var table = Run(_engine.CreateTableAsync(create, default));
    }

    public void DropTable(string schemaName, string tableName, bool ifExists)
    {
        if (!RootIsLocal)
        {
            throw Unsupported("DROP TABLE on a non-local delta-rs catalog (cloud recursive delete deferred)");
        }
        var dir = LocalTableDir(schemaName, tableName);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        else if (!ifExists)
        {
            throw new InvalidOperationException($"delta-rs: table {schemaName}.{tableName} does not exist.");
        }
    }

    public void CreateSchema(string schemaName, bool ifNotExists)
    {
        if (_schemas && RootIsLocal)
        {
            Directory.CreateDirectory(Path.Combine(LocalRootDir, schemaName));
        }
        // flat catalog: schemas are implicit ("main") — no-op.
    }

    public void DropSchema(string schemaName, bool ifExists)
    {
        if (_schemas && RootIsLocal && !string.Equals(schemaName, MainSchema, StringComparison.Ordinal))
        {
            var dir = Path.Combine(LocalRootDir, schemaName);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            else if (!ifExists)
            {
                throw new InvalidOperationException($"delta-rs: schema {schemaName} does not exist.");
            }
            return;
        }
        throw Unsupported("DROP SCHEMA (only in a local schemas-mode catalog)");
    }

    // ---- snapshots (history) + change data feed ----

    private IArrowArrayStream SnapshotsStream(string? schema, string? table)
    {
        if (string.IsNullOrEmpty(table))
        {
            throw new ArgumentException("delta-rs snapshots: a table name is required (catalog, 'schema.table').");
        }
        var resolvedSchema = string.IsNullOrEmpty(schema) ? MainSchema : schema!;
        using var t = Open(resolvedSchema, table!);
        var history = Run(t.HistoryAsync(null, default));   // null => all commits
        ulong current = t.Version() ?? (ulong)Math.Max(0, history.Length - 1);

        var tsType = new TimestampType(TimeUnit.Microsecond, (string?)null);
        var versions = new Int64Array.Builder();
        var timestamps = new TimestampArray.Builder(tsType);
        var operations = new StringArray.Builder();
        var operationParams = new StringArray.Builder();

        // delta-dotnet's CommitInfo carries no explicit version; history is newest-first, so version = current - i
        // (best-effort — commit versions are contiguous). Timestamp is Unix ms.
        for (int i = 0; i < history.Length; i++)
        {
            var c = history[i];
            versions.Append((long)current - i);
            if (c.Timestamp is { } ms)
            {
                timestamps.Append(DateTimeOffset.FromUnixTimeMilliseconds(ms));
            }
            else
            {
                timestamps.AppendNull();
            }
            operations.Append(c.Operation);
            operationParams.Append(c.OperationParameters is { } p ? JsonSerializer.Serialize(p) : null);
        }

        var schemaOut = new Schema(new[]
        {
            new Field("version", Int64Type.Default, nullable: false),
            new Field("timestamp", tsType, nullable: true),
            new Field("operation", StringType.Default, nullable: true),
            new Field("operation_parameters", StringType.Default, nullable: true),
        }, metadata: null);
        var batch = new RecordBatch(schemaOut, new IArrowArray[]
        {
            versions.Build(), timestamps.Build(), operations.Build(), operationParams.Build(),
        }, history.Length);
        return new InMemoryArrayStream(schemaOut, new[] { batch });
    }

    private IArrowArrayStream ChangesStream(string? tableRef, string? range)
    {
        if (string.IsNullOrEmpty(tableRef))
        {
            throw new ArgumentException("delta-rs changes: a table is required (catalog, 'schema.table', from, to).");
        }
        string schema = MainSchema, table = tableRef!;
        int dot = tableRef!.IndexOf('.');
        if (dot >= 0)
        {
            schema = tableRef.Substring(0, dot);
            table = tableRef.Substring(dot + 1);
        }
        ulong from = 0;
        ulong? to = null;
        if (!string.IsNullOrEmpty(range))
        {
            var parts = range!.Split(':');
            if (parts.Length > 0 && ulong.TryParse(parts[0], out var f)) { from = f; }
            if (parts.Length > 1 && ulong.TryParse(parts[1], out var tv)) { to = tv; }
        }

        using var t = Open(schema, table);
        var options = new TableChangesOptions(from) { EndVersion = to };
        // CDF is typically modest; materialize so the table handle can be released here. Schema comes from the
        // first batch (it already carries _change_type / _commit_version / _commit_timestamp).
        var batches = new List<RecordBatch>();
        Schema? schemaOut = null;
        foreach (var b in t.QueryTableChangesAsync(options, default).ToBlockingEnumerable())
        {
            schemaOut ??= b.Schema;
            batches.Add(b);
        }
        schemaOut ??= ChangeSchema(t.Schema());
        return new InMemoryArrayStream(schemaOut, batches);
    }

    private static Schema ChangeSchema(Schema tableSchema)
    {
        var fields = new List<Field>(tableSchema.FieldsList)
        {
            new Field("_change_type", StringType.Default, nullable: false),
            new Field("_commit_version", Int64Type.Default, nullable: false),
            new Field("_commit_timestamp", new TimestampType(TimeUnit.Microsecond, (string?)null), nullable: true),
        };
        return new Schema(fields, tableSchema.Metadata);
    }

    // ---- transactions (Delta is per-commit; no cross-statement transaction) ----

    public void BeginTransaction() { }
    public void CommitTransaction() { }
    public void RollbackTransaction() { }

    // ---- unsupported in v1 ----

    // UPDATE/DELETE: delta-rs DML is predicate/SQL-based with no low-level position/remove API, so it cannot
    // satisfy DuckDB's rowid model (DuckDB never hands us the WHERE). Deferred — see docs/delta-rs-provider.md
    // "The DML crux" (options: reconstruct a predicate from scanned keys, passthrough MERGE, or a remote-merge
    // optimizer step). No rowid is advertised, so DuckDB won't plan these.
    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys) =>
        throw Unsupported("UPDATE/DELETE (delta-rs predicate DML does not map to DuckDB's rowid model — use "
            + "the engineeredwooddelta provider, or see docs/delta-rs-provider.md)");

    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data) =>
        throw Unsupported("UPDATE/DELETE (delta-rs predicate DML does not map to DuckDB's rowid model — use "
            + "the engineeredwooddelta provider, or see docs/delta-rs-provider.md)");

    // ALTER: delta-dotnet exposes no add-column API (only InsertOptions.OverwriteSchema on a write).
    public void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2,
                           Field? column, int flags) =>
        throw Unsupported("ALTER TABLE (delta-dotnet has no schema-DDL API)");

    public IArrowArrayStream ExecuteQuery(string sql) => throw Unsupported("raw query");
    public long ExecuteNonQuery(string sql) => throw Unsupported("exec");
    public IArrowArrayStream InsertReturning(string s, string t, IArrowArrayStream r) =>
        throw Unsupported("INSERT ... RETURNING");

    public Schema GetFunctionParamSchema(string s, string f) => throw NoFunctions();
    public Schema GetFunctionReturnSchema(string s, string f) => throw NoFunctions();
    public IArrowArrayStream ExecuteScalar(string s, string f, IArrowArrayStream a) => throw NoFunctions();
    public Schema GetFunctionOutputSchema(string s, string f, RecordBatch? a = null) => throw NoFunctions();
    public IBoundTable TableBind(string s, string f, RecordBatch? a) => throw NoFunctions();
    public IArrowInOutBinding InOutBind(string s, string f, RecordBatch? a, Schema inputSchema) => throw NoFunctions();
    public IAggregateSession AggOpen(string s, string f) => throw NoFunctions();

    public void Dispose() => _engine.Dispose();

    // ---- helpers ----

    private static (List<RecordBatch> Batches, long Rows) ReadAll(IArrowArrayStream data)
    {
        var list = new List<RecordBatch>();
        long rows = 0;
        while (true)
        {
            var b = data.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (b is null)
            {
                break;
            }
            list.Add(b);
            rows += b.Length;
        }
        return (list, rows);
    }

    private static IArrowArrayStream SingleColumn(string name, IReadOnlyList<string> values)
    {
        var b = new StringArray.Builder();
        foreach (var v in values)
        {
            b.Append(v);
        }
        var schema = new Schema(new[] { new Field(name, StringType.Default, nullable: true) }, metadata: null);
        var batch = new RecordBatch(schema, new IArrowArray[] { b.Build() }, values.Count);
        return new InMemoryArrayStream(schema, new[] { batch });
    }

    private static IArrowArrayStream ThreeColumn(string n1, IReadOnlyList<string> c1, string n2,
        IReadOnlyList<string> c2, string n3, IReadOnlyList<string> c3)
    {
        IArrowArray Build(IReadOnlyList<string> vals)
        {
            var b = new StringArray.Builder();
            foreach (var v in vals) { b.Append(v); }
            return b.Build();
        }
        var schema = new Schema(new[]
        {
            new Field(n1, StringType.Default, nullable: true),
            new Field(n2, StringType.Default, nullable: true),
            new Field(n3, StringType.Default, nullable: true),
        }, metadata: null);
        var batch = new RecordBatch(schema, new[] { Build(c1), Build(c2), Build(c3) }, c1.Count);
        return new InMemoryArrayStream(schema, new[] { batch });
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
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var v)
                && (v.ValueKind == JsonValueKind.True
                    || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static T Run<T>(Task<T> t) => t.GetAwaiter().GetResult();
    private static void Run(Task t) => t.GetAwaiter().GetResult();

    private static NotSupportedException Unsupported(string what) =>
        new($"delta-rs provider: {what} is not supported.");

    private static NotSupportedException NoFunctions() =>
        new("delta-rs provider: catalog functions are not supported.");
}
