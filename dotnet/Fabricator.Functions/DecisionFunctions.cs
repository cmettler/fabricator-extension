// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.Functions;

/// <summary>
/// The decision-table cell parser and source classifier, exposed as connection-free GLOBAL scalars so the
/// introspection can apply them ACROSS EVERY CELL AT ONCE instead of row by row in a template.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ THIS IS WHAT MAKES THE THREE-LANGUAGE SPLIT WORK.</b> The recursion lives in C#
/// (<see cref="DecisionRuleParser"/>), the SHAPE of the generated statement lives in a Liquid template — and
/// everything BETWEEN them is set-based work over a relation: read the metadata rows, pick the input and
/// output columns, parse every cell, aggregate the per-rule fragments into one string. That middle part is
/// SQL's job, and it can only be SQL's job if the parser is callable FROM SQL. These four functions are that
/// seam and they cost no new mechanism: <c>IProvider.GlobalScalarFunctions</c> already existed.
/// </para>
/// <para>
/// ⚠ All four are CONSISTENT rather than the default VOLATILE — they are pure string functions, so DuckDB
/// may fold one over constant arguments at plan time. That is exactly what should happen to
/// <c>dmn_relation('decision_rules', 'auto')</c>, which is evaluated once per render and never per row.
/// </para>
/// <para>
/// ⚠ They are also worth having on their own: <c>SELECT dmn_condition(cell, 'age', 'duckdb')</c> answers
/// "what does this cell become?" for a rules table nobody can get right by reading, which is the question a
/// decision table's author asks most often.
/// </para>
/// </remarks>
internal static class DecisionFunctions
{
    internal static IEnumerable<IScalarFunction> All =>
        new IScalarFunction[]
        {
            new DmnConditionFunction(),
            new DmnValueFunction(),
            new DmnIsAggregateFunction(),
            new DmnRelationFunction(),
        };

    internal static Field Str(string name) => new(name, StringType.Default, nullable: true);
}

/// <summary>
/// <c>dmn_condition(cell, column, dialect, ref_prefix)</c> — one INPUT cell to a SQL boolean over
/// <c>column</c>, with every <c>@ref</c> resolved to <c>ref_prefix</c> + its name.
/// </summary>
/// <remarks>
/// ⚠ A NULL <c>cell</c> yields <c>TRUE</c> rather than NULL, and that is the semantic rather than sloppiness:
/// a blank cell in a decision table MEANS "any value". A NULL <c>column</c> does yield NULL — there is
/// nothing to build a condition about. The asymmetry is deliberate and pinned.
/// </remarks>
internal sealed class DmnConditionFunction : IScalarFunction
{
    public string Name => "dmn_condition";

    public Schema Parameters => new(
        new[]
        {
            DecisionFunctions.Str("cell"), DecisionFunctions.Str("column"),
            DecisionFunctions.Str("dialect"), DecisionFunctions.Str("ref_prefix"),
        },
        metadata: null);

    public Field Result => DecisionFunctions.Str("sql");

    public bool IsVolatile => false;

    public IArrowArray Invoke(RecordBatch args)
    {
        var cells = (StringArray)args.Column(0);
        var columns = (StringArray)args.Column(1);
        var dialects = (StringArray)args.Column(2);
        var prefixes = (StringArray)args.Column(3);
        var b = new StringArray.Builder().Reserve(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            if (columns.IsNull(i))
            {
                b.AppendNull();
                continue;
            }
            var dialect = DecisionRuleParser.ParseDialect(dialects.IsNull(i) ? null : dialects.GetString(i));
            b.Append(DecisionRuleParser.ParseCondition(
                cells.IsNull(i) ? null : cells.GetString(i), columns.GetString(i), dialect,
                prefixes.IsNull(i) ? null : prefixes.GetString(i)));
        }
        return b.Build();
    }
}

/// <summary>
/// <c>dmn_value(cell, dialect, ref_prefix)</c> — one OUTPUT cell to a SQL value expression, with every
/// <c>@ref</c> resolved to <c>ref_prefix</c> + its name.
/// </summary>
/// <remarks>
/// ⚠ A NULL cell PROPAGATES as NULL here, where <see cref="DmnConditionFunction"/>'s yields <c>TRUE</c>. An
/// absent output has no value, so ordinary SQL null-propagation is the honest answer; what an EMPTY output
/// cell should mean is a question about the DATA, and the introspection answers it (blank ⇒ <c>NULL</c>)
/// rather than this function guessing.
/// </remarks>
internal sealed class DmnValueFunction : IScalarFunction
{
    public string Name => "dmn_value";

    public Schema Parameters => new(
        new[]
        {
            DecisionFunctions.Str("cell"), DecisionFunctions.Str("dialect"),
            DecisionFunctions.Str("ref_prefix"),
        },
        metadata: null);

    public Field Result => DecisionFunctions.Str("sql");

    public bool IsVolatile => false;

    public IArrowArray Invoke(RecordBatch args)
    {
        var cells = (StringArray)args.Column(0);
        var dialects = (StringArray)args.Column(1);
        var prefixes = (StringArray)args.Column(2);
        var b = new StringArray.Builder().Reserve(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            if (cells.IsNull(i))
            {
                b.AppendNull();
                continue;
            }
            var dialect = DecisionRuleParser.ParseDialect(dialects.IsNull(i) ? null : dialects.GetString(i));
            b.Append(DecisionRuleParser.ParseValue(cells.GetString(i), dialect,
                prefixes.IsNull(i) ? null : prefixes.GetString(i)));
        }
        return b.Build();
    }
}

/// <summary>
/// <c>dmn_is_aggregate(expr)</c> — TRUE when an output preprocessing expression contains an aggregate, which
/// is what switches the whole table into aggregate mode.
/// </summary>
/// <remarks>
/// ⚠ It scans EVERY call, not the outermost one: <c>EXP(SUM(LOG(?)))</c> IS an aggregate and its outer call
/// is not. NULL and blank are FALSE rather than NULL — "this column declares no preprocessing" is a definite
/// answer, and the caller aggregates it with <c>bool_or</c>, where a NULL would be noise.
/// </remarks>
internal sealed class DmnIsAggregateFunction : IScalarFunction
{
    public string Name => "dmn_is_aggregate";

    public Schema Parameters => new(new[] { DecisionFunctions.Str("expr") }, metadata: null);

    public Field Result => new("is_aggregate", BooleanType.Default, nullable: true);

    public bool IsVolatile => false;

    public IArrowArray Invoke(RecordBatch args)
    {
        var exprs = (StringArray)args.Column(0);
        var b = new BooleanArray.Builder().Reserve(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            b.Append(DecisionRuleParser.IsAggregate(exprs.IsNull(i) ? null : exprs.GetString(i)));
        }
        return b.Build();
    }
}

/// <summary>
/// <c>dmn_relation(rules, source)</c> — the FROM-clause text for a rules argument that may be a relation
/// NAME, a FILE, JSON TEXT, or a verbatim relation EXPRESSION.
/// </summary>
/// <remarks>
/// ⚠ A NULL or empty <c>rules</c> is an ERROR, not NULL: every caller is about to splice the answer into a
/// statement, so "I could not tell" must stop the render rather than produce a statement with a hole in it.
/// </remarks>
internal sealed class DmnRelationFunction : IScalarFunction
{
    public string Name => "dmn_relation";

    public Schema Parameters => new(
        new[] { DecisionFunctions.Str("rules"), DecisionFunctions.Str("source") }, metadata: null);

    public Field Result => DecisionFunctions.Str("relation");

    public bool IsVolatile => false;

    public IArrowArray Invoke(RecordBatch args)
    {
        var rules = (StringArray)args.Column(0);
        var sources = (StringArray)args.Column(1);
        var b = new StringArray.Builder().Reserve(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            b.Append(DecisionRuleSource.Relation(
                rules.IsNull(i) ? null : rules.GetString(i),
                sources.IsNull(i) ? null : sources.GetString(i)));
        }
        return b.Build();
    }
}
