using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// An <see cref="IBoundTable"/> over an <see cref="IArrowTableFunctionBinding"/> — for a stored proc, a custom
/// (pure-C#) table function, or a connection-free GLOBAL table function. Its batches carry the FULL output
/// schema (no SQL projection), so the result streams via <see cref="AsyncEnumerableArrowStream"/> over the
/// binding's <c>Execute</c> and DuckDB projects + filters above the scan. <paramref name="supportsPushdown"/>
/// drives the host's by-name projection mapping (NOT SQL pushdown): true for a custom/global function (full
/// result mapped by NAME), false for a stored proc (full result, projected positionally above the scan).
/// </summary>
public sealed class BindingBoundTable : IBoundTable
{
    private readonly IArrowTableFunctionBinding _binding;
    private readonly bool _supportsPushdown;

    public BindingBoundTable(IArrowTableFunctionBinding binding, bool supportsPushdown)
    {
        _binding = binding;
        _supportsPushdown = supportsPushdown;
    }

    public Schema OutputSchema => _binding.OutputSchema;
    public bool MapResultByName => _supportsPushdown;

    public IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues) =>
        new AsyncEnumerableArrowStream(
            _binding.OutputSchema, _binding.Execute(new TableFunctionScan(specJson, filterValues)));

    public void Dispose() => _binding.Dispose();
}
