// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Apache.Arrow;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The native Delta data-file <b>reader</b> (<see cref="IDataFileReader"/>): DuckDB's own
/// <c>read_parquet</c> decodes each data file the Delta layer asks for — the read-side counterpart of
/// <see cref="NativeParquetDataFileWriter"/>, completing the codec seam so copy-on-write rewrites,
/// merge-on-read UPDATE reads and compaction all decode through the host engine. This is what preserves
/// codec-specific representations the engineered-wood reader is blind to (the parquet VARIANT annotation
/// round-trips as the registered <c>ew.variant_transport</c> transport), and it inherits the native decode
/// quality (shredded variants, all encodings) for free.
///
/// <para>The contract requires batches in FILE ORDER (every consumer is position-keyed), so the query
/// orders by <c>file_row_number</c> explicitly rather than relying on DuckDB's preserve_insertion_order
/// setting (a user can turn that off globally). The position column itself is projected away.</para>
/// </summary>
internal sealed class NativeParquetDataFileReader : IDataFileReader
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Delta.Native");

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

        // Explicit projection intersected with the file's ACTUAL columns (probed footer-only): a requested
        // column may be absent from this file (a partial-column INSERT / a later-ADDed column) and
        // read_parquet errors on unknown names — the Delta layer backfills the absent ones as typed NULLs.
        // An empty intersection falls back to all columns (the row count must still come from the file).
        // The explicit list also excludes the file_row_number ordering column from the output.
        IReadOnlyList<string> present = ProbeColumns(uri);
        IReadOnlyList<string> cols = present;
        if (physicalColumns is { Count: > 0 })
        {
            var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
            var kept = physicalColumns.Where(presentSet.Contains).ToList();
            if (kept.Count > 0)
            {
                cols = kept;
            }
        }
        string select = string.Join(", ", cols.Select(Quote));

        var sql = $"SELECT {select} FROM read_parquet('{uri.Replace("'", "''")}', file_row_number => true) "
                  + "ORDER BY file_row_number";
        Log.LogDebug("delta native file read: {Sql}", sql);

        using var stream = Host.Query(sql);
        // Batch sizes at this seam are worth tracing: the RecordBatch coming out of the host query is BOTH
        // the morsel a parallel scan hands one thread AND — because engineered-wood writes one parquet file
        // per input batch — the file granularity of anything that writes what it reads (compaction). One
        // DataChunk per batch means STANDARD_VECTOR_SIZE rows; see the note on HostQueryGetNext.
        long batchCount = 0, totalRows = 0, minRows = long.MaxValue, maxRows = 0;
        while (true)
        {
            var rb = await stream.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (rb is null)
            {
                Log.LogDebug(
                    "delta native file read: {Rel} yielded {Batches} batch(es), {Rows} rows (min={Min} max={Max}) "
                    + "— STREAMED, never materialized (this method is an async iterator)",
                    rel, batchCount, totalRows, batchCount == 0 ? 0 : minRows, maxRows);
                break;
            }
            batchCount++;
            totalRows += rb.Length;
            minRows = Math.Min(minRows, rb.Length);
            maxRows = Math.Max(maxRows, rb.Length);
            Log.LogTrace("delta native file read: batch {N} rows={Rows}", batchCount, rb.Length);
            // DuckDB hands a VARIANT column across as the ew.variant_transport LEAF BLOB (the type extension
            // in fabricator_variant.cpp), but engineered-wood's read pipeline expects the physical layout the
            // Delta spec requires: VariantColumnCoercion THROWS on a BinaryArray whose schema says variant.
            // Converting here is what lets that coercion stay UNPATCHED — the seam is ours, so the adaptation
            // belongs on this side of it.
            yield return VariantTransport.ToCanonical(rb);
        }
    }

    private static IReadOnlyList<string> ProbeColumns(string uri)
    {
        using var s = Host.Query($"SELECT * FROM read_parquet('{uri.Replace("'", "''")}') LIMIT 0");
        return s.Schema.FieldsList.Select(f => f.Name).ToList();
    }

    private static string Quote(string col) => "\"" + col.Replace("\"", "\"\"") + "\"";
}
