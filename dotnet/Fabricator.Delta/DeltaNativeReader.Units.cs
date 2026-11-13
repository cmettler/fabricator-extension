// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The native read as scan UNITS (ABI v102 <c>scan_next_unit</c>; <c>SET fabricator_scan_units</c>, ATTACH
/// <c>scan_units</c>, both default on). The planned files are cut into units in plan order — groups of files, or
/// row-group slices of one file — and each DuckDB scan thread takes the next unit and reads it through its OWN inner
/// host query (the plain form: a declared schema map, no footer probe), so the threads no longer share one stream
/// behind the pull lock. TPC-H SF10, file scan off: RustFS 33-40 s -> 17 s, OneLake 42.6 s -> 24.8 s.
/// </summary>
internal static partial class DeltaNativeReader
{
    // Smallest unit worth its own host query (a bind, a connection and a streaming result each).
    private static readonly long UnitMinBytes =
        long.TryParse(Environment.GetEnvironmentVariable("FABRICATOR_NATIVE_UNIT_MIN_BYTES"), out var b) && b > 0
            ? b : 32L << 20;

    private static readonly bool SlicesEnabled =
        Environment.GetEnvironmentVariable("FABRICATOR_NATIVE_UNIT_SLICES") != "0";

    /// <summary>The prepared scan as units, or null when it has fewer than two (the caller reads it whole).</summary>
    internal static IArrowArrayStream? ReadUnits(Prepared scan, int partitions, long? minBytes = null)
    {
        var groups = Group(scan.Listing.Files, partitions, minBytes is > 0 ? minBytes.Value : UnitMinBytes);
        if (groups.Count < 2)
        {
            return null;
        }
        var source = new NativeUnitSource(scan, groups, Math.Min(partitions, groups.Count));
        Log.LogInformation("delta native units {Path}: files={Files} units={Units} threads={Threads}",
                           scan.Path, scan.Listing.Files.Count, groups.Count, source.Count);
        return ScanUnits.Register(source.OutSchema, source);
    }

    // One unit: consecutive files, or a row-group slice [Lo, Hi) of one file.
    private sealed record UnitPlan(List<DeltaReader.NativeScanFile> Files, long Lo = -1, long Hi = -1)
    {
        internal string? SliceCond => Lo < 0
            ? null
            : FormattableString.Invariant($"file_row_number >= {Lo} AND file_row_number < {Hi}");
    }

    // Consecutive files, each group at least max(total / (2 * partitions), UnitMinBytes); a file of unknown size
    // is a group of its own. A file of at least twice that size is cut at its row-group boundaries (read from the
    // footer, one parquet_metadata query for all such files), so one big file no longer caps the unit count.
    private static List<UnitPlan> Group(IReadOnlyList<DeltaReader.NativeScanFile> files, int partitions, long minBytes)
    {
        long total = 0;
        foreach (var f in files)
        {
            total += f.SizeBytes ?? 0;
        }
        long target = Math.Max(total / Math.Max(1, 2 * partitions), minBytes);
        // Slice only when whole files give too few units: a unit caps how many threads run the plan above the scan,
        // while a whole file's query already reads its row groups in parallel, and a file_row_number range costs a
        // little per row (measured ~25% CPU on a bare scan). TPC-H SF10: lineitem (9 files) is faster whole, orders
        // (2) and customer (1) much faster sliced.
        bool slice = SlicesEnabled && files.Count < Math.Max(2, partitions / 4);
        var big = new List<DeltaReader.NativeScanFile>();
        foreach (var f in files)
        {
            if (slice && f.SizeBytes is { } sz && sz >= 2 * target && f.NumRecords is { } n && n > 0)
            {
                big.Add(f);
            }
        }
        var rowGroups = big.Count > 0 ? RowGroupRows(big) : new Dictionary<string, List<long>>();

        var groups = new List<UnitPlan>();
        List<DeltaReader.NativeScanFile>? current = null;
        long bytes = 0;
        foreach (var f in files)
        {
            if (f.SizeBytes is not { } size || size <= 0)
            {
                groups.Add(new UnitPlan(new List<DeltaReader.NativeScanFile> { f }));
                current = null;
                continue;
            }
            if (rowGroups.TryGetValue(f.Uri, out var rgs))
            {
                // Slices of whole row groups, each about `target` bytes (bytes estimated from the row share).
                long rows = f.NumRecords!.Value;
                long lo = 0;
                long pos = 0;
                long acc = 0;
                foreach (var r in rgs)
                {
                    pos += r;
                    acc += r;
                    if ((double)size * acc / rows >= target && pos < rows)
                    {
                        groups.Add(new UnitPlan(new List<DeltaReader.NativeScanFile> { f }, lo, pos));
                        lo = pos;
                        acc = 0;
                    }
                }
                groups.Add(new UnitPlan(new List<DeltaReader.NativeScanFile> { f }, lo, long.MaxValue));
                current = null;
                continue;
            }
            if (current is null || bytes >= target)
            {
                current = new List<DeltaReader.NativeScanFile>();
                groups.Add(new UnitPlan(current));
                bytes = 0;
            }
            current.Add(f);
            bytes += size;
        }
        return groups;
    }

    // Row counts per row group, in file order, for files whose footer agrees with the log's numRecords; a file
    // missing from the answer (or the whole query failing) is simply not split.
    private static Dictionary<string, List<long>> RowGroupRows(List<DeltaReader.NativeScanFile> files)
    {
        var result = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        try
        {
            var list = string.Join(", ", files.Select(f => "'" + f.Uri.Replace("'", "''") + "'"));
            var sql = "SELECT file_name, row_group_id, any_value(row_group_num_rows) FROM parquet_metadata(["
                      + list + "]) GROUP BY ALL ORDER BY 1, 2";
            using var s = Host.Query(sql, HostSession.Neutral);
            while (s.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                using (batch)
                {
                    var names = (StringArray)batch.Column(0);
                    var counts = (Int64Array)batch.Column(2);
                    for (int i = 0; i < batch.Length; i++)
                    {
                        string name = names.GetString(i);
                        if (!result.TryGetValue(name, out var l))
                        {
                            result[name] = l = new List<long>();
                        }
                        l.Add(counts.GetValue(i) ?? 0);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "delta native units: row-group probe failed, files stay whole");
            return new Dictionary<string, List<long>>(StringComparer.Ordinal);
        }
        foreach (var f in files)
        {
            if (result.TryGetValue(f.Uri, out var l) && (l.Count < 2 || l.Sum() != f.NumRecords))
            {
                result.Remove(f.Uri);
            }
        }
        return result;
    }

    private sealed class NativeUnitSource : IScanUnitSource
    {
        private readonly Prepared _scan;
        private readonly List<UnitPlan> _groups;
        private readonly Schema _partSchema;
        private IArrowArrayStream? _first; // unit 0, opened up front for the schema
        private int _next;

        internal NativeUnitSource(Prepared scan, List<UnitPlan> groups, int count)
        {
            _scan = scan;
            _groups = groups;
            Count = count;
            _first = OpenUnit(groups[0]);
            OutSchema = _first.Schema;
            _partSchema = new Schema(new List<Field>(OutSchema.FieldsList)
            {
                new Field(ScanUnits.UnitColumn, Int64Type.Default, nullable: false),
            }, OutSchema.Metadata);
        }

        internal Schema OutSchema { get; }

        public int Count { get; }

        public IArrowArrayStream? NextUnit()
        {
            int unit = Interlocked.Increment(ref _next) - 1;
            if (unit >= _groups.Count)
            {
                return null;
            }
            var inner = unit == 0
                ? Interlocked.Exchange(ref _first, null) ?? throw new InvalidOperationException("unit 0 taken twice")
                : OpenUnit(_groups[unit]);
            return new UnitStream(inner, unit, _partSchema);
        }

        public void Close() => Interlocked.Exchange(ref _first, null)?.Dispose();

        private IArrowArrayStream OpenUnit(UnitPlan u) =>
            DeltaNativeReader.Open(_scan, WithFiles(_scan.Listing, u.Files), u.SliceCond, unit: true);
    }

    private sealed class UnitStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private readonly long _unit;
        private int _disposed;

        internal UnitStream(IArrowArrayStream inner, long unit, Schema schema)
        {
            _inner = inner;
            _unit = unit;
            Schema = schema;
        }

        public Schema Schema { get; }

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                return null;
            }
            int n = batch.Length;
            var units = new long[n];
            System.Array.Fill(units, _unit);
            var unitArray = new Int64Array(new ArrowBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                                               units.AsSpan()).ToArray()), ArrowBuffer.Empty, n, 0, 0);
            var arrays = new List<IArrowArray>(batch.ColumnCount + 1);
            for (int c = 0; c < batch.ColumnCount; c++)
            {
                arrays.Add(batch.Column(c));
            }
            arrays.Add(unitArray);
            return new RecordBatch(Schema, arrays, n);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _inner.Dispose();
            }
        }
    }
}
