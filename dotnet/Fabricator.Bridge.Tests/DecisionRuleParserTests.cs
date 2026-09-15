// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Fabricator.Functions;
using Xunit;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// <see cref="DecisionRuleParser"/> — one decision-table cell to SQL.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ THESE ARE THE PORTED ENGINE'S OWN DOCUMENTED EXAMPLES, DELIBERATELY. The parser came from
/// <c>decision-rules-py</c>, whose documentation lists the cell forms it supports with their expected SQL;
/// every one of those is an assertion here, so the port is pinned against the thing it was ported FROM
/// rather than against whatever this implementation happens to do.
/// </para>
/// <para>
/// ⚠ THIS IS THE REASON THE PARSER IS C# RATHER THAN SQL OR LIQUID. It runs OFFLINE — no DuckDB, no
/// server, no rules table — so the whole grammar is gated on every push in tier 0, in about two minutes.
/// A parser living in a Fluid template or a recursive CTE could only be tested by rendering and executing
/// something, which is a far coarser instrument for a function whose output is a STRING.
/// </para>
/// </remarks>
public class DecisionRuleParserTests
{
    // ── wildcards ────────────────────────────────────────────────────────────────────────────────────
    // ⚠ A BLANK CELL MEANS "ANY VALUE". Rendering anything but TRUE here would silently NARROW every rule
    // that leaves a column open, which is most of them — and the result would still be valid SQL.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void Wildcard_matches_anything(string? cell)
        => Assert.Equal("TRUE", DecisionRuleParser.ParseCondition(cell, "age"));

    // ── the documented input-condition forms ─────────────────────────────────────────────────────────
    [Theory]
    // bare value, auto-quoted
    [InlineData("foo", "region", "region = 'foo'")]
    [InlineData("42", "age", "age = 42")]
    // comparison
    [InlineData("> 18", "age", "age > 18")]
    [InlineData(">= 18", "age", "age >= 18")]
    [InlineData("<> 'x'", "region", "region <> 'x'")]
    // comma list
    [InlineData("EU, US", "region", "region IN ('EU', 'US')")]
    // BETWEEN, including expression bounds
    [InlineData("between 21 and 65", "age", "age BETWEEN 21 AND 65")]
    [InlineData("between min(1) and max(100)", "age", "age BETWEEN min(1) AND max(100)")]
    // FEEL ranges — every bracket combination
    [InlineData("[21 .. 65]", "age", "age >= 21 AND age <= 65")]
    [InlineData("(0 .. 100)", "age", "age > 0 AND age < 100")]
    [InlineData("[0 .. 100)", "age", "age >= 0 AND age < 100")]
    [InlineData("(0 .. 100]", "age", "age > 0 AND age <= 100")]
    // range bounds that are expressions / column refs
    [InlineData("[0 .. @age]", "amount", "amount >= 0 AND amount <= age")]
    [InlineData("[ceil(@age/10)*10 .. 100]", "amount", "amount >= ceil(age/10)*10 AND amount <= 100")]
    // cross-column comparison
    [InlineData("> @age", "amount", "amount > age")]
    // negation
    [InlineData("not RU", "region", "NOT (region = 'RU')")]
    // explicit IN with each bracket style
    [InlineData("in (1,2,3,4)", "age", "age IN (1, 2, 3, 4)")]
    // full condition, passed through with @refs resolved
    [InlineData("@age*2 > min(1)+1", "age", "age*2 > min(1)+1")]
    // an already-quoted string keeps its commas — it is ONE value, not a list
    [InlineData("'a,b,c'", "region", "region = 'a,b,c'")]
    public void Documented_condition_forms(string cell, string column, string expected)
        => Assert.Equal(expected, DecisionRuleParser.ParseCondition(cell, column));

    // ── the `?` placeholder, whose two meanings are positional ───────────────────────────────────────
    // ⚠⚠ `?` AT THE START DELEGATES, `?` ELSEWHERE SUBSTITUTES, and the difference is observable: after
    // delegation the remainder gets VALUE treatment, so `a` is quoted; after substitution the cell is raw
    // SQL the author is writing. Getting these the same way round would silently change quoting.
    [Theory]
    [InlineData("? >=a", "col", "col >= 'a'")]
    // ⚠ NO SPACE, and that is FAITHFUL rather than sloppy: substitution is a literal replace, so the
    // cell's own spacing survives. The ported engine's doc writes this example as `5 >= col`, which is its
    // MEANING — copying that as the expected STRING is what made this the one row that failed first.
    [InlineData("5 >=?", "col", "5 >=col")]
    [InlineData("contains(?, 'foo')", "col", "contains(col, 'foo')")]
    public void Placeholder_delegates_at_start_and_substitutes_elsewhere(string cell, string column, string expected)
        => Assert.Equal(expected, DecisionRuleParser.ParseCondition(cell, column));

    // ⚠⚠ THE ORDERING ROW. A FEEL range is matched BEFORE the comma check, so a range whose bound is a
    // function call is not split on the argument comma into a three-item IN list. Reverse the two and this
    // is the case that breaks — and it breaks into VALID SQL, which is why it is pinned.
    [Fact]
    public void Feel_range_wins_over_comma_splitting()
        => Assert.Equal(
            "col >= 0 AND col <= func(1,2,col)",
            DecisionRuleParser.ParseCondition("[0 .. func(1,2,?)]", "col"));

    // ⚠ A comma inside a function call is not a list separator either.
    [Fact]
    public void Function_argument_comma_is_not_a_list()
        => Assert.Equal("coalesce(col, 'x') = 'y'", DecisionRuleParser.ParseCondition("coalesce(?, 'x') = 'y'", "col"));

    // ── output / value expressions ───────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("approve", "'approve'")]
    [InlineData("'approve'", "'approve'")]
    [InlineData("42", "42")]
    [InlineData("0.5", "0.5")]
    [InlineData("-3", "-3")]
    [InlineData("NULL", "NULL")]
    [InlineData("true", "TRUE")]
    [InlineData("@amount * 0.01", "amount * 0.01")]
    [InlineData("@amount / @age", "amount / age")]
    [InlineData("@col", "col")]
    // a type cast is an EXPRESSION, not a string
    [InlineData("@age::varchar", "age::varchar")]
    [InlineData("'20010101'::date", "'20010101'::date")]
    // parenthesised content is parsed INSIDE and re-wrapped
    [InlineData("(func(@x))", "(func(x))")]
    public void Documented_value_forms(string cell, string expected)
        => Assert.Equal(expected, DecisionRuleParser.ParseValue(cell));

    // ⚠ The ONE place a cell becomes a literal — and the quote doubling is the complete escape.
    [Fact]
    public void Bare_string_is_quoted_and_escaped()
        => Assert.Equal("'O''Brien'", DecisionRuleParser.ParseValue("O'Brien"));

    // ── aggregate detection ──────────────────────────────────────────────────────────────────────────
    // ⚠⚠ IT SCANS EVERY CALL, NOT THE OUTERMOST. `EXP(SUM(LOG(?)))` IS an aggregate and its outer call is
    // not — a build testing only the outer name reports false and the engine silently stays in row mode.
    [Theory]
    [InlineData("max(?)", true)]
    [InlineData("arg_max(?,risk)", true)]
    [InlineData("EXP(SUM(LOG(?)))", true)]
    [InlineData("max(round(?,2))", true)]
    [InlineData("round(?,2)", false)]
    [InlineData("?", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Aggregate_detection_scans_nested_calls(string? expr, bool expected)
        => Assert.Equal(expected, DecisionRuleParser.IsAggregate(expr));

    // ── dialect ──────────────────────────────────────────────────────────────────────────────────────
    // ⚠⚠ THE ONLY CONSTRUCT THAT DIFFERS TODAY. A bracketed IN is a DuckDB LIST literal; T-SQL has no such
    // thing, so it is rewritten to parentheses rather than rendered into a syntax error. Everything else
    // the parser emits is near-ANSI, and the CELL carries the target's own function vocabulary untouched.
    [Fact]
    public void Bracketed_in_list_stays_a_list_on_duckdb()
        => Assert.Equal("age IN [1, 2, 3, 4]",
            DecisionRuleParser.ParseCondition("in [1,2,3,4]", "age", DmnDialect.DuckDb));

    [Fact]
    public void Bracketed_in_list_becomes_parentheses_on_tsql()
        => Assert.Equal("age IN (1, 2, 3, 4)",
            DecisionRuleParser.ParseCondition("in [1,2,3,4]", "age", DmnDialect.TSql));

    // ⚠ A cell's own FUNCTION vocabulary is passed through for BOTH dialects — that is the feature, and it
    // is why the dialect parameter does so little. A T-SQL rule may call CHARINDEX and a DuckDB one may
    // call contains(); neither is translated, and neither should be.
    [Fact]
    public void Cell_function_vocabulary_is_never_translated()
        => Assert.Equal("CHARINDEX('x', col) > 0",
            DecisionRuleParser.ParseCondition("CHARINDEX('x', ?) > 0", "col", DmnDialect.TSql));

    // ── the reference prefix ─────────────────────────────────────────────────────────────────────────
    // ⚠⚠ IT EXISTS FOR ONE MEASURED DuckDB RULE: a macro PARAMETER shadows a column of the same name
    // anywhere in the body, so a generated macro whose parameter is `age` and whose preprocessing CTE also
    // produces `age` reads the RAW ARGUMENT everywhere downstream — the preprocessed value is computed and
    // silently never used. MEASURED both ways on a two-line macro: bare `x` yields the argument, `p.x`
    // yields the CTE column. The renderer therefore QUALIFIES, and @refs have to be qualified with it.
    [Theory]
    [InlineData("> @age", "preprocessed.amount", "preprocessed.amount > preprocessed.age")]
    [InlineData("@age*2 > min(1)+1", "preprocessed.age", "preprocessed.age*2 > min(1)+1")]
    [InlineData("[0 .. @age]", "preprocessed.amount",
                "preprocessed.amount >= 0 AND preprocessed.amount <= preprocessed.age")]
    public void Ref_prefix_qualifies_every_column_reference(string cell, string column, string expected)
        => Assert.Equal(expected, DecisionRuleParser.ParseCondition(cell, column, DmnDialect.DuckDb, "preprocessed."));

    [Theory]
    [InlineData("@amount / @age", "preprocessed.amount / preprocessed.age")]
    [InlineData("@col", "preprocessed.col")]
    [InlineData("@age::varchar", "preprocessed.age::varchar")]
    public void Ref_prefix_reaches_value_expressions_too(string cell, string expected)
        => Assert.Equal(expected, DecisionRuleParser.ParseValue(cell, DmnDialect.DuckDb, "preprocessed."));

    // ⚠⚠ THE PREFIX MUST NOT CHANGE WHICH BRANCH A CELL TAKES, only the text emitted. The two detectors
    // strip @refs before matching, so they are called with an EMPTY prefix deliberately — otherwise a
    // prefix containing a dot (or worse, an operator) could reclassify a cell. This row is the control: a
    // bare string is still QUOTED, not mistaken for an expression, whatever the prefix.
    [Fact]
    public void Ref_prefix_does_not_reclassify_a_cell()
    {
        Assert.Equal("'approve'", DecisionRuleParser.ParseValue("approve", DmnDialect.DuckDb, "preprocessed."));
        Assert.Equal("preprocessed.region = 'RU'",
            DecisionRuleParser.ParseCondition("RU", "preprocessed.region", DmnDialect.DuckDb, "preprocessed."));
    }

    // ⚠ A `$` in the prefix must not be read as a regex substitution token — the prefix is caller-supplied
    // text, and the replacement goes through a MatchEvaluator rather than a "$1" replacement string.
    [Fact]
    public void A_dollar_in_the_prefix_is_literal()
        => Assert.Equal("$p$age", DecisionRuleParser.ParseValue("@age", DmnDialect.DuckDb, "$p$"));

    // An absent prefix is the ordinary bare resolution — the default every other test here relies on.
    [Fact]
    public void No_prefix_resolves_bare()
        => Assert.Equal("amount / age", DecisionRuleParser.ParseValue("@amount / @age"));

    // ── the dialect name ─────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(null, DmnDialect.DuckDb)]
    [InlineData("", DmnDialect.DuckDb)]
    [InlineData("DuckDB", DmnDialect.DuckDb)]
    [InlineData("tsql", DmnDialect.TSql)]
    [InlineData("MSSQL", DmnDialect.TSql)]
    [InlineData("sqlserver", DmnDialect.TSql)]
    public void Dialect_names_are_case_insensitive(string? name, DmnDialect expected)
        => Assert.Equal(expected, DecisionRuleParser.ParseDialect(name));

    // ⚠ An unknown dialect is refused rather than defaulting to DuckDB: it was written to SELECT a target,
    // and a silent fallback would render the wrong dialect's SQL and say nothing.
    [Fact]
    public void An_unknown_dialect_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DecisionRuleParser.ParseDialect("oracle"));
        Assert.Contains("duckdb, tsql", ex.Message, StringComparison.Ordinal);
    }

    // ── the guard ────────────────────────────────────────────────────────────────────────────────────
    // ⚠ A malformed cell must fail with a SENTENCE. A StackOverflowException cannot be caught, so it would
    // take the whole process down rather than failing one statement.
    [Fact]
    public void Pathological_nesting_fails_with_a_message()
    {
        var cell = new string('(', 200) + "x" + new string(')', 200);
        var ex = Assert.Throws<InvalidOperationException>(() => DecisionRuleParser.ParseValue(cell));
        Assert.Contains("nests deeper", ex.Message, StringComparison.Ordinal);
    }
}
