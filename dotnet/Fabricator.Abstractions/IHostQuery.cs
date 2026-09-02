// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// SQL on the DuckDB instance that is hosting us, as a host service (<see cref="FabricatorServices"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>No opener is passed, deliberately</b> — the bridge's implementation reads the AMBIENT
/// <c>ClientContext</c> at call time. See <see cref="FabricatorServices"/> for why capturing one would dangle
/// and for the crossing rule that follows.
/// </para>
/// <para>
/// ⚠⚠ <b>A query here runs on its OWN connection, so it reads COMMITTED state.</b> A caller inside
/// <c>BEGIN; INSERT …;</c> does NOT observe that transaction's uncommitted rows — MEASURED, and measured
/// identically at bind time and at execute time, and identically on a plain DuckDB table, so it is a property
/// of opening a separate connection rather than anything the caller does (docs/fluid-templating.md §8.2). One
/// rule to document, not two. <paramref name="inheritSession"/> does NOT change this: it copies SETTINGS, not
/// transaction membership.
/// </para>
/// <para>
/// ⚠⚠ <b>It does not refuse writes, and a caller that binds must.</b> This will happily run an INSERT. A
/// caller invoked during BIND — a sqlgen generator, a template rendered at bind — must refuse anything that
/// is not a SELECT, because a bind repeats and happens without execution: MEASURED, a bind-time write fires
/// on <c>EXPLAIN</c> of a statement that never runs, and again on merely DEFINING a view over it
/// (docs/fluid-templating.md §8.3). See the Fluid plugin's <c>query</c> filter for the refusal shape: decide
/// on the STATEMENT KIND, before execution, never by catching afterwards.
/// </para>
/// </remarks>
public interface IHostQuery
{
    /// <summary>
    /// Runs <paramref name="sql"/> on a FRESH host connection and returns the result as an Arrow stream the
    /// caller owns and disposes.
    /// </summary>
    /// <param name="sql">The statement. ⚠ With <paramref name="parameters"/> this is ONE statement only (the
    /// host prepares it); without, several may be separated by <c>;</c> and the LAST one's result is
    /// returned.</param>
    /// <param name="parameters">An optional 1-row batch of parameter values; only row 0 is read, and an empty
    /// batch binds all-NULL rather than erroring.</param>
    /// <param name="inheritSession">
    /// <see langword="true"/> (the default) runs AS the calling session — its <c>TimeZone</c> and catalog
    /// search path are copied onto the fresh connection, so names resolve and timestamps read the way they do
    /// in the statement that reached us. <see langword="false"/> asks for a clean session, which is what a
    /// caller wants when the SQL must mean the same thing regardless of who invoked it.
    /// </param>
    /// <remarks>
    /// ⚠ <b>The STATEMENT decides how the batch binds.</b> When the batch's column names are all parameter
    /// names the statement actually declares (<c>$region</c>, <c>$min</c>), it binds BY NAME; otherwise it
    /// binds POSITIONALLY, which is what a <c>?</c> / <c>$1</c> statement gets — such a statement names its
    /// parameters "1", "2", … so ordinary column names cannot collide with it by accident.
    /// <para>⚠ Prefer the parameter form over building a literal: it is the difference between handing the
    /// engine a VALUE and handing it SQL text, and on any path that classifies or validates user input that
    /// distinction is the whole defence.</para>
    /// </remarks>
    IArrowArrayStream Query(string sql, RecordBatch? parameters = null, bool inheritSession = true);

    /// <summary>
    /// Runs a non-query statement (DDL / DML) and returns the affected-row count when the engine reports one.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ <b>The count is INFERRED from the result shape — the first column of the first batch when it is an
    /// Int64 — and NOT asked of the statement.</b> MEASURED 2026-09-02: DML yields its affected count, PURE
    /// DDL (<c>CREATE TABLE z(a INTEGER)</c>) yields <c>0</c>, and a <b>CTAS yields the rows it created</b>
    /// (<c>CREATE TABLE c AS SELECT * FROM range(7)</c> → <b>7</b>).
    /// <para>
    /// ⚠ <b>That last case DIVERGES from the SQL-surface <c>fabricator_host_exec</c>, which answers 0</b>,
    /// because the C++ side can ask DuckDB's <c>StatementReturnType::CHANGED_ROWS</c> and managed code
    /// cannot. An earlier version of this doc claimed the CTAS yielded 0 here too; it was copied from the
    /// other surface rather than measured on this one.
    /// </para>
    /// <para>
    /// ⚠ It follows that a SELECT returning one BIGINT reports that VALUE as a "count". A caller for whom
    /// that matters must refuse a SELECT before calling — with DuckDB's parser, not a prefix test.
    /// </para>
    /// <para>For several statements the count is the LAST one's.</para>
    /// </remarks>
    long ExecuteNonQuery(string sql);
}
