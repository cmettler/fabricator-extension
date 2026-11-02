// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// The result stream of a table-function scan, which also releases the scan's pushed filter constants when the
/// host releases the result.
/// <para>Why it exists: the filter constants arrive as a stream the host's <c>ArrowProducer</c> backs, and that
/// producer lives exactly as long as the scan. A binding that ignores pushed filters and never disposes
/// <see cref="TableFunctionScan.FilterValues"/> leaves the release to the GC finalizer, which runs it AFTER the
/// producer is gone: a use-after-free that macOS reports (a destroyed mutex throws) and Linux and Windows hide.
/// Seen in CI from <c>ducklake_delta_maintain(...) WHERE step IN (...)</c>. The host releases the result stream
/// before the producer, so releasing the constants here is always in time. A second dispose is a no-op, so a
/// binding that already disposed them is unaffected.</para>
/// </summary>
internal sealed class FilterValuesOwningStream : IArrowArrayStream
{
    private readonly IArrowArrayStream _inner;
    private IArrowArrayStream? _filterValues;

    internal FilterValuesOwningStream(IArrowArrayStream inner, IArrowArrayStream? filterValues)
    {
        _inner = inner;
        _filterValues = filterValues;
    }

    public Schema Schema => _inner.Schema;

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
        _inner.ReadNextRecordBatchAsync(cancellationToken);

    public void Dispose()
    {
        try
        {
            _inner.Dispose();
        }
        finally
        {
            _filterValues?.Dispose();
            _filterValues = null;
        }
    }
}
