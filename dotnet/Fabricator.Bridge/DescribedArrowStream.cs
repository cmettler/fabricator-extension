// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// An <see cref="IArrowArrayStream"/> that can report its SCHEMA without executing anything, and executes
/// only when rows are actually pulled. It exists to stop <c>fabricator_query</c> running the caller's SQL
/// TWICE.
/// </summary>
/// <remarks>
/// <para><b>The defect it fixes, MEASURED.</b> <c>fabricator_query</c> is a TABLE function, so DuckDB needs
/// its column types at BIND — and the host obtains them by calling the scan factory just to read
/// <c>get_schema</c> (<c>arrow_ingest.cpp</c>'s <c>PopulateReturnSchema</c>), then calls it again to produce
/// rows. Since <see cref="DbDataReaderArrowStream"/> can only be constructed around an ALREADY-EXECUTED
/// reader, the bind-time probe was a full execution:
/// <c>SELECT * FROM fabricator_query('db','INSERT INTO t VALUES (1); SELECT 1 AS x')</c> inserted <b>two</b>
/// rows. The same statement through <c>fabricator_exec</c> inserted one.</para>
/// <para><b>Why no ABI change is needed.</b> The Arrow C stream interface has SEPARATE <c>get_schema</c> and
/// <c>get_next</c> callbacks, and the host's bind-time probe calls <c>get_schema</c> and then releases the
/// stream. So a stream that DESCRIBES on the first and EXECUTES on the second satisfies both callers without
/// the host having to say which it wants.</para>
/// <para><b>⚠ The describe and the execution must agree, or the host reads a schema that does not match the
/// rows</b> — the <c>duckdb_arrow_scan</c> mismatch class. What makes that unlikely here is not care but
/// CONSTRUCTION: on the SQL Server provider both answers are built by
/// <see cref="Conversion.SqlArrowMapping.ToArrowField"/> from a <c>DbColumn</c>, so the describe path and the
/// execute path run the SAME mapping over the same kind of metadata. A provider whose describe were derived
/// some other way would be taking a real risk.</para>
/// <para><b>⚠ The fallback is REQUIRED and it costs nothing when unused.</b> A provider that cannot describe a
/// given statement — <c>sp_describe_first_result_set</c> cannot handle every shape, and non-SQL-Server
/// providers do not implement it at all — returns null, and this class then EXECUTES to answer the schema and
/// KEEPS that stream for the rows. So the unsupported case is exactly today's behaviour (one execution per
/// exported stream) rather than a failure, and the supported case saves one whole execution of the caller's
/// SQL.</para>
/// <para>⚠ Not used for the provider's own internal reads. Those call <c>catalog.ExecuteQuery</c> directly in
/// C# and want the rows immediately, so wrapping them would only add a describe round trip they never use.
/// This is wired at the <c>execute_query</c> ABI handler alone.</para>
/// </remarks>
public sealed class DescribedArrowStream : IArrowArrayStream
{
    private readonly Func<Schema?> _describe;
    private readonly Func<IArrowArrayStream> _execute;
    private readonly object _gate = new();
    private IArrowArrayStream? _inner;
    private Schema? _schema;
    private bool _disposed;

    /// <param name="describe">
    /// Reports the result schema WITHOUT executing, or null when this statement cannot be described.
    /// </param>
    /// <param name="execute">Runs the statement for real. Called at most once.</param>
    public DescribedArrowStream(Func<Schema?> describe, Func<IArrowArrayStream> execute)
    {
        _describe = describe;
        _execute = execute;
    }

    /// <summary>
    /// The result schema: from the describe if it can answer, else from an execution whose stream is retained
    /// so the rows do not cost a second one.
    /// </summary>
    /// <remarks>
    /// ⚠ A describe FAILURE is not swallowed. Only a provider's explicit "I cannot describe this" (null) falls
    /// back — an exception propagates, because a describe that threw is telling the caller something real
    /// about their SQL (a syntax error, a missing object), and reporting that at BIND is strictly better than
    /// discovering it mid-scan.
    /// </remarks>
    public Schema Schema
    {
        get
        {
            lock (_gate)
            {
                if (_schema is not null)
                {
                    return _schema;
                }
                _schema = _describe();
                if (_schema is null)
                {
                    _inner = _execute();
                    _schema = _inner.Schema;
                }
                return _schema;
            }
        }
    }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        IArrowArrayStream inner;
        lock (_gate)
        {
            if (_disposed)
            {
                return new ValueTask<RecordBatch?>((RecordBatch?)null);
            }
            inner = _inner ??= _execute();
        }
        return inner.ReadNextRecordBatchAsync(cancellationToken);
    }

    /// <summary>
    /// Releases the underlying stream if one was ever created. ⚠ A stream whose schema was described and whose
    /// rows were never pulled owns NOTHING — no connection, no reader — which is precisely the point: the
    /// host's bind-time probe now costs a describe and a dispose instead of a query.
    /// </summary>
    public void Dispose()
    {
        IArrowArrayStream? inner;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            inner = _inner;
            _inner = null;
        }
        inner?.Dispose();
    }
}
