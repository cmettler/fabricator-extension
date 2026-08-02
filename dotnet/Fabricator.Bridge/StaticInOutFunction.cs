using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Convenience base for a custom table-in-out with a FIXED output schema: override <see cref="OutputSchema"/>
/// and <see cref="DoExchange"/>, and the base supplies the <see cref="ICatalogInOutFunction.Bind"/> → binding
/// wiring (no separate binding class to write). This is to <see cref="ICatalogInOutFunction"/> what
/// <c>StaticTableFunction</c> is to <c>ICatalogTableFunction</c>.
///
/// The author owns the streaming loop in <see cref="DoExchange"/>: read <c>input</c> (one batch per input
/// chunk, ends at EOF), yield output batches, and yield a length-0 sentinel (<c>InOutExchange.EmptyBatch</c>)
/// after each input chunk. Keep any cross-chunk state in DoExchange LOCALS — a fresh enumerator runs per
/// exchange, so state never leaks across prepared re-executions. For an output schema that depends on the
/// call's args, implement <see cref="ICatalogInOutFunction"/> directly instead.
/// </summary>
public abstract class StaticInOutFunction : ICatalogInOutFunction
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

    /// <summary>The streaming transform: read <paramref name="input"/> and yield output — a non-empty batch is
    /// emitted (HAVE_MORE_OUTPUT), a length-0 batch (see <c>InOutExchange.EmptyBatch</c>) is the per-input-chunk
    /// sentinel (NEED_MORE_INPUT). Keep cross-chunk state in locals (fresh per exchange).</summary>
    public abstract IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, CancellationToken ct = default);

    public IArrowInOutBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding(this);

    // Thin per-call binding: a fixed output schema + forwards DoExchange to the function (a fresh enumerator
    // per exchange). The function holds no per-exchange state, so it is safely reused across re-executions.
    private sealed class Binding : IArrowInOutBinding
    {
        private readonly StaticInOutFunction _fn;

        public Binding(StaticInOutFunction fn) => _fn = fn;

        public Schema OutputSchema => _fn.OutputSchema;

        public IAsyncEnumerable<RecordBatch> DoExchange(IAsyncEnumerable<RecordBatch> input,
                                                        CancellationToken ct = default) => _fn.DoExchange(input, ct);

        public void Dispose()
        {
        }
    }
}
