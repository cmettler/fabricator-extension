using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
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
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        var uri = _writableRoot + "/" + rel;
        // DuckDB's single-file COPY does NOT create the target's parent directory, so a partitioned file
        // (region=US/<uuid>.parquet) or a _change_data file would fail. Create it first (recursive, idempotent).
        // Best-effort: on an object store (OneLake/S3) directories are implicit — CreateDirectory may be a no-op
        // or unimplemented, and the blob write creates the path anyway, so a failure here is not fatal.
        int slash = rel.LastIndexOf('/');
        if (slash > 0)
        {
            try { HostFs.CreateDir(AmbientOpener.Current, _writableRoot + "/" + rel.Substring(0, slash)); }
            catch { /* object-store implicit dirs / unimplemented CreateDirectory — the COPY still writes */ }
        }
        var sql =
            $"COPY (SELECT * FROM {InputName}) TO '{uri.Replace("'", "''")}' " +
            "(FORMAT parquet, WRITE_BLOOM_FILTER true, RETURN_STATS)";
        long rows = 0;
        foreach (var b in batches)
        {
            rows += b.Length;
        }
        Log.LogInformation("delta native write {Uri} rows={Rows} batches={Batches}", uri, rows, batches.Count);

        // Bind the batches as a fresh Arrow stream (the host dequeues + exports each; InMemoryArrayStream only
        // disposes UNdequeued batches, and the C export doesn't free managed buffers — so the caller's batches
        // stay valid for its subsequent stats collection). One parquet file is written from the whole stream.
        var input = new (string, IArrowArrayStream)[]
        {
            (InputName, new InMemoryArrayStream(batches[0].Schema, batches)),
        };
        using var result = Host.Query(sql, input);
        long size = ReadFileSize(result, cancellationToken);
        return new ValueTask<long>(size);
    }

    // RETURN_STATS emits one row per written file: (filename, count, file_size_bytes, footer_size_bytes,
    // column_statistics, partition_keys). A single-file COPY writes exactly one row → sum defensively anyway.
    private static long ReadFileSize(IArrowArrayStream result, CancellationToken ct)
    {
        long total = 0;
        RecordBatch? b;
        while ((b = result.ReadNextRecordBatchAsync(ct).AsTask().GetAwaiter().GetResult()) is not null)
        {
            var idx = b.Schema.GetFieldIndex("file_size_bytes");
            if (idx < 0)
            {
                continue;
            }
            var col = b.Column(idx);
            for (int i = 0; i < b.Length; i++)
            {
                total += ToLong(col, i);
            }
        }
        return total;
    }

    private static long ToLong(IArrowArray col, int i) => col switch
    {
        UInt64Array u when u.GetValue(i) is { } v => checked((long)v),
        Int64Array s when s.GetValue(i) is { } v => v,
        UInt32Array u when u.GetValue(i) is { } v => v,
        Int32Array s when s.GetValue(i) is { } v => v,
        _ => 0,
    };
}
