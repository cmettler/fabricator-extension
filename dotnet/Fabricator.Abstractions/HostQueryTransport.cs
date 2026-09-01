// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// The seam through which a plugin reaches <c>host_query</c> — running SQL on the DuckDB instance that is
/// hosting us. Declared HERE, in the contract assembly, and FILLED IN by the bridge at boot, so a plugin
/// uses it with a reference to <c>Fabricator.Abstractions</c> alone.
/// </summary>
/// <remarks>
/// <para>
/// The precedent is <see cref="HostHttpTransport"/> and its rules carry over verbatim. ⚠ <b>The delegate
/// carries no opener, deliberately.</b> The bridge's implementation reads the AMBIENT ClientContext at call
/// time, which is the only correct moment: a catalog is DATABASE-scoped and outlives the connection that
/// attached it, so anything holding an ATTACH-time <c>ClientContext*</c> would be a dangling pointer the day
/// that connection closes — the <c>table_stats</c> SIGSEGV class.
/// </para>
/// <para>
/// ⚠ It follows that this is usable only from INSIDE an ABI crossing (a scan, a function's
/// <c>Execute</c>, a provider's <c>OpenCatalog</c>) or anywhere the ambient still flows from one. The
/// ambient is an <c>AsyncLocal</c>, so it survives <c>await</c> and <c>Task.Run</c>; it does NOT survive a
/// thread parked before the crossing began.
/// </para>
/// <para>
/// ⚠⚠ <b>A query here runs on its OWN connection, so it reads COMMITTED state.</b> A caller inside
/// <c>BEGIN; INSERT …;</c> does NOT observe that transaction's uncommitted rows — MEASURED, and measured
/// identically at bind time and at execute time, and identically on a plain DuckDB table, so it is a
/// property of opening a separate connection rather than anything the caller does (docs/fluid-templating.md
/// §8.2). One rule to document, not two.
/// </para>
/// <para>
/// ⚠⚠ <b>It does not refuse writes, and a caller that binds must.</b> The transport will happily run an
/// INSERT. A caller invoked during BIND — a sqlgen generator, a template rendered at bind — must refuse
/// anything that is not a SELECT, because a bind repeats and happens without execution: MEASURED, a
/// bind-time write fires on <c>EXPLAIN</c> of a statement that never runs, and again on merely DEFINING a
/// view over it (docs/fluid-templating.md §8.3). See <see cref="Fabricator.FluidPlugin"/>'s <c>query</c>
/// for the refusal shape: decide on the STATEMENT KIND, before execution, never by catching afterwards.
/// </para>
/// </remarks>
public static class HostQueryTransport
{
    /// <summary>
    /// Runs <paramref name="sql"/> on a FRESH host connection and returns the result as an Arrow stream the
    /// caller owns and disposes. <c>parameters</c> is an optional 1-row batch bound positionally to the
    /// statement's <c>?</c> / <c>$1</c> placeholders. Installed by the bridge; null until then.
    /// </summary>
    /// <remarks>
    /// ⚠ Prefer the parameter form over building a literal: it is the difference between handing the engine
    /// a VALUE and handing it SQL text, and on any path that classifies or validates user input that
    /// distinction is the whole defence.
    /// </remarks>
    public static Func<string, RecordBatch?, IArrowArrayStream>? Query { get; set; }

    /// <summary>True once the bridge has installed the transport and the host supports host_query.</summary>
    public static bool IsAvailable => Query is not null;
}
