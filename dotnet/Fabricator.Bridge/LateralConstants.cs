// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// The BIND-TIME CONSTANT channel for lateral functions — the host half of <see cref="Params.Constant"/>,
/// and it is STATELESS. DuckDB's binder rewrites EVERY argument of a table-in-out call into the synthesized
/// input relation (bind_table_function.cpp), so a lateral function's bind receives no argument VALUES — but
/// the C++ bind recovers a constant slot's value anyway: in the literal (all-constant) shape from the folded
/// parameter (<c>input.inputs</c>), and in the correlated shape from the synthesized column's rendered NAME,
/// which for an unaliased expression is the expression's own re-parseable rendering
/// (<c>TryFoldRenderedConstant</c> in fabricator_lateral.cpp: parse → ConstantBinder — refuses columns —
/// → fold — refuses volatiles — → accept only when the folded type equals the slot's bound type). The value
/// then rides the args batch into <see cref="ILateralFunction.Bind"/> under the parameter's declared name.
/// </summary>
/// <remarks>
/// <para><b>MEASURED to carry everything probed, bare, in BOTH call shapes</b>: strings (escaping intact),
/// numbers, casts (<c>5::SMALLINT</c> → <c>CAST(5 AS SMALLINT)</c>), foldable expressions
/// (<c>upper('ab') || ',cd'</c>), LISTs, STRUCTs (<c>main.struct_pack(a := 5)</c>), <c>getvariable(…)</c>,
/// and prepared-statement parameters (DuckDB re-binds every EXECUTE, and the re-parsed <c>$1</c> re-binds
/// against the executing binder's parameter values).</para>
/// <para><b>A `const_arg(…)` capture wrapper existed for one day and was REMOVED once the text channel was
/// measured to need no fallback</b> — with it went the value registry and its refcounted lifecycle, which
/// were the only stateful parts of the feature (docs/lateral_unnest_analysis.md §9.1). If a value ever
/// surfaces whose rendering does not round-trip, the failure is this class's LOUD bind-time refusal, and
/// the wrapper is cheap to re-introduce; do not re-add it speculatively.</para>
/// </remarks>
internal static class LateralConstants
{
    /// <summary>
    /// Validates the <see cref="Params.Constant"/> arguments of one lateral call: each declared constant
    /// parameter must have arrived with a bind-time value (the C++ bind leaves a NULL exactly when it could
    /// not resolve one — a column, a volatile, a non-foldable expression, or an explicit NULL). Throws the
    /// one refusal a caller can act on; passes the batch through untouched otherwise.
    /// </summary>
    internal static RecordBatch? Validate(string func, Schema parameters, RecordBatch? args)
    {
        var constants = new HashSet<string>(
            parameters.FieldsList.Where(f => Params.StyleOf(f) == ParamStyle.Constant).Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);
        if (constants.Count == 0)
        {
            return args;
        }
        if (args is null)
        {
            // The C++ bind marshals a column per declared constant, so this is a contract break, not user error.
            throw new InvalidOperationException(
                $"fabricator: lateral function '{func}' declares constant parameters but the bind received no "
                + "argument batch");
        }
        for (int i = 0; i < args.ColumnCount; i++)
        {
            var field = args.Schema.FieldsList[i];
            if (!constants.Contains(field.Name))
            {
                continue;
            }
            var col = args.Column(i);
            if (col.Length < 1 || col.IsNull(0))
            {
                throw new NotSupportedException(
                    $"fabricator: parameter '{field.Name}' of '{func}' is a bind-time CONSTANT, and this "
                    + "argument has no bind-time value. Pass a non-NULL constant expression that folds at "
                    + "bind — a literal, a cast, getvariable(...), a prepared parameter — not a column or a "
                    + "volatile expression.");
            }
        }
        return args;
    }
}
