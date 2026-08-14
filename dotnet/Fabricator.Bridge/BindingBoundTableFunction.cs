using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// An <see cref="IBoundTableFunction"/> over an <see cref="ITableFunctionBinding"/> — for a stored proc, a custom
/// (pure-C#) table function, or a connection-free GLOBAL table function. Its batches carry the FULL output
/// schema (no SQL projection), so the result streams via <see cref="AsyncEnumerableArrowStream"/> over the
/// binding's <c>Execute</c> and DuckDB projects + filters above the scan. <paramref name="supportsPushdown"/>
/// drives the host's by-name projection mapping (NOT SQL pushdown): true for a custom/global function (full
/// result mapped by NAME), false for a stored proc (full result, projected positionally above the scan).
/// </summary>
public sealed class BindingBoundTableFunction : IBoundTableFunction
{
    private readonly ITableFunctionBinding _binding;
    private readonly bool _supportsPushdown;

    public BindingBoundTableFunction(ITableFunctionBinding binding, bool supportsPushdown)
    {
        _binding = binding;
        _supportsPushdown = supportsPushdown;
    }

    public Schema OutputSchema => _binding.OutputSchema;
    public bool MapResultByName => _supportsPushdown;

    public IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues)
    {
        var scan = new TableFunctionScan(specJson, filterValues);
        // ⚠ THE DECLARED SCHEMA IS THE CONTRACT WITH arrow_ingest, AND IT USED TO BE UNCONDITIONALLY THE FULL
        // ONE — which is what made projection pushdown impossible for every binding behind this wrapper, the
        // two path-based Delta readers included. A binding that emitted a subset would have mismatched the
        // declaration, and the host does not error on that: it reads columns that are not there (SIGSEGV).
        // So the projection is honoured HERE, where both the full schema and the spec are in hand, rather
        // than by adding a per-scan schema to the binding interface.
        var schema = _binding.SupportsProjectionPushdown
            ? ProjectionPlan.Schema(_binding.OutputSchema, scan.Spec?.Columns)
            : _binding.OutputSchema;
        return new AsyncEnumerableArrowStream(schema, _binding.Execute(scan));
    }

    public void Dispose() => _binding.Dispose();
}
