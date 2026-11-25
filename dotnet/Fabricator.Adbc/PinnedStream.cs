// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Adbc;

/// <summary>
/// A scan's result on a transaction's pinned connection: it streams from the driver, and when something else needs the
/// connection (<see cref="AdbcTransaction.Use"/>) it reads the rest of its result into memory and serves that. So
/// statements one after the other in a transaction buffer nothing; only an overlap does, and only the part not read yet.
/// </summary>
/// <remarks>
/// Locks: the connection (the transaction's gate), then <see cref="_sync"/> — never the other way round. A read of the
/// LIVE result holds both for one batch; a read of the buffer holds only <see cref="_sync"/>, so it never waits for
/// whoever holds the connection (a load holding it may be waiting for exactly the rows this scan feeds).
/// </remarks>
internal sealed class PinnedStream : IArrowArrayStream
{
    private readonly AdbcTransaction _txn;
    private readonly object _sync = new();
    private readonly Action<string>? _log;
    private IArrowArrayStream? _live;
    private Queue<RecordBatch>? _buffered;
    private volatile bool _off; // the live result is gone: buffered, finished, abandoned or disposed
    private string? _abandoned;

    /// <param name="live">The driver's result; owned (its statement too) from here on.</param>
    public PinnedStream(AdbcTransaction txn, IArrowArrayStream live, Action<string>? log)
    {
        _txn = txn;
        _live = live;
        _log = log;
        Schema = live.Schema;
    }

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_off)
            {
                lock (_sync)
                    return new ValueTask<RecordBatch?>(FromBuffer());
            }
            // the connection, but without making ourselves step aside; wait in slices, since a load holding it may
            // be waiting for our rows — it makes us buffer first (Use), and then the buffer needs no connection
            if (!_txn.TryEnter(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            try
            {
                lock (_sync)
                {
                    if (_off)
                        return new ValueTask<RecordBatch?>(FromBuffer());
                    var batch = _live!.ReadNextRecordBatchAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
                    if (batch is null)
                    {
                        CloseLive();
                        _buffered = new Queue<RecordBatch>();
                        _txn.Release(this);
                    }
                    return new ValueTask<RecordBatch?>(batch);
                }
            }
            finally
            {
                _txn.Exit();
            }
        }
    }

    /// <summary>Under the connection: read the rest of the live result into memory and let go of the driver.</summary>
    internal void Buffer()
    {
        lock (_sync)
        {
            if (_off)
                return;
            var rest = new Queue<RecordBatch>();
            long rows = 0;
            try
            {
                while (_live!.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
                {
                    rest.Enqueue(batch);
                    rows += batch.Length;
                }
            }
            catch (Exception ex)
            {
                foreach (var b in rest)
                    b.Dispose();
                rest.Clear();
                _abandoned = "adbc: reading the rest of a scan, so its connection could run the next statement, failed: "
                             + ex.Message;
            }
            _buffered = rest;
            CloseLive();
            _log?.Invoke($"pinned scan buffered: {rest.Count} batches, {rows} rows not read yet when the connection was needed");
        }
    }

    /// <summary>Under the connection: the transaction ended with the scan still open.</summary>
    internal void Abandon()
    {
        lock (_sync)
        {
            if (_off)
                return;
            _abandoned = "adbc: the transaction ended while this scan was still open";
            _buffered = new Queue<RecordBatch>();
            CloseLive();
        }
    }

    private RecordBatch? FromBuffer()
    {
        if (_abandoned is not null)
            throw new InvalidOperationException(_abandoned);
        return _buffered is { Count: > 0 } ? _buffered.Dequeue() : null;
    }

    private void CloseLive()
    {
        _off = true;
        var live = _live;
        _live = null;
        live?.Dispose();
    }

    public void Dispose()
    {
        while (!_off)
        {
            if (!_txn.TryEnter(20))
                continue;
            try
            {
                lock (_sync)
                {
                    if (!_off)
                    {
                        CloseLive();
                        _buffered = new Queue<RecordBatch>();
                        _txn.Release(this);
                    }
                }
            }
            finally
            {
                _txn.Exit();
            }
        }
        lock (_sync)
        {
            while (_buffered is { Count: > 0 })
                _buffered.Dequeue().Dispose();
        }
    }
}
