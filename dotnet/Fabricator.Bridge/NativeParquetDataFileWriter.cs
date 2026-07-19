using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The native Delta <b>write</b> half of the "inversion" (docs/native-delta-write.md): an engineered-wood
/// <see cref="IDataFileWriter"/> that produces each parquet data file with DuckDB's own native parquet writer
/// (via a bound-input <c>COPY … TO … (FORMAT parquet)</c> on a fresh host connection, <see cref="Host.Query"/>)
/// instead of engineered-wood's parquet codec. The <c>_delta_log</c> commit (add/stats/protocol) stays in
/// engineered-wood — only the data bytes move to DuckDB (battle-tested encodings, automatic bloom filters,
/// standard footers). The batch is bound as a connection-scoped Arrow view and copied out to the file the
/// table filesystem maps the relative path to; <c>RETURN_STATS</c> yields the written file's byte size.
/// </summary>
internal sealed class NativeParquetDataFileWriter : IDataFileWriter
{
    private const string InputName = "__fabricator_delta_write_src";
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Delta.Native");

    // The table root as a URI DuckDB's writer can open (onelake:// for OneLake, else the local/s3 path). Files
    // are written to <root>/<relativePath> so they resolve identically to engineered-wood's own _fs mapping.
    private readonly string _writableRoot;
    // Per-write parquet tuning (compression / row-group size) rendered into the COPY options. Null => DuckDB
    // defaults (snappy, 122880-row groups) — the pre-tuning behavior.
    private readonly DeltaWriteSpec? _spec;

    internal NativeParquetDataFileWriter(string tablePath, DeltaWriteSpec? spec = null)
    {
        _writableRoot = DeltaReader.ToReadableRoot(tablePath);
        _spec = spec;
    }

    /// <summary>True when native write is usable (the host registered host_query). Falls back to the built-in
    /// engineered-wood writer otherwise.</summary>
    internal static bool Available => Host.CanQuery;

    public ValueTask<long> WriteAsync(IReadOnlyList<RecordBatch> batches, string relativePath,
                                      CancellationToken cancellationToken)
    {
        if (batches.Count == 0)
        {
            throw new InvalidOperationException("native delta write: no batches to write");
        }
        // Bind the batches as a fresh Arrow stream (the host dequeues + exports each; InMemoryArrayStream only
        // disposes UNdequeued batches, and the C export doesn't free managed buffers — so the caller's batches
        // stay valid for its subsequent stats collection). One parquet file is written from the whole stream.
        // A column-mapping rewrite/compaction hands us batches engineered-wood already annotated with
        // `PARQUET:field_id` (via SetParquetFieldIds); DuckDB's COPY does NOT read Arrow field metadata, so lift
        // those ids into an explicit FIELD_IDS clause — else the native-written file would lose its field ids and
        // an id-mode reader couldn't map it.
        var src = new InMemoryArrayStream(batches[0].Schema, batches);
        var (_, size, _) = RunCopy(_writableRoot, relativePath, src, cancellationToken,
                                   fieldIds: FieldIdsFromMetadata(batches[0].Schema), spec: _spec);
        return new ValueTask<long>(size);
    }

    // Renders the resolved write tuning as native COPY options. COMPRESSION/ROW_GROUP_SIZE are DuckDB parquet
    // COPY options; bloom-filter COLUMNS are an EW-codec-only knob (DuckDB's writer blooms dictionary-encoded
    // columns automatically — WRITE_BLOOM_FILTER true is always set), so BloomFilterColumns is not rendered.
    internal static string CopyTuning(DeltaWriteSpec? spec)
    {
        if (spec is null)
        {
            return string.Empty;
        }
        string s = string.Empty;
        if (spec.Compression is { } codec)
        {
            s += ", COMPRESSION " + DuckDbCodecName(codec);
        }
        if (spec.RowGroupSize is { } rows)
        {
            s += ", ROW_GROUP_SIZE " + rows.ToString(CultureInfo.InvariantCulture);
        }
        return s;
    }

    private static string DuckDbCodecName(EngineeredWood.Compression.CompressionCodec codec) => codec switch
    {
        EngineeredWood.Compression.CompressionCodec.Uncompressed => "uncompressed",
        EngineeredWood.Compression.CompressionCodec.Snappy => "snappy",
        EngineeredWood.Compression.CompressionCodec.Gzip => "gzip",
        EngineeredWood.Compression.CompressionCodec.Brotli => "brotli",
        EngineeredWood.Compression.CompressionCodec.Zstd => "zstd",
        EngineeredWood.Compression.CompressionCodec.Lz4 => "lz4_raw",
        _ => throw new NotSupportedException(
            $"parquet compression '{codec}' is not supported by DuckDB's native parquet writer "
            + "(native_write) — use snappy/zstd/gzip/brotli/lz4/uncompressed."),
    };

    /// <summary>
    /// Streams <paramref name="src"/> (a pull-based Arrow stream — the whole dataset never materializes here) into
    /// <c>&lt;writableRoot&gt;/&lt;relativePath&gt;</c> via DuckDB's native <c>COPY … TO … (FORMAT parquet,
    /// WRITE_BLOOM_FILTER true, RETURN_STATS)</c>, creating the parent directory first (best-effort). Returns the
    /// written file's total (rowCount, sizeBytes) plus — when <paramref name="statsSchema"/> is supplied — the
    /// Delta stats JSON (<c>numRecords/minValues/maxValues/nullCount</c>) built from <c>RETURN_STATS</c>'s
    /// <c>column_statistics</c>. Shared by the per-file <see cref="IDataFileWriter"/> path (which passes
    /// <c>statsSchema=null</c> — engineered-wood computes its own stats over the in-hand batches) and the streaming
    /// bulk-write path (which passes the write schema, since it never holds the batches to stat them itself).
    /// </summary>
    /// <summary>One parquet file written by a COPY, as read back from RETURN_STATS.</summary>
    internal readonly record struct CopiedFile(
        string RelativePath, long Rows, long Size,
        Dictionary<string, string>? PartitionValues, string? Stats);

    internal static (long Rows, long Size, string? Stats) RunCopy(
        string writableRoot, string relativePath, IArrowArrayStream src, CancellationToken ct,
        Schema? statsSchema = null, IReadOnlyDictionary<string, int>? fieldIds = null,
        IReadOnlyDictionary<string, string>? renameToPhysical = null, string? fieldIdsSpec = null,
        DeltaWriteSpec? spec = null)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        var uri = writableRoot + "/" + rel;
        // DuckDB's single-file COPY does NOT create the target's parent directory, so a partitioned file
        // (region=US/<uuid>.parquet) or a _change_data file would fail. Create it first (recursive, idempotent).
        // Best-effort: on an object store (OneLake/S3) directories are implicit — CreateDirectory may be a no-op
        // or unimplemented, and the blob write creates the path anyway, so a failure here is not fatal.
        int slash = rel.LastIndexOf('/');
        if (slash > 0)
        {
            try { HostFs.CreateDir(AmbientOpener.Current, writableRoot + "/" + rel.Substring(0, slash)); }
            catch { /* object-store implicit dirs / unimplemented CreateDirectory — the COPY still writes */ }
        }
        var sql =
            $"COPY (SELECT {SelectList(src.Schema, renameToPhysical)} FROM {InputName}) TO '{uri.Replace("'", "''")}' " +
            "(FORMAT parquet, WRITE_BLOOM_FILTER true, RETURN_STATS" + CopyTuning(spec)
            + (fieldIdsSpec is not null ? ", FIELD_IDS " + fieldIdsSpec : FieldIdsClause(fieldIds)) + ")";
        Log.LogInformation("delta native copy {Uri}", uri);
        var input = new (string, IArrowArrayStream)[] { (InputName, src) };
        using var result = Host.Query(sql, input);
        var files = ReadFileStats(result, ct, statsSchema, writableRoot);
        long rows = 0, size = 0;
        foreach (var f in files) { rows += f.Rows; size += f.Size; }
        return (rows, size, files.Count > 0 ? files[0].Stats : null);
    }

    /// <summary>
    /// One COPY whose source is a raw SQL text instead of a bound Arrow stream — the clustered-OPTIMIZE
    /// rewrite: <c>COPY (&lt;sourceSql&gt;) TO '&lt;root&gt;/&lt;relativePath&gt;'</c>, so the read (per-file
    /// UNION ALL), the global ORDER BY (DuckDB's spilling sort) and the parquet write all run inside ONE
    /// host query — the data never crosses the C ABI. The caller renders any logical→physical renames and
    /// the ORDER BY into <paramref name="sourceSql"/> itself; <paramref name="statsSchema"/> (physical
    /// names, user columns only — row-tracking columns excluded) drives the Delta stats JSON.
    /// </summary>
    internal static List<CopiedFile> RunCopySql(
        string writableRoot, string relativePath, string sourceSql, CancellationToken ct,
        Schema? statsSchema, string? fieldIdsSpec = null)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        var uri = writableRoot + "/" + rel;
        var sql =
            $"COPY ({sourceSql}) TO '{uri.Replace("'", "''")}' " +
            "(FORMAT parquet, WRITE_BLOOM_FILTER true, RETURN_STATS"
            + (fieldIdsSpec is not null ? ", FIELD_IDS " + fieldIdsSpec : "") + ")";
        Log.LogInformation("delta native clustered copy {Uri}", uri);
        using var result = Host.Query(sql);
        return ReadFileStats(result, ct, statsSchema, writableRoot);
    }

    /// <summary>
    /// STREAMING partitioned write: streams <paramref name="src"/> into DuckDB's native <c>COPY … TO
    /// '&lt;root&gt;' (FORMAT parquet, PARTITION_BY (cols), APPEND true, FILENAME_PATTERN '{uuid}', RETURN_STATS)</c>
    /// — DuckDB produces the Hive <c>col=val/&lt;uuid&gt;.parquet</c> layout in one pull-based pass (bounded memory),
    /// excluding the partition columns from the files (Delta convention; values live in the returned
    /// <c>partition_keys</c>). Returns one <see cref="CopiedFile"/> per written file (relative path, row count,
    /// size, partition values, and — when <paramref name="statsSchema"/> is set — the Delta stats JSON).
    /// <c>APPEND</c> + a unique <c>{uuid}</c> filename add new files without disturbing existing partitions (the
    /// Delta log's add/remove decides overwrite-vs-append; old files are logically removed, physically VACUUMed).
    /// </summary>
    internal static List<CopiedFile> RunCopyPartitioned(
        string writableRoot, IReadOnlyList<string> partitionColumns, IArrowArrayStream src,
        CancellationToken ct, Schema? statsSchema, IReadOnlyDictionary<string, int>? fieldIds = null,
        IReadOnlyDictionary<string, string>? renameToPhysical = null, string? fieldIdsSpec = null,
        DeltaWriteSpec? spec = null)
    {
        // Under column mapping the input stream was renamed to physical names, so `partitionColumns` must
        // already be the PHYSICAL (output) names — dirs, RETURN_STATS.partition_keys, and FIELD_IDS key by them.
        var quoted = string.Join(", ", partitionColumns.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""));
        var sql =
            $"COPY (SELECT {SelectList(src.Schema, renameToPhysical)} FROM {InputName}) TO '{writableRoot.Replace("'", "''")}' " +
            $"(FORMAT parquet, PARTITION_BY ({quoted}), APPEND true, FILENAME_PATTERN '{{uuid}}', " +
            "WRITE_BLOOM_FILTER true, RETURN_STATS" + CopyTuning(spec)
            + (fieldIdsSpec is not null ? ", FIELD_IDS " + fieldIdsSpec : FieldIdsClause(fieldIds)) + ")";
        Log.LogInformation("delta native partitioned copy {Root} by [{Cols}]", writableRoot, quoted);
        var input = new (string, IArrowArrayStream)[] { (InputName, src) };
        using var result = Host.Query(sql, input);
        return ReadFileStats(result, ct, statsSchema, writableRoot);
    }

    // RETURN_STATS emits one row per written file: (filename, count, file_size_bytes, footer_size_bytes,
    // column_statistics, partition_keys). Build one CopiedFile per row — relative path from `filename`, partition
    // values from `partition_keys` (a MAP(colname → value)), and the Delta stats JSON from `column_statistics`
    // when a schema was supplied.
    private static List<CopiedFile> ReadFileStats(
        IArrowArrayStream result, CancellationToken ct, Schema? statsSchema, string writableRoot)
    {
        var files = new List<CopiedFile>();
        RecordBatch? b;
        while ((b = result.ReadNextRecordBatchAsync(ct).AsTask().GetAwaiter().GetResult()) is not null)
        {
            int fnIdx = b.Schema.GetFieldIndex("filename");
            int sizeIdx = b.Schema.GetFieldIndex("file_size_bytes");
            int countIdx = b.Schema.GetFieldIndex("count");
            int statsIdx = b.Schema.GetFieldIndex("column_statistics");
            int pkIdx = b.Schema.GetFieldIndex("partition_keys");
            for (int i = 0; i < b.Length; i++)
            {
                long fileRows = countIdx >= 0 ? ToLong(b.Column(countIdx), i) : 0;
                long fileSize = sizeIdx >= 0 ? ToLong(b.Column(sizeIdx), i) : 0;
                string rel = fnIdx >= 0 && b.Column(fnIdx) is StringArray fn && !fn.IsNull(i)
                    ? Relativize(fn.GetString(i), writableRoot) : "";
                var parts = pkIdx >= 0 ? ReadPartitionKeys(b.Column(pkIdx), i) : null;
                string? stats = statsSchema is not null && statsIdx >= 0
                    ? BuildDeltaStats(b.Column(statsIdx), i, fileRows, statsSchema) : null;
                files.Add(new CopiedFile(rel, fileRows, fileSize, parts, stats));
            }
        }
        return files;
    }

    // Strip the table-root prefix from a RETURN_STATS filename and normalize to forward slashes. DuckDB returns
    // the root part as given (forward slashes) but may use backslashes in the partition subdirs on Windows.
    private static string Relativize(string filename, string writableRoot)
    {
        var f = filename.Replace('\\', '/');
        var root = writableRoot.Replace('\\', '/').TrimEnd('/');
        int idx = f.IndexOf(root, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? f.Substring(idx + root.Length).TrimStart('/') : f;
    }

    // partition_keys is a MAP(VARCHAR colname → VARCHAR value) — one entry per partition column (unquoted names).
    private static Dictionary<string, string>? ReadPartitionKeys(IArrowArray col, int row)
    {
        if (col is not MapArray map) return null;
        var kv = (StructArray)map.KeyValues;
        var names = (StringArray)kv.Fields[0];
        var vals = (StringArray)kv.Fields[1];
        int start = map.ValueOffsets[row];
        int end = map.ValueOffsets[row + 1];
        if (end <= start) return null;
        var d = new Dictionary<string, string>();
        for (int e = start; e < end; e++)
        {
            if (names.IsNull(e)) continue;
            d[names.GetString(e)] = vals.IsNull(e) ? "" : vals.GetString(e);
        }
        return d.Count > 0 ? d : null;
    }

    private static long ToLong(IArrowArray col, int i) => col switch
    {
        UInt64Array u when u.GetValue(i) is { } v => checked((long)v),
        Int64Array s when s.GetValue(i) is { } v => v,
        UInt32Array u when u.GetValue(i) is { } v => v,
        Int32Array s when s.GetValue(i) is { } v => v,
        _ => 0,
    };

    // ------------------------------------------------------------------------------------------------------
    // Delta stats from DuckDB RETURN_STATS.column_statistics — a MAP(VARCHAR, STRUCT{min,max,null_count,…})
    // where the key is the quoted column name and min/max are DECODED TEXT (one struct type serves every
    // column). We type each value from the WRITE schema and emit exact-or-omit: min/max for integer / decimal /
    // string / boolean / date / naive-timestamp (exact text), OMITTED for float/double (decoded text may round →
    // a too-narrow min could wrongly skip a file) and for tz-timestamp / time / nested (format risk). nullCount +
    // numRecords are always emitted (exact integers). On any parse hiccup we fall back to numRecords-only stats
    // (never fail the write for statistics).
    // ------------------------------------------------------------------------------------------------------

    private enum StatKind { Number, String, Bool, Timestamp, Skip }

    private static StatKind KindFor(IArrowType t) => t switch
    {
        Int8Type or Int16Type or Int32Type or Int64Type
            or UInt8Type or UInt16Type or UInt32Type or UInt64Type
            or Decimal128Type or Decimal256Type => StatKind.Number,
        BooleanType => StatKind.Bool,
        StringType or LargeStringType => StatKind.String,
        Date32Type or Date64Type => StatKind.String,          // DuckDB text is already "yyyy-MM-dd"
        TimestampType tt when tt.Timezone is null => StatKind.Timestamp, // "y-M-d H:m:s[.f]" → ISO 'T'
        _ => StatKind.Skip,                                    // float/double, tz-timestamp, time, blob, nested
    };

    private static string NumRecordsOnly(long numRecords) => $"{{\"numRecords\":{numRecords}}}";

    private static string BuildDeltaStats(IArrowArray statsColumn, int row, long numRecords, Schema schema)
    {
        try
        {
            // DuckDB shape: column_statistics = MAP(VARCHAR colname -> MAP(VARCHAR statname -> VARCHAR value)).
            // Everything is stringified in the inner map (one MAP type serves every column + every stat).
            if (statsColumn is not MapArray outer)
                return NumRecordsOnly(numRecords);

            var outerKV = (StructArray)outer.KeyValues;   // {key: colname string, value: inner map}
            var colNames = (StringArray)outerKV.Fields[0];
            var innerMaps = (MapArray)outerKV.Fields[1];  // one inner map per outer entry (flattened)
            var innerKV = (StructArray)innerMaps.KeyValues;
            var statNames = (StringArray)innerKV.Fields[0];   // "min" / "max" / "null_count" / …
            var statVals = (StringArray)innerKV.Fields[1];    // the stringified stat value

            int start = outer.ValueOffsets[row];
            int end = outer.ValueOffsets[row + 1];

            // Collect per-column (kind, minText, maxText, nullCount), typed from the write schema.
            var cols = new List<(string Name, StatKind Kind, string? Min, string? Max, long? Null)>();
            for (int e = start; e < end; e++)
            {
                string col = Unquote(colNames.GetString(e));
                int fi = schema.GetFieldIndex(col);
                if (fi < 0) continue;
                var kind = KindFor(schema.GetFieldByIndex(fi).DataType);

                string? min = null, max = null;
                long? nulls = null;
                int iStart = innerMaps.ValueOffsets[e];
                int iEnd = innerMaps.ValueOffsets[e + 1];
                for (int s = iStart; s < iEnd; s++)
                {
                    string sn = statNames.GetString(s);
                    string? sv = statVals.IsNull(s) ? null : statVals.GetString(s);
                    if (sv is null) continue;
                    if (sn == "min") min = sv;
                    else if (sn == "max") max = sv;
                    else if (sn == "null_count" && long.TryParse(sv, out var n)) nulls = n;
                }
                cols.Add((col, kind, min, max, nulls));
            }

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("numRecords", numRecords);

                w.WritePropertyName("minValues");
                w.WriteStartObject();
                foreach (var c in cols)
                    if (c.Kind != StatKind.Skip && c.Min is not null) WriteStat(w, c.Name, c.Kind, c.Min);
                w.WriteEndObject();

                w.WritePropertyName("maxValues");
                w.WriteStartObject();
                foreach (var c in cols)
                    if (c.Kind != StatKind.Skip && c.Max is not null) WriteStat(w, c.Name, c.Kind, c.Max);
                w.WriteEndObject();

                w.WritePropertyName("nullCount");
                w.WriteStartObject();
                foreach (var c in cols)
                    if (c.Null is { } n) w.WriteNumber(c.Name, n);
                w.WriteEndObject();

                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "delta native stats: falling back to numRecords-only");
            return NumRecordsOnly(numRecords);
        }
    }

    private static void WriteStat(Utf8JsonWriter w, string name, StatKind kind, string text)
    {
        w.WritePropertyName(name);
        switch (kind)
        {
            case StatKind.Number:
                // Emit the exact decoded text as a JSON number (no double round-trip). Guard invalid tokens.
                try { w.WriteRawValue(text, skipInputValidation: false); }
                catch { w.WriteStringValue(text); }
                break;
            case StatKind.Bool:
                w.WriteBooleanValue(string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));
                break;
            case StatKind.Timestamp:
                // DuckDB renders a naive timestamp as "yyyy-MM-dd HH:mm:ss[.ffffff]" — ISO wants a 'T' separator.
                w.WriteStringValue(text.Length > 10 && text[10] == ' '
                    ? text.Substring(0, 10) + "T" + text.Substring(11) : text);
                break;
            default: // String, Date
                w.WriteStringValue(text);
                break;
        }
    }

    // DuckDB keys column_statistics by the QUOTED column name (e.g. "id"); strip one surrounding double-quote pair.
    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s.Substring(1, s.Length - 2) : s;

    // The COPY inner projection: `*` normally, or an explicit `"logical" AS "physical"` alias list for a
    // column-mapping table (per the Delta protocol, data files must use the PHYSICAL column names in BOTH name
    // and id mode — the input stream carries logical names, so the COPY renames on the way out).
    private static string SelectList(Schema schema, IReadOnlyDictionary<string, string>? renameToPhysical)
    {
        if (renameToPhysical is null || renameToPhysical.Count == 0)
        {
            return "*";
        }
        var parts = new List<string>(schema.FieldsList.Count);
        foreach (var f in schema.FieldsList)
        {
            parts.Add(renameToPhysical.TryGetValue(f.Name, out var phys) && phys != f.Name
                ? $"{QuoteIdent(f.Name)} AS {QuoteIdent(phys)}"
                : QuoteIdent(f.Name));
        }
        return string.Join(", ", parts);
    }

    private static string QuoteIdent(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // Renders the COPY `FIELD_IDS {'col': id, …}` option so DuckDB's parquet writer stamps each column's Delta
    // `columnMapping.id` into the parquet field_id — the identity an id-mode reader maps by. Keys are the OUTPUT
    // (physical) column names. Empty/null → no clause (a non-mapping table). String-literal keys tolerate
    // arbitrary column names. Top-level columns only; a nested mapped column would use the
    // `{'s': {__duckdb_field_id: 1, 'c': 2}}` recursion (a later slice).
    private static string FieldIdsClause(IReadOnlyDictionary<string, int>? fieldIds)
    {
        if (fieldIds is null || fieldIds.Count == 0)
        {
            return "";
        }
        var sb = new System.Text.StringBuilder(", FIELD_IDS {");
        bool first = true;
        foreach (var kv in fieldIds)
        {
            if (!first) { sb.Append(", "); }
            first = false;
            sb.Append('\'').Append(kv.Key.Replace("'", "''")).Append("': ").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
        }
        sb.Append('}');
        return sb.ToString();
    }

    // Lifts `PARQUET:field_id` Arrow field metadata (set by engineered-wood's SetParquetFieldIds on a
    // column-mapping rewrite/compaction) into a logical-name → field_id map for FieldIdsClause. Null when no field
    // carries the metadata (the common non-mapping case) → no FIELD_IDS clause.
    private static IReadOnlyDictionary<string, int>? FieldIdsFromMetadata(Schema schema)
    {
        Dictionary<string, int>? map = null;
        foreach (var f in schema.FieldsList)
        {
            if (f.Metadata is { } md && md.TryGetValue("PARQUET:field_id", out var s)
                && int.TryParse(s, out var id))
            {
                (map ??= new Dictionary<string, int>())[f.Name] = id;
            }
        }
        return map;
    }
}
