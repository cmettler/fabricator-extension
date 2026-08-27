// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Adapts an <see cref="ICollectorBinding"/> to <see cref="IInOutBinding"/> so a collector flows
/// through the existing in-out exchange marshaling (<see cref="InOutExchangeStream"/> / the <c>inout_bind</c>
/// / <c>inout_exchange_open</c> ABI) with no ABI change. The semantic difference is entirely in the contract:
/// <c>Collect</c> reads all input before yielding and yields NO sentinels — which is exactly what the C++
/// collector operator's NON-gated (buffered) input stream allows, so there is no single-slot deadlock.
/// </summary>
public sealed class CollectorInOutBinding : IInOutBinding
{
    private readonly ICollectorBinding _inner;

    public CollectorInOutBinding(ICollectorBinding inner) => _inner = inner;

    public Schema OutputSchema => _inner.OutputSchema;

    public IAsyncEnumerable<RecordBatch> DoExchange(IAsyncEnumerable<RecordBatch> input,
                                                    CancellationToken ct = default) => _inner.Collect(input, ct);

    public void Dispose() => _inner.Dispose();
}
