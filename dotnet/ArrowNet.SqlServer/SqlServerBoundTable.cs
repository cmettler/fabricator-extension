using System;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using ArrowNet.Bridge;

namespace ArrowNet.SqlServer;

// IBoundTable implementations for the Phase 5 table-function session model (table_bind / table_execute /
// table_close). A bound table resolves its output schema once and runs the scan possibly many times (once
// per execution); the host frees it via table_close. Two shapes, matching the two execution models:

// A stored proc or a custom (pure-C#) table function — wraps an IArrowTableFunctionBinding. Its batches
// carry the FULL output schema (no SQL projection), so the result streams via AsyncEnumerableArrowStream
// over the binding's IAsyncEnumerable and DuckDB projects + filters above the scan. `supportsPushdown` here
// drives the host's push_projection (by-name column mapping), NOT SQL pushdown: it is true for a custom
// function (the full result is mapped by NAME, as before) and false for a stored proc (full result,
// projected positionally above the scan) — preserving the prior `push_projection = !is_proc` behavior.
internal sealed class BindingBoundTable : IBoundTable
{
    private readonly IArrowTableFunctionBinding _binding;
    private readonly bool _supportsPushdown;

    public BindingBoundTable(IArrowTableFunctionBinding binding, bool supportsPushdown)
    {
        _binding = binding;
        _supportsPushdown = supportsPushdown;
    }

    public Schema OutputSchema => _binding.OutputSchema;
    public bool SupportsPushdown => _supportsPushdown;

    public IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues) =>
        new AsyncEnumerableArrowStream(
            _binding.OutputSchema, _binding.Execute(new TableFunctionScan(specJson, filterValues)));

    public void Dispose() => _binding.Dispose();
}

// A discovered SQL Server TVF — wraps a SqlServerTableValuedFunction + the constant args. The scan pushes
// projection + filter into the SELECT (ScanFromSource), so its stream is returned directly (its schema is
// the PROJECTED schema, matching its batches). The args batch is reused across executions and disposed here.
internal sealed class TvfBoundTable : IBoundTable
{
    private readonly SqlServerTableValuedFunction _tvf;
    private readonly RecordBatch _args;

    public TvfBoundTable(SqlServerTableValuedFunction tvf, RecordBatch args)
    {
        _tvf = tvf;
        _args = args;
    }

    public Schema OutputSchema => _tvf.OutputSchema;
    public bool SupportsPushdown => true;

    public IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues) =>
        _tvf.ExecuteScan(_args, specJson, filterValues); // reads _args (does not consume it)

    public void Dispose() => _args.Dispose();
}
