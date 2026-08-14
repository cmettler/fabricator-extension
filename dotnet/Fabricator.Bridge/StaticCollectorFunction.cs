using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Convenience base for a custom COLLECTOR table-in-out with a FIXED output schema: override
/// <see cref="OutputSchema"/> and <see cref="Collect"/>, and the base supplies the
/// <see cref="ICatalogCollectorTableFunction.Bind"/> → binding wiring. This is to
/// <see cref="ICatalogCollectorTableFunction"/> what <c>StaticInOutFunction</c> is to
/// <see cref="ICatalogInOutFunction"/>.
///
/// The author writes the whole-table transform in <see cref="Collect"/>: read <c>allInput</c> to EOF
/// (every input batch — copy values out, don't retain the batch), then yield the full output. No sentinels —
/// the operator buffers all input before <c>Collect</c> runs. Cross-input state lives in <c>Collect</c> LOCALS
/// (a fresh enumerator runs per call, so state never leaks across prepared re-executions).
/// </summary>
public abstract class StaticCollectorFunction : ICatalogCollectorTableFunction
{
    /// <summary>Target catalog schema (e.g. "dbo").</summary>
    public abstract string SchemaName { get; }

    /// <summary>Function name.</summary>
    public abstract string Name { get; }

    /// <summary>The declared input-table columns.</summary>
    public abstract Schema InputSchema { get; }

    /// <summary>Optional constant "cost" args, declared as named parameters (e.g. <c>path := '…'</c>).</summary>
    public virtual Schema NamedParameters { get; } = new Schema(System.Array.Empty<Field>(), metadata: null);

    /// <summary>
    /// The canonical signature: the input table as a <see cref="Params.TableInput"/> field, then any named
    /// cost args. Composed here so a subclass keeps declaring the two halves it cares about.
    /// </summary>
    public Schema Parameters => Params.Combine(
        new Schema(new[] { Params.TableInput("input", InputSchema) }, metadata: null), NamedParameters);

    /// <summary>The fixed output columns.</summary>
    public abstract Schema OutputSchema { get; }

    /// <summary>The whole-table transform: read <paramref name="allInput"/> fully, then yield the output.</summary>
    public abstract IAsyncEnumerable<RecordBatch> Collect(
        IAsyncEnumerable<RecordBatch> allInput, CancellationToken ct = default);

    public ICollectorBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding(this);

    private sealed class Binding : ICollectorBinding
    {
        private readonly StaticCollectorFunction _fn;

        public Binding(StaticCollectorFunction fn) => _fn = fn;

        public Schema OutputSchema => _fn.OutputSchema;

        public IAsyncEnumerable<RecordBatch> Collect(IAsyncEnumerable<RecordBatch> allInput,
                                                     CancellationToken ct = default) => _fn.Collect(allInput, ct);

        public void Dispose()
        {
        }
    }
}
