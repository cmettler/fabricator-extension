// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Wraps a bound host-query input so a SECOND scan of it is LOUD instead of empty.
/// <para>Why it exists: an input registered with <see cref="Host.Query(string, System.Collections.Generic.IReadOnlyList{System.ValueTuple{string, IArrowArrayStream}})"/>
/// is a connection-scoped view over a single-use stream. <see cref="InMemoryArrayStream"/> returns
/// <c>null</c> once drained, so if a plan ever scans that view twice the second scan sees ZERO ROWS and
/// succeeds. For a deletion-vector anti-join that is not a degraded result, it is DELETED ROWS COMING BACK —
/// silently. A <c>WITH … AS MATERIALIZED</c> CTE is what makes the single scan true today; this makes a future
/// planner change that breaks that assumption fail instead of corrupting an answer.</para>
/// <para>A read after end-of-stream is the signal, not the read count: a stream legitimately reports
/// end-of-stream once per scan, so throwing on the SECOND end-of-stream read is what distinguishes "drained"
/// from "scanned again".</para>
/// </summary>
internal sealed class SingleScanArrowStream : IArrowArrayStream
{
    private readonly IArrowArrayStream _inner;
    private readonly string _name;
    private bool _drained;

    internal SingleScanArrowStream(IArrowArrayStream inner, string name)
    {
        _inner = inner;
        _name = name;
    }

    public Schema Schema => _inner.Schema;

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        if (_drained)
        {
            throw new System.InvalidOperationException(
                $"fabricator: bound input '{_name}' was scanned more than once. It is a single-use stream, so a "
                + "second scan would silently contribute NO rows — for a deletion-vector anti-join that resurrects "
                + "deleted rows. The query must reference it exactly once (a WITH … AS MATERIALIZED CTE).");
        }
        var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            _drained = true;
        }
        return batch;
    }

    public void Dispose() => _inner.Dispose();
}
