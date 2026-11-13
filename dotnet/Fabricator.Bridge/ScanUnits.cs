// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>A scan read as UNITS (ABI v102 <c>scan_next_unit</c>): the host's threads each pull the next unit.</summary>
internal interface IScanUnitSource
{
    /// <summary>How many host threads may pull units at once (at most the scan's thread count).</summary>
    int Count { get; }

    /// <summary>The next unit's stream, or null when the scan has none left. Called concurrently by the host's
    /// threads. A unit's batches carry the scan's columns plus a trailing <see cref="ScanUnits.UnitColumn"/>
    /// (Int64, the unit's number in plan order — the host builds its batch indexes from it).</summary>
    IArrowArrayStream? NextUnit();

    /// <summary>Called once, after the scan's own stream and every unit stream were released.</summary>
    void Close();
}

/// <summary>
/// The unit scans in flight, by id. A provider answers a scan whose spec carries <c>partitions</c> (the host's thread
/// count, under <c>SET fabricator_scan_units</c>) with an EMPTY stream whose schema carries <see cref="ScanIdKey"/>
/// and <see cref="CountKey"/>; the host's threads then call <c>scan_next_unit(id)</c> until it answers none, each
/// reading its unit without the scan's pull lock. The source closes when the scan's stream and every unit stream are
/// released.
/// </summary>
internal static class ScanUnits
{
    internal const string ScanIdKey = "fabricator.unit_scan";
    internal const string CountKey = "fabricator.unit_threads";
    internal const string UnitColumn = "__fabricator_unit";

    private static readonly ConcurrentDictionary<long, Entry> _scans = new();
    private static long _nextId;

    private sealed class Entry
    {
        internal required IScanUnitSource Source;
        internal int Refs = 1; // the scan's own stream
    }

    /// <summary>The scan's own stream: <paramref name="schema"/> with the two keys added, and no batches.</summary>
    internal static IArrowArrayStream Register(Schema schema, IScanUnitSource source)
    {
        long id = Interlocked.Increment(ref _nextId);
        _scans[id] = new Entry { Source = source };
        var metadata = new Dictionary<string, string>(schema.Metadata ?? new Dictionary<string, string>())
        {
            [ScanIdKey] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [CountKey] = source.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return new EmptyStream(new Schema(schema.FieldsList, metadata), () => Release(id));
    }

    /// <summary>The next unit of scan <paramref name="id"/>, or null when it has none left.</summary>
    internal static IArrowArrayStream? NextUnit(long id)
    {
        if (!_scans.TryGetValue(id, out var entry))
        {
            throw new InvalidOperationException($"scan_next_unit: no unit scan {id}");
        }
        lock (entry)
        {
            entry.Refs++;
        }
        try
        {
            var unit = entry.Source.NextUnit();
            if (unit is null)
            {
                Release(id);
                return null;
            }
            return new ReleasingStream(unit, () => Release(id));
        }
        catch
        {
            Release(id);
            throw;
        }
    }

    private static void Release(long id)
    {
        if (!_scans.TryGetValue(id, out var entry))
        {
            return;
        }
        bool last;
        lock (entry)
        {
            last = --entry.Refs == 0;
        }
        if (last && _scans.TryRemove(id, out _))
        {
            entry.Source.Close();
        }
    }

    private sealed class EmptyStream : IArrowArrayStream
    {
        private Action? _release;

        internal EmptyStream(Schema schema, Action release)
        {
            Schema = schema;
            _release = release;
        }

        public Schema Schema { get; }

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
            new((RecordBatch?)null);

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class ReleasingStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private Action? _release;

        internal ReleasingStream(IArrowArrayStream inner, Action release)
        {
            _inner = inner;
            _release = release;
        }

        public Schema Schema => _inner.Schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadNextRecordBatchAsync(cancellationToken);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _release, null) is { } release)
            {
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    release();
                }
            }
        }
    }
}
