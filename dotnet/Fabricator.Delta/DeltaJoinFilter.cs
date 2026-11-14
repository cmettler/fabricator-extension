// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Apache.Arrow;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// A hash join's runtime filters on the CODEC engine's read, as engineered-wood row masks
/// (<see cref="DeltaReadOptions.RowMasks"/>, EW #502).
/// </summary>
/// <remarks>
/// <para>
/// DuckDB hands a scan under a join its build side as a prefix-range filter, or a bloom filter for keys that one does
/// not cover. Neither has a predicate form. The host keeps the filter object and names it in the scan's filter tree as
/// a <c>lookup</c> node (<see cref="FilterNode.Id"/>); each becomes a <see cref="DeltaRowMask"/> over its column whose
/// function hands the key column to the host (<see cref="HostFs.JoinFilterLookup"/>, Arrow C data, nothing
/// serialized) and gets DuckDB's own answer back. engineered-wood runs the mask INSIDE its row-group reads, so the
/// other columns of the rows it rejects are never decoded.
/// </para>
/// <para>
/// ⚠ Deliberately NOT part of the read predicate (<c>DeltaReadOptions.Filter</c>): that predicate is also the
/// transaction's READ SET for conflict checks, which cannot evaluate a host function. The key RANGE beside the lookup
/// is an ordinary comparison and does go there (file, row-group and page pruning).
/// </para>
/// <para>
/// A mask may only ever widen: DuckDB's join checks its keys itself, and the host re-applies every filter to each
/// chunk (superset_filter_pushdown). So a bloom filter's false positives, a dictionary answer for a value no live row
/// holds (<see cref="DeltaRowMask.ValueWise"/>), or a filter already removed (every row passes) are all safe.
/// </para>
/// <para>
/// The client context rides the closure (not the ambient opener): engineered-wood calls masks on its own reader
/// threads, concurrently. It is the scan's ClientContext, valid until the scan releases its stream, after which no
/// mask runs and the host removes the filter ids.
/// </para>
/// </remarks>
internal static class DeltaJoinFilter
{
    private static readonly ILogger _log = FabricatorLog.CreateLogger("Fabricator.Delta");

    /// <summary>Off with <c>FABRICATOR_JOIN_FILTER_MASKS=0</c> (the catalog then declares no lookup capability).</summary>
    internal static bool Enabled { get; } =
        !string.Equals(Environment.GetEnvironmentVariable("FABRICATOR_JOIN_FILTER_MASKS"), "0", StringComparison.Ordinal);

    /// <summary>
    /// The AND-reachable <c>lookup</c> nodes of <paramref name="filter"/> as row masks, or null when there are none.
    /// Only top-level columns: a lookup on a struct member is left to the host's re-apply.
    /// </summary>
    internal static IReadOnlyList<DeltaRowMask>? Masks(FilterNode? filter, nint clientContext)
    {
        if (filter is null || clientContext == 0 || !HostFs.CanJoinFilterLookup)
        {
            return null;
        }
        var lookups = new List<(ulong Id, string Column)>();
        Walk(filter, lookups);
        if (lookups.Count == 0)
        {
            return null;
        }
        var masks = new List<DeltaRowMask>(lookups.Count);
        foreach (var (id, column) in lookups)
        {
            masks.Add(new DeltaRowMask(new[] { column }, batch => Mask(clientContext, id, batch)) { ValueWise = true });
        }
        _log.LogDebug("delta join filter masks: {Masks}",
                      string.Join(", ", lookups.ConvertAll(l => $"#{l.Id} on {l.Column}")));
        return masks;
    }

    private static void Walk(FilterNode node, List<(ulong, string)> found)
    {
        if (string.Equals(node.Op, "and", StringComparison.Ordinal))
        {
            foreach (var child in node.Children ?? new List<FilterNode>())
            {
                Walk(child, found);
            }
            return;
        }
        // OR branches are left alone: a lookup under an OR filters nothing on its own
        if (string.Equals(node.Op, "lookup", StringComparison.Ordinal) && node.Id is ulong id)
        {
            string? column = node.Path is { Count: > 0 } p ? (p.Count == 1 ? p[0] : null) : node.Col;
            if (column is not null)
            {
                found.Add((id, column));
            }
        }
    }

    /// <summary>
    /// Passes the stream through and logs, at its end, how many rows the read returned with the masks in place: the
    /// observable that the masks filtered INSIDE the read (the host's re-apply leaves the scan's output the same
    /// either way).
    /// </summary>
    internal static async IAsyncEnumerable<RecordBatch> CountReturned(IAsyncEnumerable<RecordBatch> source, string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
    {
        long rows = 0;
        await foreach (var batch in source.WithCancellation(ct).ConfigureAwait(false))
        {
            rows += batch.Length;
            yield return batch;
        }
        _log.LogDebug("delta join filter masks: {Path} returned {Rows} rows", path, rows);
    }

    // One column in, a null-free mask at offset 0 out. A key the host cannot answer for passes.
    private static BooleanArray Mask(nint clientContext, ulong id, RecordBatch batch)
    {
        int n = batch.Length;
        var bits = new byte[(n + 7) / 8];
        byte[]? match = n == 0 ? null : HostFs.JoinFilterLookup(clientContext, id, batch.Column(0));
        for (int r = 0; r < n; r++)
        {
            if (match is null || r >= match.Length || match[r] != 0)
            {
                bits[r >> 3] |= (byte)(1 << (r & 7));
            }
        }
        return new BooleanArray(new ArrowBuffer(bits), ArrowBuffer.Empty, n, 0, 0);
    }
}
