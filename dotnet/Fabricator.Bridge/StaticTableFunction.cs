using System.Collections.Generic;
using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Base class for a custom table function whose output schema is FIXED (independent of the call arguments) —
/// the common case. Implementers provide <see cref="OutputSchema"/> + <see cref="Invoke"/> (one batch of
/// constant args in, result rows out), and this adapter supplies the <see cref="ICatalogTableFunction.Bind"/>
/// machinery. Functions whose output columns depend on the arguments implement <see cref="ICatalogTableFunction"/>
/// directly instead.
/// </summary>
public abstract class StaticTableFunction : ICatalogTableFunction
{
    public abstract string SchemaName { get; }
    public abstract string Name { get; }
    public abstract Schema Parameters { get; }

    /// <summary>The fixed result columns (names + Arrow types).</summary>
    public abstract Schema OutputSchema { get; }

    // Pure C#: no SQL to push into, so neither half is claimed and DuckDB re-applies both above the scan.
    // Virtual because a subclass CAN honour them — but only by actually filtering / projecting its own rows;
    // see the guarantees on IArrowTableFunctionBinding.
    public virtual bool SupportsFilterPushdown => false;
    public virtual bool SupportsProjectionPushdown => false;

    /// <summary>Produces the result rows for one call (<paramref name="args"/> = the 1-row constant args),
    /// streamed asynchronously — implement as an async iterator (a synchronous generator just yields).</summary>
    public abstract IAsyncEnumerable<RecordBatch> Invoke(RecordBatch args, CancellationToken ct = default);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(this, args);

    private sealed class Binding : IArrowTableFunctionBinding
    {
        private readonly StaticTableFunction _fn;
        private readonly RecordBatch _args;

        public Binding(StaticTableFunction fn, RecordBatch args)
        {
            _fn = fn;
            _args = args;
        }

        public Schema OutputSchema => _fn.OutputSchema;
        public bool SupportsFilterPushdown => _fn.SupportsFilterPushdown;
        public bool SupportsProjectionPushdown => _fn.SupportsProjectionPushdown;

        // NOTE (lifetime, applies to every binding that ignores pushed filters): dispose FilterValues here,
        // in a PLAIN method — never inside an `async IAsyncEnumerable` body. An async-iterator body does not
        // run until the first MoveNext, and if the scan is torn down without a row being pulled it never runs
        // at all, leaving the imported stream to the GC finalizer — a release at an unpredictable time. The
        // host owns the underlying producer only for the duration of the scan (see
        // ArrowStreamGlobalState::filter_value_producer), so a release after that is a use-after-free that
        // only macOS reports (it validates the mutex signature; glibc/Windows corrupt silently).
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return _fn.Invoke(_args, ct);
        }

        public void Dispose() => _args.Dispose();
    }
}
