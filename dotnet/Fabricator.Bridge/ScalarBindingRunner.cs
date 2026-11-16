// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Runs a bound scalar function over an Arrow argument stream: one output batch per non-empty input batch,
/// the result column typed by whatever the binding's <see cref="IScalarFunctionBinding.Invoke"/> returns.
/// The single implementation of that loop — it used to be copy-pasted three times, once per caller of the
/// removed <c>execute_scalar</c> (the global registry, the catalog function set, and SqlServerBackend).
/// </summary>
internal static class ScalarBindingRunner
{
    /// <summary>Consumes + disposes <paramref name="args"/>. Does NOT dispose the binding.</summary>
    public static IArrowArrayStream Execute(ScalarBindingHandle handle, IArrowArrayStream args)
    {
        using var input = args;
        var batches = new List<RecordBatch>();
        Schema? resultSchema = null;
        RecordBatch? inBatch;
        while ((inBatch = input.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
        {
            IArrowArray result;
            try
            {
                if (inBatch.Length == 0)
                {
                    continue;
                }
                // The input chunk is DuckDB's memory and ours to release, but the result may BE one of its columns
                // (an identity or pass-through function) or share its buffers. So the result is kept through a
                // retained view, and only then are the input and the result as returned released: a buffer the
                // result shares stays alive through the view's reference, everything else is freed now instead of
                // by the finalizer. Disposing the same handle twice (result == an input column) is a no-op.
                var resultArray = handle.Binding.Invoke(inBatch);
                result = BorrowedBatch.View(resultArray);
                resultArray.Dispose();
            }
            finally
            {
                inBatch.Dispose();
            }
            resultSchema ??= new Schema(new[] { new Field("result", result.Data.DataType, nullable: true) }, null);
            batches.Add(new RecordBatch(resultSchema, new[] { result }, result.Length));
        }
        // No non-empty input => a correctly-typed zero-row stream. This is the ONLY place the declared field
        // is read on the execute path, which is what keeps a discovered SQL UDF's per-call-site cost at zero
        // server round trips: for that provider the property is a query, and this branch is unreachable in
        // practice (DuckDB does not invoke a scalar with an empty chunk).
        if (resultSchema is null)
        {
            var field = handle.ResolvedResult ?? handle.Definition.Result
                ?? throw new System.InvalidOperationException(
                    $"scalar function '{handle.Definition.Name}' has no resolved result type");
            resultSchema = new Schema(new[] { new Field("result", field.DataType, nullable: true) }, null);
        }
        return new InMemoryArrayStream(resultSchema, batches);
    }
}
