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

namespace ArrowNet.Bridge;

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
    private const string InputName = "__arrownet_delta_write_src";
    private static readonly ILogger Log = ArrowNetLog.CreateLogger("ArrowNet.Delta.Native");

    // The table root as a URI DuckDB's writer can open (onelake:// for OneLake, else the local/s3 path). Files
    // are written to <root>/<relativePath> so they resolve identically to engineered-wood's own _fs mapping.
    private readonly string _writableRoot;

    internal NativeParquetDataFileWriter(string tablePath)
    {
        _writableRoot = DeltaReader.ToReadableRoot(tablePath);
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
        var src = new InMemoryArrayStream(batches[0].Schema, batches);
        var (_, size, _) = RunCopy(_writableRoot, relativePath, src, cancellationToken);
        return new ValueTask<long>(size);
    }

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
    internal static (long Rows, long Size, string? Stats) RunCopy(
        string writableRoot, string relativePath, IArrowArrayStream src, CancellationToken ct,
        Schema? statsSchema = null)
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
            $"COPY (SELECT * FROM {InputName}) TO '{uri.Replace("'", "''")}' " +
            "(FORMAT parquet, WRITE_BLOOM_FILTER true, RETURN_STATS)";
        Log.LogInformation("delta native copy {Uri}", uri);
        var input = new (string, IArrowArrayStream)[] { (InputName, src) };
        using var result = Host.Query(sql, input);
        return ReadStats(result, ct, statsSchema);
    }

    // RETURN_STATS emits one row per written file: (filename, count, file_size_bytes, footer_size_bytes,
    // column_statistics, partition_keys). A single-file COPY writes exactly one row → sum count/size defensively;
    // the Delta stats JSON (when requested) is built from the FIRST file's column_statistics (single-file path).
    private static (long Rows, long Size, string? Stats) ReadStats(
        IArrowArrayStream result, CancellationToken ct, Schema? statsSchema)
    {
        long rows = 0, size = 0;
        string? stats = null;
        RecordBatch? b;
        while ((b = result.ReadNextRecordBatchAsync(ct).AsTask().GetAwaiter().GetResult()) is not null)
        {
            int sizeIdx = b.Schema.GetFieldIndex("file_size_bytes");
            int countIdx = b.Schema.GetFieldIndex("count");
            int statsIdx = b.Schema.GetFieldIndex("column_statistics");
            for (int i = 0; i < b.Length; i++)
            {
                long fileRows = countIdx >= 0 ? ToLong(b.Column(countIdx), i) : 0;
                if (sizeIdx >= 0) size += ToLong(b.Column(sizeIdx), i);
                rows += fileRows;
                // Build the Delta stats for the single written file (first row) when a schema was supplied.
                if (statsSchema is not null && stats is null && statsIdx >= 0)
                {
                    stats = BuildDeltaStats(b.Column(statsIdx), i, fileRows, statsSchema);
                }
            }
        }
        return (rows, size, stats);
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
}
