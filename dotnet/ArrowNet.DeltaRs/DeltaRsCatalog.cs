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
    private readonly bool _changeDataFeed;                      // enable delta.enableChangeDataFeed on CREATE
    // NOTE: no deletion_vectors option — verified that delta-rs (0.32.1 via delta-dotnet) copy-on-writes for
    // DELETE regardless of delta.enableDeletionVectors (BOTH the MERGE path and predicate DeleteAsync emit
    // add+remove, never a deletionVector). Declaring the feature would only bump the table to reader-v3 (which
    // can break Fabric's OneLake converter) for zero DV benefit. Use the engineeredwooddelta provider for real
    // deletion vectors. See docs/delta-rs-provider.md.

    // OneLake (Fabric) support: resolved lazily via the Unity Catalog REST API. delta-rs reads OneLake only
    // with a GUID-based abfss path, so we cache the workspace/lakehouse GUIDs + the discovered tables.
    private readonly bool _oneLake;
    private bool _oneLakeResolved;
    private bool _oneLakeSchemaEnabled;
    private Guid _workspaceId;
    private Guid _lakehouseId;
    private List<(string Schema, string Table)> _oneLakePairs = new();

    public DeltaRsCatalog(string connectionString, string? optionsJson)
    {
        var (target, storage) = StorageOptionsCodec.Decode(connectionString);
        _root = target.Replace('\\', '/').TrimEnd('/');
        _storage = storage;
        _schemas = ParseBoolOption(optionsJson, "schemas");
        _changeDataFeed = ParseBoolOption(optionsJson, "change_data_feed");
        _oneLake = FabricLakehouse.IsOneLake(_root);
        if (_oneLake)
        {
            // OneLake object_store needs these two beyond the SP creds StorageOptionsCodec already mapped
            // (azure_storage_tenant_id/client_id/client_secret). account_name is always "onelake".
            _storage["azure_storage_account_name"] = "onelake";
            _storage["azure_storage_use_fabric_endpoint"] = "true";
        }
    }

    /// <summary>Resolves the OneLake lakehouse (GUIDs + schema-enabled flag + table list) via the Unity
    /// Catalog REST API, once. delta-rs reads require the GUID-based abfss path built from these.</summary>
    private void EnsureOneLakeResolved()
    {
        if (!_oneLake || _oneLakeResolved)
        {
            return;
        }
        _storage.TryGetValue("azure_storage_tenant_id", out var tenant);
        _storage.TryGetValue("azure_storage_client_id", out var clientId);
        _storage.TryGetValue("azure_storage_client_secret", out var clientSecret);
        var (schemaEnabled, ws, lh, tables) = FabricLakehouse.ResolveOneLakeTables(_root, tenant, clientId, clientSecret);
        _oneLakeSchemaEnabled = schemaEnabled;
        _workspaceId = ws;
        _lakehouseId = lh;
        // Map UC's schema to our display schema: schema-enabled → the UC schema; flat → "main" (the flat abfss
        // path omits the schema segment, which the flat TableUri below reproduces).
        _oneLakePairs = tables.Select(t => (schemaEnabled ? t.Schema : MainSchema, t.Table)).ToList();
        _oneLakeResolved = true;
    }

    /// <summary>Table configuration applied at CREATE (null if none). Enables Change Data Feed when the
    /// <c>change_data_feed</c> ATTACH option is set, so INSERT/DELETE/UPDATE capture change data.</summary>
    private Dictionary<string, string>? CreateConfiguration() =>
        _changeDataFeed ? new Dictionary<string, string> { ["delta.enableChangeDataFeed"] = "true" } : null;

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

    /// <summary>The table location as a URI delta-dotnet accepts: <c>file://</c> for a local root; a
    /// GUID-based OneLake abfss path (the only form delta-rs's object_store reads); else the raw cloud URI.</summary>
    private string TableUri(string schema, string table)
    {
        if (_oneLake)
        {
            EnsureOneLakeResolved();
            // abfss://<wsGuid>@onelake.dfs.fabric.microsoft.com/<lhGuid>/Tables/[<schema>/]<table>. The schema
            // segment is present iff the lakehouse is schema-enabled (a flat lakehouse maps to "main" with no
            // segment). Works for both discovered and newly-created tables.
            string seg = _oneLakeSchemaEnabled ? $"{schema}/" : string.Empty;
            return $"abfss://{_workspaceId}@onelake.dfs.fabric.microsoft.com/{_lakehouseId}/Tables/{seg}{table}";
        }
        if (RootIsLocal)
        {
            return new Uri(Path.GetFullPath(LocalTableDir(schema, table))).AbsoluteUri;
        }
        var rel = _schemas ? $"{schema}/{table}" : table;
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
        // Rowid = ALL columns (a full-row identity). delta-rs has no low-level position/remove API, so DELETE/
        // UPDATE run as a record-batch MERGE matching the scanned rows on every column (NULL-safe). This is
        // sound because a WHERE can't distinguish identical rows, so DuckDB's rowid set is always a complete
        // equivalence class. See docs/delta-rs-provider.md "The DML crux".
        MetadataKind.RowId => SingleColumn("name", RowIdColumns(schema!, table!)),
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

    /// <summary>Table discovery: OneLake → the Unity Catalog REST API (paginated); local FS → subdirs
    /// containing a <c>_delta_log</c>. Other cloud roots (S3 / plain ADLS) are not yet enumerated (delta-dotnet
    /// exposes no directory listing).</summary>
    private IEnumerable<(string Schema, string Table)> DiscoverPairs()
    {
        if (_oneLake)
        {
            EnsureOneLakeResolved();
            foreach (var pair in _oneLakePairs)
            {
                yield return pair;
            }
            yield break;
        }
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
        var spec = ScanSpec.Parse(specJson);

        // Filter pushdown: translate the (superset-safe) FilterNode to a DataFusion WHERE (unpushable → TRUE,
        // dropped, since DuckDB re-applies every predicate above the scan). Only pushed when something renders.
        string? where = null;
        if (spec?.Filter is { } filter)
        {
            var built = BuildWhere(filter, ReadFilterValues(filterValues));
            if (built != "TRUE")
            {
                where = built;
            }
        }

        // Time travel: FROM t AT (VERSION => n) / AT (TIMESTAMP => ts). Read via QueryAsync (DataFusion reads
        // the LOADED snapshot) — NOT ReadAsArrowTableAsync, which needs the kernel and throws on a versioned
        // load. delta-rs supports both version and timestamp.
        if (spec?.At is { } at)
        {
            ulong? version = null;
            DateTimeOffset? timestamp = null;
            if (string.Equals(at.Unit, "version", StringComparison.OrdinalIgnoreCase))
            {
                version = ulong.Parse(at.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (string.Equals(at.Unit, "timestamp", StringComparison.OrdinalIgnoreCase))
            {
                timestamp = DateTimeOffset.Parse(at.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                throw Unsupported($"time travel unit '{at.Unit}' (use VERSION or TIMESTAMP)");
            }
            return QueryScan(schemaName, tableName, version, timestamp, where);
        }

        // A pushed filter uses the QueryAsync path (file/stats skipping).
        if (where is not null)
        {
            return QueryScan(schemaName, tableName, version: null, timestamp: null, where);
        }

        // No filter, no time travel: the sanitized kernel read (schema == the bound table.Schema()).
        OwnedArrowTable owned;
        using (var table = Open(schemaName, tableName))
        {
            owned = Run(table.ReadAsArrowTableAsync(default));
        }
        return new AsyncEnumerableArrowStream(owned.Table.Schema, TableBatches(owned.Table), owner: owned);
    }

    // Reads via QueryAsync (DataFusion), optionally at a version/timestamp (time travel) and/or with a pushed
    // WHERE. Materialized + advertises the actual batch schema (delta-rs emits Utf8View for strings; a fixed
    // table.Schema() would mismatch arrow_ingest). Used for both time travel and filter pushdown — neither goes
    // through the kernel read path (which can't read a non-latest snapshot).
    private IArrowArrayStream QueryScan(string schemaName, string tableName, ulong? version,
                                        DateTimeOffset? timestamp, string? where)
    {
        using var t = Open(schemaName, tableName, version);
        if (timestamp is { } ts)
        {
            Run(t.LoadDateTimeAsync(ts, default));
        }
        var sql = where is null ? "SELECT * FROM src" : $"SELECT * FROM src WHERE {where}";
        var query = new SelectQuery(sql) { TableAlias = "src" };
        var batches = new List<RecordBatch>();
        Schema? schema = null;
        foreach (var b in t.QueryAsync(query, default).ToBlockingEnumerable())
        {
            schema ??= b.Schema;
            batches.Add(b);
        }
        schema ??= t.Schema();
        return new InMemoryArrayStream(schema, batches);
    }

    // Reads the one-row filter-constants batch into scalars (indexed by FilterNode.val/vals). Consumes the stream.
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

    // Renders a superset-safe FilterNode as a DataFusion WHERE fragment. Anything not safely renderable
    // becomes "TRUE" (dropped) — correct because DuckDB re-applies every predicate above the scan.
    private static string BuildWhere(FilterNode node, IReadOnlyList<object?> values)
    {
        switch (node.Op)
        {
            case "and":
            {
                if (node.Children is not { Count: > 0 })
                {
                    return "TRUE";
                }
                var parts = node.Children.Select(c => BuildWhere(c, values)).Where(p => p != "TRUE").ToList();
                return parts.Count == 0 ? "TRUE" : "(" + string.Join(" AND ", parts) + ")";
            }
            case "or":
            {
                if (node.Children is not { Count: > 0 })
                {
                    return "TRUE";
                }
                var parts = node.Children.Select(c => BuildWhere(c, values)).ToList();
                // A dropped OR branch widens the whole OR to everything → the entire OR must become TRUE.
                return parts.Any(p => p == "TRUE") ? "TRUE" : "(" + string.Join(" OR ", parts) + ")";
            }
            case "compare":
            {
                var op = SqlCmp(node.Cmp);
                var lit = node.Val is int vi && vi >= 0 && vi < values.Count ? Literal(values[vi]) : null;
                return (op != null && lit != null && node.Col != null) ? $"{Q(node.Col)} {op} {lit}" : "TRUE";
            }
            case "is_null":
                return node.Col != null ? $"{Q(node.Col)} IS NULL" : "TRUE";
            case "is_not_null":
                return node.Col != null ? $"{Q(node.Col)} IS NOT NULL" : "TRUE";
            case "in":
            {
                if (node.Col is null || node.Vals is not { Count: > 0 })
                {
                    return "TRUE";
                }
                var lits = node.Vals.Select(i => i >= 0 && i < values.Count ? Literal(values[i]) : null).ToList();
                return lits.Any(l => l is null) ? "TRUE" : $"{Q(node.Col)} IN ({string.Join(", ", lits)})";
            }
            default:
                return "TRUE";
        }
    }

    private static string? SqlCmp(string? cmp) => cmp switch
    {
        "=" => "=", "<>" => "<>", "<" => "<", "<=" => "<=", ">" => ">", ">=" => ">=",
        "is_distinct" => "IS DISTINCT FROM", "is_not_distinct" => "IS NOT DISTINCT FROM",
        _ => null,
    };

    // A safe SQL literal for a filter constant, or null if not renderable (→ predicate dropped to TRUE).
    private static string? Literal(object? value) => value switch
    {
        null => null,
        string s => "'" + s.Replace("'", "''") + "'",
        bool b => b ? "TRUE" : "FALSE",
        sbyte or byte or short or ushort or int or uint or long or ulong => System.Convert.ToString(
            value, System.Globalization.CultureInfo.InvariantCulture),
        float or double or decimal => ((System.IFormattable)value).ToString(null,
            System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

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

    // INSERT / CTAS / COPY. Semantics (matching the engineered-wood provider): createTable (CTAS + COPY's
    // default) or replace (CREATE OR REPLACE) => the table becomes exactly these rows (Overwrite); a plain
    // INSERT or COPY with CREATE_TABLE false => Append. COPY SCHEMA_MODE 'overwrite' replaces data + adopts the
    // incoming schema (InsertOptions.OverwriteSchema → delta-rs SchemaMode::Overwrite). SCHEMA_MODE 'merge'
    // appends + unions new source columns (old rows read NULL): a plain Append with OverwriteSchema=false
    // already maps to delta-rs SchemaMode::Merge in the bridge, so merge = force Append.
    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId, IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode, bool partitionOverwrite)
    {
        if (partitionOverwrite)
        {
            throw new NotSupportedException(
                "deltars provider: COPY PARTITION_OVERWRITE is not supported yet (use the engineeredwooddelta "
                + "provider, or delta-rs replace_where via a future MERGE surface).");
        }
        // sortColumns (SORTED BY) is a warehouse CLUSTER BY concept — Delta doesn't cluster; ignored.
        bool merge = string.Equals(schemaMode, "merge", StringComparison.OrdinalIgnoreCase);
        bool overwriteSchema = string.Equals(schemaMode, "overwrite", StringComparison.OrdinalIgnoreCase);
        var schema = data.Schema;

        // Create the table only if it doesn't exist yet (empty, schema-only + CDF config/partitions), then
        // write. An existing table with createTable=true (COPY default / CTAS-replace) is OVERWRITTEN, not an
        // error — the previous ErrorIfExists broke COPY into an existing table.
        ITable? existing = TryOpen(schemaName, tableName);
        ITable table;
        bool overwrite;
        if (existing is null)
        {
            var create = new TableCreateOptions(TableUri(schemaName, tableName), schema)
            {
                StorageOptions = _storage,
                PartitionBy = (partitionColumns ?? new List<string>()).ToList(),
                SaveMode = SaveMode.ErrorIfExists,
                Configuration = CreateConfiguration(),
            };
            table = Run(_engine.CreateTableAsync(create, default));
            overwrite = false; // fresh empty table created with this schema → Append == the rows
        }
        else
        {
            table = existing;
            // merge is always an append (schema union); otherwise createTable/replace/overwrite => overwrite.
            overwrite = !merge && (createTable || replace || overwriteSchema);
        }

        using (table)
        {
            var (batches, rows) = ReadAll(data);
            try
            {
                if (batches.Count > 0)
                {
                    // OverwriteSchema only when replacing an existing table's schema (a fresh table already has
                    // this schema; Append + OverwriteSchema is invalid). Append with OverwriteSchema=false is
                    // SchemaMode::Merge in the bridge — that IS the 'merge' behavior (union new columns).
                    bool adoptSchema = overwriteSchema && existing is not null;
                    Run(table.InsertAsync(batches, schema, new InsertOptions
                    {
                        SaveMode = overwrite ? SaveMode.Overwrite : SaveMode.Append,
                        OverwriteSchema = adoptSchema,
                    }, default));
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

    /// <summary>Opens the table if it exists, else null (a table-not-found error becomes "does not exist").</summary>
    private ITable? TryOpen(string schemaName, string tableName)
    {
        try
        {
            return Open(schemaName, tableName);
        }
        catch
        {
            return null;
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
            Configuration = CreateConfiguration(),
        };
        using var table = Run(_engine.CreateTableAsync(create, default));
    }

    public void DropTable(string schemaName, string tableName, bool ifExists)
    {
        if (_oneLake)
        {
            // Recursive DFS delete of the table folder (idempotent). The GUID-based abfss TableUri maps to the
            // correct OneLake DFS URL, same as reads. Reuses the FabricLakehouse delete the engineered-wood
            // provider uses live.
            var (tenant, clientId, clientSecret) = OneLakeCreds();
            FabricLakehouse.DeleteOneLakeDirectory(TableUri(schemaName, tableName), tenant, clientId, clientSecret);
            return;
        }
        if (!RootIsLocal)
        {
            throw Unsupported("DROP TABLE on a non-local delta-rs catalog (S3/plain-ADLS recursive delete deferred)");
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

    /// <summary>The SP fields (tenant/client/secret) StorageOptionsCodec mapped into <see cref="_storage"/> —
    /// used to mint a credential for the OneLake DFS management ops (DROP). Nulls fall back to a default chain.</summary>
    private (string? Tenant, string? ClientId, string? ClientSecret) OneLakeCreds()
    {
        _storage.TryGetValue("azure_storage_tenant_id", out var tenant);
        _storage.TryGetValue("azure_storage_client_id", out var clientId);
        _storage.TryGetValue("azure_storage_client_secret", out var clientSecret);
        return (tenant, clientId, clientSecret);
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

    // DELETE via a record-batch MERGE: the scanned rows (keys = all columns) are the source; delete every
    // target row matching one of them on ALL columns (NULL-safe). DuckDB never hands us the WHERE, so this is
    // the rowid route mapped onto delta-rs's MERGE — see docs/delta-rs-provider.md "The DML crux".
    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys)
    {
        var schema = keys.Schema;
        var (batches, rows) = ReadAll(keys);
        if (rows == 0)
        {
            foreach (var b in batches) { b.Dispose(); }
            return 0;
        }
        var cols = schema.FieldsList.Select(f => f.Name).ToList();
        string on = string.Join(" AND ", cols.Select(c => NullSafeEq($"target.{Q(c)}", $"source.{Q(c)}")));
        string query = $"MERGE INTO target USING source ON {on} WHEN MATCHED THEN DELETE";
        using var table = Open(schemaName, tableName);
        try
        {
            Run(table.MergeAsync(query, batches, schema, default));
            return rows;
        }
        finally
        {
            foreach (var b in batches) { b.Dispose(); }
        }
    }

    // UPDATE via a record-batch MERGE: data = [setCols ++ keyCols(all columns)]. Match target on the key
    // columns (NULL-safe), UPDATE SET the set columns. Source columns are renamed (s__/k__) to avoid the
    // set/key name overlap. NOTE: if the pre-image row is duplicated, delta-rs may reject the ambiguous
    // multi-match — acceptable v1 (identical rows can't be selectively updated by a WHERE anyway).
    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data)
    {
        var fields = data.Schema.FieldsList;
        var (batches, rows) = ReadAll(data);
        if (rows == 0)
        {
            foreach (var b in batches) { b.Dispose(); }
            return 0;
        }
        var setNames = fields.Take(setColumnCount).Select(f => f.Name).ToList();
        var keyNames = fields.Skip(setColumnCount).Select(f => f.Name).ToList();
        var renamedSchema = RenameSchema(data.Schema, setColumnCount);
        var renamed = batches.Select(b => RenameBatch(b, setColumnCount)).ToList();

        string on = string.Join(" AND ",
            keyNames.Select(c => NullSafeEq($"target.{Q(c)}", $"source.{Q("k__" + c)}")));
        string set = string.Join(", ", setNames.Select(c => $"{Q(c)} = source.{Q("s__" + c)}"));
        string query = $"MERGE INTO target USING source ON {on} WHEN MATCHED THEN UPDATE SET {set}";
        using var table = Open(schemaName, tableName);
        try
        {
            Run(table.MergeAsync(query, renamed, renamedSchema, default));
            return rows;
        }
        finally
        {
            // renamed batches share the originals' arrays — dispose only the originals (single ownership).
            foreach (var b in batches) { b.Dispose(); }
        }
    }

    private static string NullSafeEq(string a, string b) => $"(({a} = {b}) OR ({a} IS NULL AND {b} IS NULL))";

    // Quote a DataFusion identifier.
    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static Schema RenameSchema(Schema schema, int setColumnCount)
    {
        var fields = schema.FieldsList;
        var renamed = new List<Field>(fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            var prefix = i < setColumnCount ? "s__" : "k__";
            renamed.Add(new Field(prefix + fields[i].Name, fields[i].DataType, fields[i].IsNullable));
        }
        return new Schema(renamed, schema.Metadata);
    }

    private static RecordBatch RenameBatch(RecordBatch batch, int setColumnCount)
    {
        var arrays = new IArrowArray[batch.ColumnCount];
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            arrays[i] = batch.Column(i);
        }
        return new RecordBatch(RenameSchema(batch.Schema, setColumnCount), arrays, batch.Length);
    }

    private IReadOnlyList<string> RowIdColumns(string schema, string table)
    {
        using var t = Open(schema, table);
        return t.Schema().FieldsList.Select(f => f.Name).ToList();
    }

    // ALTER TABLE. ADD COLUMN is a metadata-only schema evolution done via a 0-row merge-append (a plain
    // Append with OverwriteSchema=false maps to delta-rs SchemaMode::Merge, which unions the widened schema —
    // old rows read NULL, no data written). Works on every backend (object_store), no engineered-wood IO seam
    // and no delta-rs Rust amendment. RENAME TABLE moves the table folder (local only for now). Other kinds
    // (RENAME/DROP COLUMN, ALTER TYPE) need column mapping / a rewrite → clean error.
    public void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2,
                           Field? column, int flags)
    {
        switch (alterKind)
        {
            case AlterKind.AddColumn:
            {
                var col = column ?? throw new InvalidOperationException(
                    "delta-rs ADD COLUMN requires a column definition.");
                string name = string.IsNullOrEmpty(arg1) ? col.Name : arg1!;
                using var t = Open(schemaName, tableName);
                var current = t.Schema();
                var fields = new List<Field>(current.FieldsList) { new Field(name, col.DataType, nullable: true) };
                var widened = new Schema(fields, current.Metadata);
                var arrays = new IArrowArray[fields.Count];
                for (int i = 0; i < fields.Count; i++)
                {
                    arrays[i] = EmptyArray(fields[i].DataType);
                }
                var empty = new RecordBatch(widened, arrays, 0);
                // 0-row Append → SchemaMode::Merge (bridge) → the column is added, existing rows read NULL.
                Run(t.InsertAsync(new[] { empty }, widened,
                    new InsertOptions { SaveMode = SaveMode.Append, OverwriteSchema = false }, default));
                return;
            }
            case AlterKind.RenameTable:
            {
                string newName = arg1 ?? throw new InvalidOperationException(
                    "delta-rs RENAME TABLE requires a new table name.");
                if (!RootIsLocal)
                {
                    throw Unsupported("RENAME TABLE on a non-local delta-rs catalog (cloud rename deferred)");
                }
                Directory.Move(LocalTableDir(schemaName, tableName), LocalTableDir(schemaName, newName));
                return;
            }
            default:
                throw Unsupported("ALTER TABLE (only ADD COLUMN and RENAME TABLE are supported on delta-rs; "
                    + "RENAME/DROP COLUMN + ALTER TYPE need column mapping)");
        }
    }

    /// <summary>An empty (0-length) Arrow array of the given type, for the widened schema of a 0-row
    /// merge-append (ADD COLUMN). Covers the Delta-valid column types.</summary>
    private static IArrowArray EmptyArray(IArrowType type) => type switch
    {
        BooleanType => new BooleanArray.Builder().Build(),
        Int8Type => new Int8Array.Builder().Build(),
        Int16Type => new Int16Array.Builder().Build(),
        Int32Type => new Int32Array.Builder().Build(),
        Int64Type => new Int64Array.Builder().Build(),
        UInt8Type => new UInt8Array.Builder().Build(),
        UInt16Type => new UInt16Array.Builder().Build(),
        UInt32Type => new UInt32Array.Builder().Build(),
        UInt64Type => new UInt64Array.Builder().Build(),
        FloatType => new FloatArray.Builder().Build(),
        DoubleType => new DoubleArray.Builder().Build(),
        Decimal128Type d => new Decimal128Array.Builder(d).Build(),
        StringType => new StringArray.Builder().Build(),
        Date32Type => new Date32Array.Builder().Build(),
        TimestampType ts => new TimestampArray.Builder(ts).Build(),
        _ => throw new NotSupportedException(
            $"delta-rs ADD COLUMN: table column type {type.TypeId} is not supported for the schema-merge write."),
    };

    public IArrowArrayStream ExecuteQuery(string sql) => throw Unsupported("raw query");

    // Maintenance command dialect (delta-rs ops engineered-wood lacks), invoked via
    // mssql_net_exec('<catalog>', '<cmd>'):
    //   OPTIMIZE <table> [ZORDER (c1, c2, ...)]      -- bin-pack, or Z-order clustering
    //   VACUUM   <table> [RETAIN <hours> HOURS] [DRY RUN]
    //   CHECKPOINT <table>
    // <table> is '<schema>.<table>' (schema defaults to 'main'). Returns 0 (these aren't row-affecting DML).
    public long ExecuteNonQuery(string sql)
    {
        var text = (sql ?? string.Empty).Trim();
        var tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw Unsupported($"exec '{text}' — expected OPTIMIZE|VACUUM|CHECKPOINT <table> …");
        }
        var (schema, table) = SplitTableRef(tokens[1]);
        using var t = Open(schema, table);
        switch (tokens[0].ToUpperInvariant())
        {
            case "OPTIMIZE":
            {
                var options = new OptimizeOptions();
                if (HasToken(tokens, "ZORDER") || HasToken(tokens, "Z-ORDER"))
                {
                    options.OptimizeType = OptimizeType.ZOrder;
                    options.ZOrderColumns = ParseParenColumns(text);
                }
                Run(t.OptimizeAsync(options, default));
                return 0;
            }
            case "VACUUM":
            {
                var options = new VacuumOptions { VacuumMode = VacuumMode.Full };
                int r = TokenIndex(tokens, "RETAIN");
                if (r >= 0 && r + 1 < tokens.Length &&
                    ulong.TryParse(tokens[r + 1], out var hours))
                {
                    options.RetentionHours = hours;
                }
                if (HasToken(tokens, "DRY"))
                {
                    options.DryRun = true;
                }
                Run(t.VacuumAsync(options, default));
                return 0;
            }
            case "CHECKPOINT":
                Run(t.CheckpointAsync(default));
                return 0;
            default:
                throw Unsupported($"exec verb '{tokens[0]}' — supported: OPTIMIZE, VACUUM, CHECKPOINT");
        }
    }

    private static (string Schema, string Table) SplitTableRef(string reference)
    {
        int dot = reference.IndexOf('.');
        return dot >= 0
            ? (reference.Substring(0, dot), reference.Substring(dot + 1))
            : (MainSchema, reference);
    }

    private static bool HasToken(string[] tokens, string token) => TokenIndex(tokens, token) >= 0;

    private static int TokenIndex(string[] tokens, string token)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static IReadOnlyList<string> ParseParenColumns(string text)
    {
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return System.Array.Empty<string>();
        }
        return text.Substring(open + 1, close - open - 1)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Trim('"', '[', ']', '`'))
            .ToList();
    }
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
