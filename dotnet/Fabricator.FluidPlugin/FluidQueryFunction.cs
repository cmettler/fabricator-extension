// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.FluidPlugin;

/// <summary>
/// <c>fluid_query(template [, params := …])</c> — a SQL-GENERATING table function whose SQL is a Liquid
/// template. The rendered text IS the statement: at BIND time DuckDB hands the generator the call's constant
/// arguments and SUBSTITUTES the returned SELECT for the call (<c>bind_replace</c>, the <c>query_table()</c>
/// mechanism), so nothing streams through C# at execution and the generated SQL's own scans keep their full
/// pushdown, parallelism and join reordering.
///
/// <para>Where <see cref="FluidRenderFunction"/> makes TEXT from a template, this makes a RELATION:
/// <c>SELECT * FROM fluid_query('SELECT {{ n }} AS n', params := {'n': 1})</c>. The output schema falls out
/// of binding the generated SQL, so it may differ per call with nothing declared here.</para>
///
/// <para><b>⚠ WHAT IS AND IS NOT QUOTED.</b> <c>{{ x }}</c> interpolates RAW, deliberately — a template must
/// be able to emit table names, predicates and whole SQL fragments, which is the only reason to generate SQL
/// from a template at all. Values that are DATA go through the plugin's two filters:
/// <c>{{ v | sql }}</c> renders a SQL literal (<see cref="DuckSql.Literal"/>: quoted string, invariant
/// number, typed date/time, <c>NULL</c>) and <c>{{ n | sql_ident }}</c> renders a quoted identifier. Both are
/// allow-lists — a value with no provably safe rendering is refused by name rather than interpolated.</para>
/// </summary>
/// <remarks>
/// <b>⚠ THE GENERATOR RUNS AT BIND, REPEATEDLY, AND WITHOUT EXECUTION.</b> That is the
/// <see cref="ISqlTableFunction"/> contract: an <c>EXPLAIN</c>, a <c>DESCRIBE</c> or a <c>CREATE VIEW</c>
/// binds without running anything, and a view or prepared statement re-binds every time. Rendering is
/// deterministic and side-effect-free, so this satisfies it — and it is also why a Fluid <c>query</c>
/// function (the follow-on slice) is a separate question rather than a free addition: it would execute SQL
/// during someone else's bind. See docs/fluid-templating.md §3.
/// </remarks>
internal sealed class FluidQueryFunction : ISqlTableFunction
{
    public string Name => "fluid_query";

    public Schema Parameters => new(new[]
    {
        // NON-nullable: a NULL template has no meaningful statement to generate, and the host's own check
        // then refuses it by parameter name instead of failing somewhere inside the parser.
        Params.Positional("template", StringType.Default, nullable: false),
        // NAMED and optional, following fabricator_sql_seq's `cols :=`. The NullType sentinel registers it as
        // DuckDB ANY, so the same parameter takes a STRUCT (type-safe, no quoting), a MAP, or a JSON string —
        // exactly what fluid_render accepts, because the params bag means the same thing in both.
        Params.Named("params", NullType.Default),
    }, metadata: null);

    public string GenerateSql(RecordBatch args)
    {
        int t = args.Schema.GetFieldIndex("template");
        if (t < 0 || args.Column(t) is not StringArray templates || templates.Length == 0 || templates.IsNull(0))
        {
            throw new ArgumentException("fluid_query: 'template' must be a non-NULL VARCHAR");
        }

        // ⚠ BY NAME, not by position: a named parameter is present only when SUPPLIED, so its column index
        // depends on the call site. Absent => no variables, which is a legitimate call (`fluid_query('SELECT 1')`).
        int p = args.Schema.GetFieldIndex("params");
        var bag = p >= 0 ? args.Column(p) : null;

        return FluidEngine.Render(Name, templates.GetString(0),
                                  ctx => FluidValueModel.Bind(ctx, bag, 0));
    }
}
