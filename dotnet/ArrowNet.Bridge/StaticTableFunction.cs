using System.Collections.Generic;
using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// Base class for a custom table function whose output schema is FIXED (independent of the call arguments) —
/// the common case. Implementers provide <see cref="OutputSchema"/> + <see cref="Invoke"/> (one batch of
/// constant args in, result rows out), and this adapter supplies the <see cref="IArrowTableFunction.Bind"/>
/// machinery. Functions whose output columns depend on the arguments implement <see cref="IArrowTableFunction"/>
/// directly instead.
/// </summary>
public abstract class StaticTableFunction : IArrowTableFunction
{
    public abstract string SchemaName { get; }
    public abstract string Name { get; }
    public abstract Schema Parameters { get; }

    /// <summary>The fixed result columns (names + Arrow types).</summary>
    public abstract Schema OutputSchema { get; }

    /// <summary>Produces the result rows for one call (<paramref name="args"/> = the 1-row constant args).</summary>
    public abstract IEnumerable<RecordBatch> Invoke(RecordBatch args);

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
        public bool SupportsPushdown => false; // pure C#: no SQL to push into; DuckDB re-applies above the scan

        public IEnumerable<RecordBatch> Execute(TableFunctionScan scan)
        {
            scan.FilterValues?.Dispose();
            return _fn.Invoke(_args);
        }

        public void Dispose() => _args.Dispose();
    }
}
