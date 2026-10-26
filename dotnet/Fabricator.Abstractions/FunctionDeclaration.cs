// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// One catalog-bound routine a provider declares (<see cref="IProviderCatalog.GetFunctions"/>): where it binds,
/// its name, and how the host registers it.
/// </summary>
/// <param name="SchemaPath">The schema to bind into. A schema the catalog did not discover is skipped, so an ATTACH
/// <c>schema_filter</c> gates functions too.</param>
/// <param name="Name">The function's name, resolved as <c>db.schema.name(…)</c>.</param>
/// <param name="Kind">The host registration kind: <c>scalar</c> / <c>table</c> / <c>proc</c> / <c>table_sql</c> /
/// <c>inout</c> / <c>lateral</c> / <c>collector</c> / <c>aggregate</c> / <c>aggregate_spill</c>. ⚠ Anything else
/// is ignored by the host, which is a very quiet way to lose a function.</param>
/// <param name="ParamCount">Declared argument count (positional ++ named), or the input-table width for the in-out
/// kinds. Reported by <c>fabricator_functions()</c>; the host does not register from it.</param>
/// <param name="ReturnType">Arrow type name of a scalar/aggregate result; empty for every other kind.</param>
/// <param name="VarArgs">The variadic tail's type, or empty when the function is not variadic — beside a non-empty
/// value <paramref name="ParamCount"/> is a minimum. Mirrors <c>duckdb_functions().varargs</c>.</param>
public sealed record FunctionDeclaration(SchemaPath SchemaPath, string Name, string Kind, int ParamCount = 0,
                                         string ReturnType = "", string VarArgs = "");
