// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// A DuckDB <b>MACRO</b> a provider ships, declared as one complete <c>CREATE MACRO</c> statement. Registered at
/// extension load into DuckDB's SYSTEM catalog (like a built-in), so it resolves as a bare <c>fn(...)</c> —
/// or <c>FROM fn(...)</c> for a table macro — in every database, with no ATTACH.
///
/// <para>The host does NOT parse the body itself: it hands <see cref="CreateSql"/> to DuckDB's own parser and
/// registers the resulting <c>CreateMacroInfo</c>. So the FULL macro grammar works — named parameters with
/// defaults (<c>n := 8</c>), overload sets (<c>… AS a, (x, y) AS b</c>), and <c>AS TABLE &lt;query&gt;</c> for a
/// table macro (the parsed statement carries the scalar/table kind).</para>
///
/// <para>A macro is a SQL TEMPLATE expanded by the binder: parameters are substituted as EXPRESSIONS, so the
/// statement's structure and identifiers are fixed at declaration time (nothing is string-interpolated at call
/// time). When the SQL TEXT itself must depend on the arguments — object names, IN-list/UNION fan-out, metadata
/// looked up at bind time — use a SQL-generating table function instead
/// (<c>ISqlTableFunction</c>). See docs/macros-and-sqlgen-functions.md.</para>
/// </summary>
/// <param name="Name">
/// The macro's name, used for cross-provider duplicate detection and diagnostics. The AUTHORITATIVE
/// name/parameters/body come from parsing <see cref="CreateSql"/>, so this must match the name written there.
/// Prefix it to avoid colliding with DuckDB built-ins or another provider (e.g. <c>fabricator_…</c>).
/// </param>
/// <param name="CreateSql">
/// The complete statement, e.g.
/// <c>"CREATE MACRO fabricator_bucket_of(v, n := 8) AS bucket(n, v)"</c> or
/// <c>"CREATE MACRO fabricator_delta_head(path, n := 100) AS TABLE SELECT * FROM fabricator_delta_scan(path) LIMIT n"</c>.
/// A statement that does not parse as a single <c>CREATE MACRO</c> is skipped with a warning at load (a broken
/// provider macro never blocks the extension).
/// </param>
public sealed record MacroDefinition(string Name, string CreateSql);
