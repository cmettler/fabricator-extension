using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Runs a bound scalar function over an Arrow argument stream: one output batch per non-empty input batch,
/// the result column typed by whatever the binding's <see cref="IScalarFunctionBinding.Invoke"/> returns.
/// The single implementation of that loop — it used to be copy-pasted three times, once per caller of the
/// removed <c>execute_scalar</c> (the global registry, the catalog function set, and SqlServerBackend).
/// </summary>
public static class ScalarBindingRunner
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
            if (inBatch.Length == 0)
            {
                continue;
            }
            var resultArray = handle.Binding.Invoke(inBatch);
            resultSchema ??= new Schema(new[] { new Field("result", resultArray.Data.DataType, nullable: true) }, null);
            batches.Add(new RecordBatch(resultSchema, new[] { resultArray }, resultArray.Length));
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
