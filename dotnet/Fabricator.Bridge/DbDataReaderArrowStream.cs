// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge.Conversion;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// Streams a <see cref="DbDataReader"/> result set to the C++ host as Arrow
/// record batches, one batch per <see cref="ReadNextRecordBatchAsync"/> call.
/// Backend-agnostic (any ADO.NET provider); owns and disposes the reader,
/// command, and connection. Rows are fetched with <c>ReadAsync</c> so a query
/// interrupt (Ctrl+C / timeout) can cancel a blocking network fetch via the
/// optional <see cref="InterruptScope"/>; the Arrow C-stream exporter still
/// blocks once on the returned task (safe — the hostfxr CLR has no
/// SynchronizationContext, so sync-over-async can't deadlock). See
/// docs/cancellation.md. When no scope is supplied the token is <c>default</c>
/// and behavior is identical to a plain synchronous read.
/// </summary>
public sealed class DbDataReaderArrowStream : IArrowArrayStream
{
    private readonly DbConnection _connection;
    private readonly DbCommand _command;
    private readonly DbDataReader _reader;
    private readonly IArrowType[] _columnTypes;
    private readonly int _batchSize;
    private readonly bool _ownsConnection;
    private readonly InterruptScope? _interrupt;
    private readonly CancellationToken _token;
    private bool _done;
    private bool _released;

    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql");

    public DbDataReaderArrowStream(DbConnection connection, DbCommand command, DbDataReader reader,
                                   int batchSize = 2048, bool ownsConnection = true,
                                   InterruptScope? interrupt = null)
    {
        _connection = connection;
        _command = command;
        _reader = reader;
        _batchSize = batchSize;
        _ownsConnection = ownsConnection;
        _interrupt = interrupt;
        _token = interrupt?.Token ?? default;

        var columns = reader.GetColumnSchema();
        var fields = new Field[columns.Count];
        _columnTypes = new IArrowType[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            fields[i] = SqlArrowMapping.ToArrowField(columns[i]);
            _columnTypes[i] = fields[i].DataType;
        }
        Schema = new Schema(fields, metadata: null);
    }

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        => ReadNextBatchAsync();

    private async ValueTask<RecordBatch?> ReadNextBatchAsync()
    {
        if (_done)
        {
            return null;
        }

        var appenders = new ColumnAppender[_columnTypes.Length];
        for (int i = 0; i < appenders.Length; i++)
        {
            appenders[i] = ColumnAppender.Create(_columnTypes[i]);
        }

        int rows = 0;
        bool exhausted = false;
        // ReadAsync honors the interrupt token — a blocking network packet fetch cancels on Ctrl+C/timeout.
        // For a provider without true async (e.g. ADOMD) DbDataReader.ReadAsync falls back to a synchronous
        // read, so this is safe everywhere; for buffered rows it completes synchronously (no overhead spike).
        while (rows < _batchSize)
        {
            if (!await _reader.ReadAsync(_token).ConfigureAwait(false))
            {
                exhausted = true; // the RESULT SET ended — distinguishable from "this batch is full"
                break;
            }
            for (int i = 0; i < appenders.Length; i++)
            {
                var value = _reader[i];
                if (value is null or DBNull)
                {
                    appenders[i].AppendNull();
                }
                else
                {
                    appenders[i].Append(value);
                }
            }
            rows++;
        }

        if (rows == 0)
        {
            _done = true;
            Release("eof");
            return null;
        }

        var arrays = new IArrowArray[appenders.Length];
        for (int i = 0; i < appenders.Length; i++)
        {
            arrays[i] = appenders[i].Build();
        }
        var batch = new RecordBatch(Schema, arrays, rows);
        if (exhausted)
        {
            // ⚠ RELEASING HERE IS ONLY SOUND BECAUSE THE BATCH OWNS ITS OWN MEMORY, and that is the
            // invariant to protect if ColumnAppender ever gains a zero-copy path. Two copies stand between
            // this batch and reader-owned memory: DbDataReader's indexer builds a FRESH object per row (it
            // is GetValue, never a reusable caller buffer — we use neither GetBytes/GetChars nor
            // SequentialAccess), and every appender copies into Arrow builder buffers (binary goes through
            // Append(ReadOnlySpan<byte>)). `Schema` is likewise captured in the constructor, so it outlives
            // the reader too. An appender that ALIASED the incoming object would make this a use-after-free.
            _done = true;
            Release("eof");
        }
        return batch;
    }

    /// <summary>
    /// Releases the reader/command/connection. Called at END OF RESULT SET as well as from
    /// <see cref="Dispose"/>, so a drained scan stops holding a SQL Server result set open for the rest of
    /// the query.
    /// </summary>
    /// <remarks>
    /// <para><b>Why eagerly:</b> the consumer releases this stream from the scan's global-state DESTRUCTOR,
    /// i.e. at query teardown rather than at pipeline end. A fully-drained reader therefore stayed OPEN for
    /// the rest of the statement, and SQL Server counts that as an outstanding result set — which is what
    /// forces MARS (or materialisation) for a statement that scans one catalog table twice, since the
    /// hash-join build side is drained long before the probe side opens its reader. See
    /// docs/transactions.md §5.1/§5.2.</para>
    /// <para><b>⚠ Idempotency is load-bearing, not defensive.</b> After this change EVERY drained scan
    /// releases twice — once here, once when the consumer releases the exported stream — so the flag is what
    /// makes the ordinary path correct, not merely what tolerates a rare double free.</para>
    /// <para><b>⚠ What this does NOT fix:</b> a scan the consumer abandons early (LIMIT, a short-circuiting
    /// join) never reaches end of result set, so its reader still lives to teardown. And it cannot help the
    /// bulk-insert collision (error 595), where the scan feeds the bulk row by row and the reader must stay
    /// open by construction — that one needs materialisation.</para>
    /// </remarks>
    private void Release(string why)
    {
        if (_released)
        {
            return;
        }
        _released = true;
        // The ONLY way to observe this from outside: whether a drained reader is released at end of result
        // set or at query teardown is invisible from SQL. `why` is what makes the eager path falsifiable.
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("reader released ({Why})", why);
        }
        _reader.Dispose();
        _command.Dispose();
        // A borrowed (pinned-transaction) connection is owned by the catalog.
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
        // Stop the interrupt poller before the ClientContext it polls is freed.
        _interrupt?.Dispose();
    }

    public void Dispose() => Release("dispose");
}
