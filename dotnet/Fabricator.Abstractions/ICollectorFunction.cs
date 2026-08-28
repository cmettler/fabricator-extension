// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// A bound COLLECTOR table-in-out call (the pipeline-breaker shape). Unlike the streaming
/// <see cref="IInOutFunctionBinding"/>, a collector emits NOTHING until it has seen ALL input: the C++ collector
/// operator buffers every input chunk, then (once, after all branches) hands the complete input to
/// <see cref="Collect"/> and emits its full output. So there is <b>no per-chunk sentinel</b> — input EOF and
/// output EOF are the genuine signals. Right for whole-table transforms (inject the input as a lookup table,
/// sort/dedup the whole input, summarize-then-emit) where the output depends on the entire input. It is a
/// pipeline breaker by definition: the input is buffered (no streaming-memory bound). For bounded memory use
/// the streaming <see cref="ICatalogInOutFunction"/> instead.
/// </summary>
public interface ICollectorFunctionBinding : IDisposable
{
    /// <summary>The FULL output columns (may depend on the call's args and the input schema).</summary>
    Schema OutputSchema { get; }

    /// <summary>The whole-table transform: <paramref name="allInput"/> yields EVERY input batch (the operator
    /// has already buffered them all; the stream ends at real input EOF), and the returned enumerable is the
    /// full output (emitted lazily, but the operator materializes it once after all input). Copy values out of
    /// each input batch — the Arrow buffers are freed after the batch is consumed; do not retain the batch.</summary>
    IAsyncEnumerable<RecordBatch> Collect(IAsyncEnumerable<RecordBatch> allInput, CancellationToken ct = default);
}

/// <summary>
/// A provider-authored custom COLLECTOR table-in-out function: the pipeline-breaker analog of
/// <see cref="ICatalogInOutFunction"/>. <see cref="Bind"/> resolves the per-call binding from the constant
/// "cost" args + the input-table schema. Surfaced into the catalog as <c>kind='collector'</c> so the C++
/// catalog registers it as a <c>{TABLE}</c>-param table function under the bare name, routed to the Sink+Source
/// collector operator (NOT the streaming exchange). For a FIXED output schema derive from
/// <see cref="StaticCollectorFunction"/>.
/// </summary>
public interface ICollectorFunction
{
    /// <summary>Function name. Catalog: <c>SELECT * FROM db.schema.Name(&lt;input&gt;)</c>; global: the bare name.</summary>
    string Name { get; }

    /// <summary>
    /// The call signature, including the INPUT TABLE itself — exactly one <see cref="Params.TableInput"/>
    /// field plus any constant "cost" args (typically <see cref="Params.Named"/>, e.g. <c>path := '…'</c>),
    /// whose supplied values arrive in <see cref="Bind"/>'s <c>args</c>. See
    /// <see cref="IInOutFunction.Parameters"/> for why the input table is a parameter rather than a
    /// schema of its own.
    /// </summary>
    Schema Parameters { get; }

    /// <summary>Binds one call: <paramref name="args"/> (nullable) are the constant "cost" args (1-row batch);
    /// <paramref name="inputSchema"/> is the actual input table's schema. Returns the per-call binding.</summary>
    ICollectorFunctionBinding Bind(RecordBatch? args, Schema inputSchema);
}

/// <summary>A catalog-bound collector table-in-out function (attach-time scope) —
/// <see cref="ICollectorFunction"/> plus the <see cref="SchemaName"/>. For a connection-free collector,
/// implement the base <see cref="ICollectorFunction"/> and declare it as a global instead.</summary>
public interface ICatalogCollectorFunction : ICollectorFunction
{
    /// <summary>Target catalog schema (e.g. "dbo").</summary>
    string SchemaName { get; }
}
