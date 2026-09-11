// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The framework "pump": exposes an <see cref="IInOutFunctionBinding"/>'s <c>DoExchange</c> as a pull-based Arrow
/// output stream for the C++ exchange operator. The host pulls this stream synchronously (the Arrow C-stream
/// exporter blocks on <see cref="ReadNextRecordBatchAsync"/>); each pull drives <c>DoExchange</c> one step. The
/// input stream is the host-exported input (one chunk per gate tenure, null at EOF), wrapped as an
/// <see cref="IAsyncEnumerable{T}"/>. Sentinels (length-0 batches) pass through verbatim.
/// </summary>
internal sealed class InOutExchangeStream : IArrowArrayStream
{
    private readonly IInOutFunctionBinding _binding;
    private readonly IArrowArrayStream _input;   // imported from C++; owned + released here
    private readonly IAsyncEnumerator<RecordBatch> _out;
    private readonly Schema _schema;
    private bool _disposed;

    public InOutExchangeStream(IInOutFunctionBinding binding, IArrowArrayStream input,
                               IReadOnlyList<int>? projected = null)
    {
        // The SQL isolation (if any) was already resolved + set on the binding at bind time (InOutBind), so
        // there is nothing isolation-related to do here. See docs/provider-extensibility.md §3.
        _binding = binding;
        _input = input;
        _schema = binding.ProjectedOutputSchema(projected);
        _out = binding.DoExchange(ReadInput(), projected, CancellationToken.None).GetAsyncEnumerator();
    }

    // ⚠ What the binding SAYS it will produce for this projection, not its full declared schema: the C
    // stream's schema is read once, before any batch, so a binding that honours the hint must be able to
    // declare the narrower shape here or the host would read narrow batches through wide converters.
    public Schema Schema => _schema;

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        // Sync-over-async at the boundary: the C++ gate-holder blocks here while this chunk's work runs. The
        // hostfxr CLR has no SynchronizationContext, so GetResult cannot deadlock (proven by the 6.0 spike).
        bool has = _out.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        return new ValueTask<RecordBatch?>(has ? _out.Current : null);
    }

    // The C++ input stream's get_next yields one chunk per gate tenure (a released/null array at end).
    private async IAsyncEnumerable<RecordBatch> ReadInput()
    {
        while (true)
        {
            var b = await _input.ReadNextRecordBatchAsync().ConfigureAwait(false);
            if (b is null)
            {
                yield break;
            }
            yield return b;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            // Runs DoExchange's finally (commit / connection close).
            _out.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort teardown
        }
        _input.Dispose();
        // The binding is reused across re-executions; it is freed by inout_bind_close, not here.
    }
}
