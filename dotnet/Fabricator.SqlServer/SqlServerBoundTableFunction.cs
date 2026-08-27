// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;

namespace Fabricator.SqlServer;

// IBoundTableFunction implementations for the Phase 5 table-function session model (tablefn_bind / tablefn_execute /
// tablefn_close). A bound table resolves its output schema once and runs the scan possibly many times (once
// per execution); the host frees it via tablefn_close. Two shapes, matching the two execution models:

// (BindingBoundTableFunction — the ITableFunctionBinding wrapper used by stored procs, custom, and global table
// functions — now lives in Fabricator.Bridge so the connection-free global path can reuse it.)

// A discovered SQL Server TVF — wraps a SqlServerTableValuedFunction + the constant args. The scan pushes
// projection + filter into the SELECT (ScanFromSource), so its stream is returned directly (its schema is
// the PROJECTED schema, matching its batches). The args batch is reused across executions and disposed here.
internal sealed class TvfBoundTableFunction : IBoundTableFunction
{
    private readonly SqlServerTableValuedFunction _tvf;
    private readonly RecordBatch _args;

    public TvfBoundTableFunction(SqlServerTableValuedFunction tvf, RecordBatch args)
    {
        _tvf = tvf;
        _args = args;
    }

    public Schema OutputSchema => _tvf.OutputSchema;
    public bool MapResultByName => true;

    public IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues) =>
        _tvf.ExecuteScan(_args, specJson, filterValues); // reads _args (does not consume it)

    public void Dispose() => _args.Dispose();
}
