using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Apache.Arrow;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace ArrowNet.Bridge;

/// <summary>
/// The native Delta data-file <b>reader</b> (<see cref="IDataFileReader"/>): DuckDB's own
/// <c>read_parquet</c> decodes each data file the Delta layer asks for — the read-side counterpart of
/// <see cref="NativeParquetDataFileWriter"/>, completing the codec seam so copy-on-write rewrites,
/// merge-on-read UPDATE reads and compaction all decode through the host engine. This is what preserves
/// codec-specific representations the engineered-wood reader is blind to (the parquet VARIANT annotation
/// round-trips as the registered <c>arrownet.variant</c> transport), and it inherits the native decode
/// quality (shredded variants, all encodings) for free.
///
/// <para>The contract requires batches in FILE ORDER (every consumer is position-keyed), so the query
/// orders by <c>file_row_number</c> explicitly rather than relying on DuckDB's preserve_insertion_order
/// setting (a user can turn that off globally). The position column itself is projected away.</para>
/// </summary>
internal sealed class NativeParquetDataFileReader : IDataFileReader
{
    private static readonly ILogger Log = ArrowNetLog.CreateLogger("ArrowNet.Delta.Native");

    private readonly string _root; // table root as a DuckDB-openable URI (onelake:// for OneLake)

    internal NativeParquetDataFileReader(string tablePath)
    {
        _root = DeltaReader.ToReadableRoot(tablePath);
    }

    internal static bool Available => Host.CanQuery;

    public async IAsyncEnumerable<RecordBatch> ReadAsync(
        string relativePath, IReadOnlyList<string>? physicalColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        var uri = _root + "/" + rel;

        // Explicit projection: either the requested physical columns, or the file's own column set (probed
        // footer-only) — needed to exclude the file_row_number ordering column from the output either way.
        IReadOnlyList<string> cols = physicalColumns is { Count: > 0 }
            ? physicalColumns
            : ProbeColumns(uri);
        string select = string.Join(", ", cols.Select(Quote));

        var sql = $"SELECT {select} FROM read_parquet('{uri.Replace("'", "''")}', file_row_number => true) "
                  + "ORDER BY file_row_number";
        Log.LogDebug("delta native file read: {Sql}", sql);

        using var stream = Host.Query(sql);
        while (true)
        {
            var rb = await stream.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (rb is null)
            {
                break;
            }
            yield return rb;
        }
    }

    private static IReadOnlyList<string> ProbeColumns(string uri)
    {
        using var s = Host.Query($"SELECT * FROM read_parquet('{uri.Replace("'", "''")}') LIMIT 0");
        return s.Schema.FieldsList.Select(f => f.Name).ToList();
    }

    private static string Quote(string col) => "\"" + col.Replace("\"", "\"\"") + "\"";
}
