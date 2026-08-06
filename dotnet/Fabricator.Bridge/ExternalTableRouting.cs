using System;
using System.Collections.Generic;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// Storage-side WRITE routing for a SQL Server <b>external table</b> over S3
/// (docs/create-table-with-options.md slice C): SQL Server itself can never INSERT into an S3 external
/// table (and can never write Delta at all), so a detected DELTA/PARQUET external table's INSERT is
/// served by writing DIRECTLY to the storage the table points at — a Delta append via a transient
/// engineered-wood catalog (the C# mirror of the <c>COPY … (FORMAT delta)</c> transient-catalog shape),
/// or one new parquet file into the folder. SQL Server then serves the reads as usual.
///
/// <para><b>Endpoint asymmetry</b>: the external DATA SOURCE's LOCATION host (<c>s3://minio:9000/</c>) is
/// SQL Server's network view and is DISCARDED — the client-side write resolves a DuckDB <c>s3</c> secret
/// by bucket scope, whose ENDPOINT is authoritative (the FABRICATOR_S3_ENDPOINT vs
/// FABRICATOR_S3_SQL_ENDPOINT split). No matching secret surfaces as the write's own credential error.</para>
///
/// <para><b>Semantics</b>: statement-atomic — the Delta append is parked per (txn, table) inside the
/// transient catalog and flushed as ONE Delta commit at the end of this call; a parquet PUT completes
/// atomically. It does NOT join a surrounding explicit DuckDB transaction (the caller guards).</para>
/// </summary>
public static class ExternalTableRouting
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql.External");

    /// <summary>True when the host query surface is available (needed for the native parquet writes).</summary>
    public static bool CanRoute => Host.CanQuery;

    /// <summary>
    /// Composes the client-side table URI from the SQL-side EXTERNAL DATA SOURCE location + the external
    /// table's LOCATION. Null when the data source is not a scheme we can write (not routable).
    /// </summary>
    /// <remarks>
    /// <para>Two schemes, and they compose DIFFERENTLY, which is the whole reason this is one function
    /// rather than a prefix test:</para>
    /// <list type="bullet">
    ///   <item><c>s3://host[:port]/</c> — the host is SQL Server's network view and is DISCARDED; the
    ///     table LOCATION already carries <c>/bucket/path</c>, so the client URI is
    ///     <c>s3://bucket/path</c> and the DuckDB s3 secret's ENDPOINT is authoritative (the
    ///     FABRICATOR_S3_ENDPOINT vs FABRICATOR_S3_SQL_ENDPOINT split).</item>
    ///   <item><c>adls://&lt;fs&gt;@&lt;acct&gt;.dfs.core.windows.net</c> — the authority is NOT discarded:
    ///     it names the filesystem and the real endpoint, and the table LOCATION is only the path WITHIN
    ///     that filesystem. So the client URI is the authority re-spelled as
    ///     <c>abfss://&lt;fs&gt;@&lt;acct&gt;…/&lt;location&gt;</c> — the same account, addressed by the
    ///     scheme our filesystem understands. (SQL Server rejects <c>abfss://</c> as a LOCATION and we
    ///     reject <c>adls://</c> as a root; the two never agree on a spelling, only on an account.)</item>
    /// </list>
    /// <para><b><c>abs://</c> (the blob endpoint) is deliberately NOT routed.</b> It names the same account
    /// through <c>.blob.core.windows.net</c>, and turning that into a DFS endpoint means rewriting the host
    /// — safe for the public cloud and wrong for sovereign clouds, private endpoints and any custom DNS. A
    /// clean "not routable" beats a guessed hostname that writes somewhere unintended; use an
    /// <c>adls://</c> data source for a writable external table. It stays fully READABLE either way.</para>
    /// </remarks>
    public static string? ComposeStorageUri(string? dataSourceLocation, string? tableLocation)
    {
        if (string.IsNullOrWhiteSpace(dataSourceLocation) || string.IsNullOrWhiteSpace(tableLocation))
        {
            return null;
        }
        var src = dataSourceLocation!.Trim();
        var rel = tableLocation!.Replace('\\', '/').Trim('/');
        if (rel.Length == 0)
        {
            return null;
        }
        if (src.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            return "s3://" + rel;
        }
        const string adls = "adls://";
        if (src.StartsWith(adls, StringComparison.OrdinalIgnoreCase))
        {
            var authority = src.Substring(adls.Length).TrimEnd('/');
            // Require <filesystem>@<host>: without the '@' this is not an addressable ADLS Gen2 endpoint and
            // we would silently build a nonsense abfss URI.
            return authority.Contains('@') ? "abfss://" + authority + "/" + rel : null;
        }
        return null;
    }

    /// <summary>
    /// Appends <paramref name="data"/> to the Delta table at <paramref name="tableUri"/> as ONE Delta
    /// commit: a transient delta-provider catalog over the parent folder (flat layout, native_write),
    /// BulkInsert (parks the append) + CommitTransaction (flushes it) — the C# mirror of the COPY
    /// (FORMAT delta) finalize. The ambient opener/txn on THIS thread drive secret resolution + buffer
    /// keying (the SQL bulk consumer re-established them). Appends never change table features, so a
    /// protocol-1.0 SQL-readable table stays readable.
    /// </summary>
    public static long AppendDelta(string tableUri, IArrowArrayStream data, bool checkConstraints, long txnId)
    {
        var uri = tableUri.TrimEnd('/');
        var (root, leaf) = SplitTable(uri);
        Log.LogInformation("external delta append: {Uri} (root={Root}, table={Leaf})", uri, root, leaf);
        var catalog = BackendRegistry.Resolve("delta").OpenCatalog(root, "{\"native_write\":\"true\"}");
        try
        {
            long rows = catalog.BulkInsert("main", leaf, data, createTable: false, replace: false,
                                           checkConstraints, txnId, partitionColumns: null, sortColumns: null,
                                           schemaMode: null, partitionOverwrite: false, optionsJson: null);
            // Flush the parked append as ONE Delta commit (statement-atomic; the transient catalog serves
            // exactly this statement, so the per-txn buffer holds only this append).
            catalog.CommitTransaction();
            return rows;
        }
        catch
        {
            try { catalog.RollbackTransaction(); } catch { /* discard-only backstop */ }
            throw;
        }
        finally
        {
            catalog.Dispose();
        }
    }

    // ---- CREATE ... WITH (location=..., table_type=...) — the CETAS-analog client-side write (slice B) ---

    /// <summary>CTAS data write for a `CREATE TABLE ... WITH (location=..., table_type='DELTA') AS ...`:
    /// creates the Delta table at <paramref name="tableUri"/> and streams the data — protocol-1.0 plain
    /// (`deletion_vectors false, column_mapping 'none'` — SQL Server's DELTA reader requirement), one
    /// create+write. An EXISTING Delta table at the location fails (the transient catalog's 'error'
    /// disposition) — this is CREATE, not OR REPLACE.</summary>
    public static long CreateDeltaAs(string tableUri, IArrowArrayStream data, long txnId,
                                     IReadOnlyList<string>? partitionColumns, IReadOnlyList<string>? sortColumns)
    {
        var (root, leaf) = SplitTable(tableUri);
        Log.LogInformation("external delta CREATE AS: {Uri}", tableUri);
        var catalog = BackendRegistry.Resolve("delta").OpenCatalog(root,
            "{\"native_write\":\"true\",\"deletion_vectors\":\"false\",\"column_mapping\":\"none\","
            + "\"copy_disposition\":\"error\"}");
        try
        {
            long rows = catalog.BulkInsert("main", leaf, data, createTable: true, replace: false,
                                           checkConstraints: false, txnId, partitionColumns, sortColumns,
                                           schemaMode: null, partitionOverwrite: false, optionsJson: null);
            catalog.CommitTransaction();
            return rows;
        }
        catch
        {
            try { catalog.RollbackTransaction(); } catch { /* discard-only backstop */ }
            throw;
        }
        finally
        {
            catalog.Dispose();
        }
    }

    /// <summary>Empty-CREATE counterpart (commit-0 only): the Delta table at <paramref name="tableUri"/> with
    /// the declared columns — the identity marker rides through (`delta.identity.*` on the marked fields), so
    /// the created external table is slice-D DML-capable from birth.</summary>
    public static void CreateDeltaEmpty(string tableUri, Schema columns,
                                        IReadOnlyList<string>? partitionColumns,
                                        IReadOnlyList<string>? sortColumns,
                                        IReadOnlyList<string>? identityColumns)
    {
        var (root, leaf) = SplitTable(tableUri);
        Log.LogInformation("external delta CREATE (empty): {Uri}", tableUri);
        var catalog = BackendRegistry.Resolve("delta").OpenCatalog(root,
            "{\"native_write\":\"true\",\"deletion_vectors\":\"false\",\"column_mapping\":\"none\"}");
        try
        {
            catalog.CreateTable("main", leaf, columns, ifNotExists: false, primaryKey: null, uniques: null,
                                defaults: null, partitionColumns, sortColumns, identityColumns,
                                optionsJson: null);
        }
        finally
        {
            catalog.Dispose();
        }
    }

    // ---- identity-keyed UPDATE/DELETE (docs/create-table-with-options.md slice D) ------------------------
    // A Delta IDENTITY column bridges the two rowid domains: it is a REAL data column (the PolyBase scan
    // reads it), engine-assigned unique, trivially stable across rewrites, and it has standard min/max
    // stats in the Delta log — so the SQL-side scan produces identity values as the rowid, and the Delta
    // side resolves them back to transient (file, position) rowids with a PRUNED scan. Identity values are
    // SNAPSHOT-INDEPENDENT (unlike transient rowids, which are only valid within one snapshot), so the two
    // sides need not agree on a version: a row concurrently deleted between scan and DML simply matches
    // nothing, and the per-statement OCC retry stays safe — exactly like identity appends.

    /// <summary>The Delta table's engine-assigned IDENTITY column (<c>delta.identity.*</c> field metadata on
    /// a BIGINT column), or null. One cached call per detected external table (a `_delta_log` open).</summary>
    public static string? FindDeltaIdentityColumn(string tableUri)
    {
        try
        {
            var schema = DeltaReader.GetSchema(AmbientOpener.Current, tableUri);
            // GetSchema returns the TRANSPORT dialect (it is a bind schema) — canonicalise for EW's converter.
            var ew = EngineeredWood.DeltaLake.Schema.SchemaConverter.FromArrowSchema(
                VariantMarker.ToCanonicalSchema(schema));
            foreach (var f in ew.Fields)
            {
                if (EngineeredWood.DeltaLake.Schema.IdentityColumn.GetConfig(f) is not null)
                {
                    return f.Name;
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug("identity probe failed for {Uri} (external DML stays unavailable): {Msg}",
                tableUri, ex.Message);
        }
        return null;
    }

    /// <summary>DELETE by identity values: resolve each id to its transient rowid with a pruned scan, then
    /// the delta provider's own rowid DELETE (DV or copy-on-write per table config — CoW keeps a
    /// protocol-1.0 table SQL-readable). Unresolved ids (concurrently deleted) match nothing.</summary>
    public static long DeleteByIdentity(string tableUri, string identityColumn, IArrowArrayStream keys, long txnId)
    {
        // Consume + dispose the (imported) key stream FIRST — an imported C stream left to the GC finalizer
        // outlives the C++ side's struct and segfaults; every exit below must not leak it.
        var ids = CollectInt64(keys);
        var (root, leaf) = SplitTable(tableUri);
        var catalog = BackendRegistry.Resolve("delta").OpenCatalog(root, "{\"native_write\":\"true\"}");
        try
        {
            if (ids.Count == 0)
            {
                return 0;
            }
            var map = ResolveRowIds(catalog, leaf, identityColumn, ids);
            Log.LogInformation("external delta DELETE {Uri}: {Ids} id(s) -> {Rows} resolved row(s)",
                tableUri, ids.Count, map.Count);
            if (map.Count == 0)
            {
                return 0;
            }
            var rowIds = new Int64Array.Builder().Reserve(map.Count);
            foreach (var rid in map.Values)
            {
                rowIds.Append(rid);
            }
            var keySchema = new Schema(
                new[] { new Field(DeltaCatalog.RowIdColumn, Int64Type.Default, nullable: false) }, null);
            var batch = new RecordBatch(keySchema, new IArrowArray[] { rowIds.Build() }, map.Count);
            long n = catalog.ExecuteDelete("main", leaf, new InMemoryArrayStream(keySchema, new[] { batch }));
            catalog.CommitTransaction(); // autocommit DELETE commits directly; this is the parked-state backstop
            return n;
        }
        catch
        {
            try { catalog.RollbackTransaction(); } catch { /* discard-only backstop */ }
            throw;
        }
        finally
        {
            catalog.Dispose();
        }
    }

    /// <summary>UPDATE keyed by identity values: <paramref name="data"/> is the DuckDB update stream
    /// (<paramref name="setColumnCount"/> SET columns ++ ONE trailing identity key column). Each key resolves
    /// to its transient rowid (unresolved ⇒ NULL ⇒ the delta update skips the row — concurrently deleted
    /// matches nothing), then the delta provider's own rowid UPDATE runs (MoR/CoW per table config).</summary>
    public static long UpdateByIdentity(string tableUri, string identityColumn, int setColumnCount,
                                        IArrowArrayStream data)
    {
        // Owned copies (the C-ABI input batches don't outlive the stream) — bounded by the statement's
        // matched rows, same as the SQL provider's parameterized UPDATE batches. The imported stream is
        // consumed + disposed HERE, deterministically (a GC-finalized imported stream segfaults).
        Schema schema;
        List<RecordBatch> batches;
        using (data)
        {
            (schema, batches, _) = DeltaWriter.Materialize(data, default);
        }
        var (root, leaf) = SplitTable(tableUri);
        var catalog = BackendRegistry.Resolve("delta").OpenCatalog(root, "{\"native_write\":\"true\"}");
        try
        {
            if (batches.Count == 0)
            {
                return 0;
            }
            if (schema.FieldsList.Count != setColumnCount + 1)
            {
                throw new NotSupportedException(
                    "external-table UPDATE expects exactly one identity key column (compound keys are not "
                    + "identity-keyed).");
            }
            var ids = new List<long>();
            foreach (var b in batches)
            {
                AppendInt64(b.Column(setColumnCount), ids);
            }
            var map = ResolveRowIds(catalog, leaf, identityColumn, ids);
            Log.LogInformation("external delta UPDATE {Uri}: {Ids} id(s) -> {Rows} resolved row(s)",
                tableUri, ids.Count, map.Count);
            if (map.Count == 0)
            {
                return 0;
            }
            // Rebuild each batch: SET columns pass through; the key column becomes the resolved transient
            // rowid (NULL where unresolved — the delta update's parser skips NULL rowids).
            var fields = new List<Field>(schema.FieldsList.Count);
            for (int j = 0; j < setColumnCount; j++)
            {
                fields.Add(schema.FieldsList[j]);
            }
            fields.Add(new Field(DeltaCatalog.RowIdColumn, Int64Type.Default, nullable: true));
            var outSchema = new Schema(fields, null);
            var outBatches = new List<RecordBatch>(batches.Count);
            foreach (var b in batches)
            {
                var rid = new Int64Array.Builder().Reserve(b.Length);
                var keyVals = new List<long?>(b.Length);
                AppendInt64Nullable(b.Column(setColumnCount), keyVals);
                foreach (var id in keyVals)
                {
                    if (id is { } v && map.TryGetValue(v, out var r))
                    {
                        rid.Append(r);
                    }
                    else
                    {
                        rid.AppendNull();
                    }
                }
                var cols = new IArrowArray[setColumnCount + 1];
                for (int j = 0; j < setColumnCount; j++)
                {
                    cols[j] = b.Column(j);
                }
                cols[setColumnCount] = rid.Build();
                outBatches.Add(new RecordBatch(outSchema, cols, b.Length));
            }
            long n = catalog.ExecuteUpdate("main", leaf, setColumnCount,
                                           new InMemoryArrayStream(outSchema, outBatches));
            catalog.CommitTransaction();
            return n;
        }
        catch
        {
            try { catalog.RollbackTransaction(); } catch { /* discard-only backstop */ }
            throw;
        }
        finally
        {
            catalog.Dispose();
        }
    }

    // identity value -> transient (file, position) rowid, via chunked PRUNED scans: the IN predicate rides
    // the spec's FilterNode + value batch, so the delta side skips files by the identity column's standard
    // min/max stats. Pushdown is superset-safe — the wanted-set re-filter here is the exact predicate.
    private static Dictionary<long, long> ResolveRowIds(IBackendCatalog catalog, string leaf,
                                                        string identityColumn, IReadOnlyList<long> ids)
    {
        const int ChunkSize = 500;
        var want = new HashSet<long>(ids);
        var map = new Dictionary<long, long>();
        var chunk = new List<long>(ChunkSize);
        for (int i = 0; i < ids.Count; i++)
        {
            chunk.Add(ids[i]);
            if (chunk.Count == ChunkSize || i == ids.Count - 1)
            {
                ResolveChunk(catalog, leaf, identityColumn, chunk, want, map);
                chunk.Clear();
            }
        }
        return map;
    }

    private static void ResolveChunk(IBackendCatalog catalog, string leaf, string identityColumn,
                                     IReadOnlyList<long> chunk, HashSet<long> want, Dictionary<long, long> map)
    {
        var vals = new List<int>(chunk.Count);
        var valueFields = new List<Field>(chunk.Count);
        var valueArrays = new IArrowArray[chunk.Count];
        for (int i = 0; i < chunk.Count; i++)
        {
            vals.Add(i);
            valueFields.Add(new Field("v" + i, Int64Type.Default, nullable: false));
            valueArrays[i] = new Int64Array.Builder().Append(chunk[i]).Build();
        }
        var spec = new ScanSpec
        {
            Columns = new List<string> { identityColumn, DeltaCatalog.RowIdColumn },
            Filter = new FilterNode { Op = "in", Col = identityColumn, Vals = vals },
        };
        var valueSchema = new Schema(valueFields, null);
        var values = new RecordBatch(valueSchema, valueArrays, 1);
        using var scan = catalog.ScanTable("main", leaf, JsonSerializer.Serialize(spec),
                                           new InMemoryArrayStream(valueSchema, new[] { values }));
        RecordBatch? b;
        while ((b = scan.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
        {
            using (b)
            {
                int idIdx = b.Schema.GetFieldIndex(identityColumn);
                int ridIdx = b.Schema.GetFieldIndex(DeltaCatalog.RowIdColumn);
                if (idIdx < 0 || ridIdx < 0)
                {
                    throw new InvalidOperationException(
                        "external-table DML: the delta scan did not return the identity + rowid columns.");
                }
                var ridArr = (Int64Array)b.Column(ridIdx);
                var idVals = new List<long?>(b.Length);
                AppendInt64Nullable(b.Column(idIdx), idVals);
                for (int i = 0; i < b.Length; i++)
                {
                    if (idVals[i] is { } id && want.Contains(id) && ridArr.GetValue(i) is { } rid)
                    {
                        map[id] = rid; // engine-assigned unique; a rogue writer's duplicate keeps the last
                    }
                }
            }
        }
    }

    // Drains a single-column key stream (the DuckDB DELETE plan's identity values) to a list.
    private static List<long> CollectInt64(IArrowArrayStream keys)
    {
        var ids = new List<long>();
        using (keys)
        {
            RecordBatch? b;
            while ((b = keys.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
            {
                using (b)
                {
                    if (b.Length > 0)
                    {
                        AppendInt64(b.Column(0), ids);
                    }
                }
            }
        }
        return ids;
    }

    private static void AppendInt64(IArrowArray column, List<long> into)
    {
        var tmp = new List<long?>(column.Length);
        AppendInt64Nullable(column, tmp);
        foreach (var v in tmp)
        {
            if (v is { } x)
            {
                into.Add(x);
            }
        }
    }

    private static void AppendInt64Nullable(IArrowArray column, List<long?> into)
    {
        switch (column)
        {
            case Int64Array a:
                for (int i = 0; i < a.Length; i++) { into.Add(a.GetValue(i)); }
                break;
            case Int32Array a: // an external declaration may narrow the identity column to INT
                for (int i = 0; i < a.Length; i++) { into.Add(a.GetValue(i)); }
                break;
            default:
                throw new NotSupportedException(
                    $"external-table DML: identity key column must be BIGINT/INT (got "
                    + $"{column.Data.DataType.Name}).");
        }
    }

    /// <summary>Splits <c>&lt;root&gt;/&lt;table&gt;</c>, rejecting a URI with no parent folder.</summary>
    /// <remarks>
    /// The check is on the SHAPE of what remains, not on a scheme's length: the old form
    /// (<c>slash &lt;= "s3://".Length</c>) hardcoded s3's prefix and would wave through
    /// <c>abfss://fs@host</c> — whose last slash sits inside <c>://</c> — producing the root <c>abfss:/</c>
    /// and a "table" named after the host. Requiring the root to still be a scheme + non-empty authority
    /// catches that for every scheme.
    /// </remarks>
    private static (string Root, string Leaf) SplitTable(string tableUri)
    {
        var uri = tableUri.TrimEnd('/');
        int slash = uri.LastIndexOf('/');
        string root = slash < 0 ? string.Empty : uri.Substring(0, slash);
        int scheme = root.IndexOf("://", StringComparison.Ordinal);
        if (slash < 0 || scheme < 0 || root.Length <= scheme + 3)
        {
            throw new ArgumentException($"external delta table URI '{tableUri}' has no parent folder.");
        }
        return (root, uri.Substring(slash + 1));
    }

    /// <summary>
    /// Appends <paramref name="data"/> as ONE new parquet file (<c>&lt;uuid&gt;.parquet</c>) in the external
    /// table's folder via the host's native COPY. The external table reads all files under its LOCATION, so
    /// the rows appear once the object completes (an S3 PUT/multipart-complete is atomically visible).
    /// The file's columns are the INSERT stream's columns — DuckDB already bound them to the external
    /// table's declared column list, so the schema matches by construction.
    /// </summary>
    public static long AppendParquet(string folderUri, IArrowArrayStream data)
    {
        var file = folderUri.TrimEnd('/') + "/" + Guid.NewGuid().ToString("N") + ".parquet";
        Log.LogInformation("external parquet append: {File}", file);
        // Unique per call + dropped afterwards — see BoundInput.
        string inputName = BoundInput.NextName("__fabricator_external_insert");
        var sql = $"COPY (SELECT * FROM \"{inputName}\") TO '{file.Replace("'", "''")}' (FORMAT parquet)";
        try
        {
            using var result = Host.Query(sql, new (string, IArrowArrayStream)[] { (inputName, data) });
            var batch = result.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            long rows = batch is { ColumnCount: > 0, Length: > 0 } && batch.Column(0) is Int64Array c
                        && c.GetValue(0) is long v ? v : 0;
            batch?.Dispose();
            return rows;
        }
        finally
        {
            BoundInput.Drop(inputName);
        }
    }
}
