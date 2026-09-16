// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fabricator.Functions;

/// <summary>The SQL dialect a decision table renders for.</summary>
/// <remarks>
/// ⚠ IT IS A PARSER PARAMETER FROM THE START, NOT A RETROFIT — but it does far less than it looks like it
/// should, and knowing why is what keeps this small. A rule CELL carries the TARGET'S OWN FUNCTION
/// VOCABULARY (<c>contains(?, 'x')</c>, <c>ceil(@age/10)</c>, <c>CHARINDEX(...)</c>) and is passed THROUGH
/// untouched — that is the feature, not an oversight, and it is what makes a decision table fast and
/// flexible. What this parser emits around those cells is STRUCTURE — comparison operators, IN lists,
/// BETWEEN, range expansions — which is near-ANSI. So exactly one construct actually differs today.
/// </remarks>
public enum DmnDialect
{
    /// <summary>DuckDB. <c>IN [a,b]</c> keeps its brackets (a DuckDB LIST).</summary>
    DuckDb,

    /// <summary>SQL Server / T-SQL. A bracketed IN list is rewritten to parentheses — T-SQL has no list literal.</summary>
    TSql,
}

/// <summary>
/// Turns ONE decision-table cell into SQL: an input condition, or an output value expression.
/// </summary>
/// <remarks>
/// <para>
/// A port of the <c>decision-rules-py</c> engine's <c>parse_rule</c> / <c>_cast_value</c> pair, kept
/// deliberately faithful — the regexes below are the originals, and the recursion points are the same three.
/// </para>
/// <para>
/// <b>⚠⚠ IT IS IN C# BECAUSE IT RECURSES, AND THAT IS THE WHOLE ARCHITECTURAL REASON.</b> Everything else
/// about a decision table is set-based and belongs in SQL — reading the metadata rows, pivoting, hashing,
/// applying this parser across every cell at once. But <c>not X</c> re-parses its operand, a FEEL range
/// <c>[a .. b]</c> re-parses EACH BOUND as its own comparison, and a leading <c>?</c> delegates the rest of
/// the cell back to the top. Expressing that as SQL would need a recursive CTE over strings; expressing it
/// in Liquid would need a recursive-descent parser in a template language. Both are worse than 200 lines of
/// C# that a tier-0 test can pin offline.
/// </para>
/// <para>
/// <b>⚠⚠ A DECISION TABLE IS CODE, NOT DATA — say it out loud rather than letting the quoting imply
/// otherwise.</b> An expression cell is resolved and passed THROUGH to the generated SQL; only the FALLBACK
/// (a bare string that is not an expression) is quoted as a literal. That is the design: a rule may call any
/// function the target engine has. It also means whoever authors a decision table can write arbitrary SQL,
/// exactly as whoever authors a Fluid template can. Treat the rules table as a trusted authoring surface,
/// never as user input.
/// </para>
/// </remarks>
public static class DecisionRuleParser
{
    /// <summary>
    /// Function names that switch the engine into AGGREGATE mode when one appears in an output column's
    /// preprocessing expression.
    /// </summary>
    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "min", "max", "sum", "product", "string_agg", "array_agg",
        "list", "json_group_array", "argmax", "argmin", "arg_max", "arg_min", "count",
    };

    // ── the originals, transcribed ────────────────────────────────────────────────────────────────────
    private static readonly Regex PlaceholderAtStart = new(@"^\?\s+(.+)$", RegexOptions.Singleline);
    private static readonly Regex NotPrefix = new(@"^not\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex InList = new(@"^in\s*([\[\(])(.+)([\]\)])$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex FeelRange = new(@"^([\[\(])\s*(.+?)\s*\.\.\s*(.+?)\s*([\]\)])$", RegexOptions.Singleline);
    private static readonly Regex BetweenAnd = new(@"^between\s+(.+)\s+and\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex LeadingOperator = new(@"^(!=|<>|>=|<=|>|<|=)\s*(.+)$", RegexOptions.Singleline);
    /// <summary>
    /// An operator KEYWORD at the start of a cell, where the column is implicit on the LEFT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠⚠ THE IMPLICIT LEFT-HAND COLUMN IS THE WHOLE POINT OF A DECISION-TABLE CELL</b> — a cell is a
    /// predicate FRAGMENT about its own column, so <c>&gt; 18</c> means <c>age &gt; 18</c> and
    /// <c>IS NULL</c> must mean <c>age IS NULL</c>. This is the same family as
    /// <see cref="LeadingOperator"/>, <see cref="BetweenAnd"/> and <see cref="InList"/>, which have
    /// prepended the column all along; these keywords were simply missing from it, so a bare
    /// <c>IS NULL</c> cell rendered the column-less fragment <c>IS NULL</c>.
    /// </para>
    /// <para>
    /// ⚠ The right-hand side goes through <see cref="ParseValue"/>, exactly as
    /// <see cref="LeadingOperator"/>'s does — so <c>LIKE E5</c> quotes to <c>region LIKE 'E5'</c> and
    /// <c>IS DISTINCT FROM @other</c> resolves the ref. A pattern containing <c>%</c> or <c>*</c> must be
    /// QUOTED by the author, because those are value operators and an unquoted <c>E%</c> reads as an
    /// expression — the same rule every other value position here follows.
    /// </para>
    /// <para>
    /// ⚠ <c>NOT</c> needs no entry: <see cref="NotPrefix"/> runs first and wraps, so <c>NOT LIKE 'x'</c>
    /// becomes <c>NOT (region LIKE 'x')</c> — equivalent for NULL and non-NULL alike. <c>IS NOT NULL</c>
    /// and <c>IS NOT DISTINCT FROM</c> DO need theirs, since they do not start with <c>NOT</c>.
    /// </para>
    /// <para>
    /// ⚠ <c>IN</c> is deliberately absent — <see cref="InList"/> already prepends the column for the
    /// bracketed forms and the comma check covers the bare list.
    /// </para>
    /// <para>
    /// ⚠ The trailing <c>\b</c> is load-bearing: without it <c>REGEXP_MATCHES(?, 'x')</c> and
    /// <c>LIKELY(?)</c> would be read as operators and mangled into <c>col REGEXP _MATCHES(…)</c>.
    /// </para>
    /// </remarks>
    private static readonly Regex LeadingConditionKeyword = new(
        @"^(LIKE|ILIKE|GLOB|REGEXP|SIMILAR\s+TO|IS\s+(?:NOT\s+)?NULL|IS\s+(?:NOT\s+)?DISTINCT\s+FROM)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex FunctionCallAtStart = new(@"^[a-zA-Z_]\w*\s*\(");
    private static readonly Regex ColumnRef = new(@"@(\w+)");
    private static readonly Regex BareColumnRef = new(@"^@\w+$");
    private static readonly Regex NumericLiteral = new(@"^-?(\d+\.?\d*|\.\d+)$");
    private static readonly Regex QuotedString = new(@"'[^']*'");
    private static readonly Regex AnyFunctionCall = new(@"\b[a-zA-Z_]\w*\s*\(");
    private static readonly Regex ValueOperators = new(@"[+*/%]|\|\||::");
    private static readonly Regex ComparisonOperators = new(@">=|<=|<>|!=|(?<![<>!])=|(?<!=)[><](?!=)");
    /// <summary>
    /// The operator KEYWORDS that make a cell a complete condition rather than a value to compare against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠⚠ A MISSING KEYWORD IS A SILENT WRONG ANSWER AND A SPURIOUS ONE IS A LOUD ERROR, WHICH IS WHY
    /// THIS LIST IS DELIBERATELY GENEROUS.</b> Undetected, a cell like <c>@region SIMILAR TO 'E.*'</c> falls
    /// to the string fallback and renders <c>region = '@region SIMILAR TO ''E.*'''</c> — valid SQL that is
    /// ALWAYS FALSE, with nothing failing anywhere. Over-detected, a bare data value containing one of these
    /// words passes through as raw SQL and the target engine refuses it at CREATE time. MEASURED both ways:
    /// <c>MADE IN USA</c> already passes through today (the <c>IN</c> entry), and the escape is to QUOTE the
    /// cell — <c>'MADE IN USA'</c> renders <c>origin = 'MADE IN USA'</c>. ⚠ The word boundaries do their job:
    /// <c>LIKED</c> is still quoted as a value.
    /// </para>
    /// <para>
    /// <b>⚠ IT IS A UNION ACROSS DIALECTS, AND THAT IS CORRECT BECAUSE IT DETECTS RATHER THAN TRANSLATES.</b>
    /// A keyword the target lacks simply means the cell passes through and the target complains about the
    /// author's own SQL. MEASURED on the shipped builds: <c>SIMILAR TO</c>, <c>GLOB</c> and <c>ILIKE</c> are
    /// DuckDB's and not T-SQL's; <c>REGEXP</c> is NEITHER — it is an infix operator in MySQL/SQLite and came
    /// in with the ported engine (SQL Server 2025 has no <c>REGEXP</c> operator and no <c>REGEXP_LIKE</c>
    /// function on this rig, and DuckDB rejects the infix form outright). It stays anyway: removing it would
    /// turn a cell that names it from a loud error back into a silently-false comparison.
    /// </para>
    /// <para>
    /// ⚠ <c>ILIKE</c> is listed separately rather than left to <c>LIKE</c>: there is no word boundary between
    /// <c>I</c> and <c>LIKE</c>, so <c>\bLIKE\b</c> does not match it. The negated forms need no entries —
    /// <c>NOT LIKE</c> / <c>NOT SIMILAR TO</c> / <c>NOT GLOB</c> all contain their base keyword.
    /// </para>
    /// <para>
    /// ⚠ The <c>IS …</c> forms are matched as PHRASES, not as a bare <c>IS</c>, which keeps a data value
    /// containing the word from being mistaken for an operator. <c>IS NULL</c> is the most natural cell a
    /// decision table has (does this field have a value at all?) and was in this hole until 2026-09-16.
    /// </para>
    /// </remarks>
    private static readonly Regex ConditionKeywords = new(
        @"\b(?:BETWEEN|LIKE|ILIKE|REGEXP|GLOB|IN)\b"
        + @"|\bSIMILAR\s+TO\b"
        + @"|\bIS\s+(?:NOT\s+)?(?:NULL|DISTINCT\s+FROM)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex NamedFunctionCall = new(@"\b([a-zA-Z_]\w*)\s*\(");

    /// <summary>⚠ Guards the three recursion points against a pathological cell. A rule is authored, so a
    /// real one nests two or three deep; this exists so a malformed cell fails with a sentence instead of a
    /// StackOverflowException, which no catch can turn back into an error message.</summary>
    private const int MaxDepth = 32;

    /// <summary>Maps a SQL-level dialect name onto <see cref="DmnDialect"/>, refusing an unknown one BY NAME.</summary>
    /// <remarks>
    /// ⚠ An EMPTY or absent name means DuckDB — the surface's own default — rather than an error, so a
    /// caller who never mentions a dialect never has to. What is refused is a name that was MEANT to select
    /// something: a silent fallback there would render the wrong dialect's SQL and say nothing.
    /// </remarks>
    public static DmnDialect ParseDialect(string? dialect)
    {
        var d = (dialect ?? string.Empty).Trim();
        if (d.Length == 0 || d.Equals("duckdb", StringComparison.OrdinalIgnoreCase)) return DmnDialect.DuckDb;
        if (d.Equals("tsql", StringComparison.OrdinalIgnoreCase)
            || d.Equals("mssql", StringComparison.OrdinalIgnoreCase)
            || d.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)) return DmnDialect.TSql;

        throw new InvalidOperationException(
            $"decision rule: dialect '{d}' is not one of duckdb, tsql.");
    }

    /// <summary>
    /// Parses an INPUT CONDITION cell into a SQL boolean expression over <paramref name="column"/>.
    /// </summary>
    /// <remarks>
    /// Empty, null, whitespace or <c>*</c> is a WILDCARD and yields <c>TRUE</c> — it must, because a
    /// decision table's blank cell means "any value", and rendering anything else would silently narrow
    /// every rule that leaves a column open.
    /// <para>⚠ ORDER IS LOAD-BEARING IN ONE PLACE: the FEEL range is matched BEFORE the comma check, or
    /// <c>[0 .. func(1,2,?)]</c> is split on the argument comma and parsed as a three-item IN list.</para>
    /// <para>
    /// <b>THE DISPATCH, IN ORDER.</b> Each branch below carries CONTEXT / PARSES / SAMPLE / TEST; this is
    /// the index. Every sample is on column <c>age</c> or <c>region</c>.
    /// <code>
    ///  #  branch              a cell that reaches it        renders
    ///  1  wildcard            (blank), *                    TRUE
    ///  2  placeholder         ? >=a  /  5 >=?               age >= 'a'  /  5 >=age
    ///  3  not                 not RU                        NOT (region = 'RU')
    ///  4  IN list             in (1,2)  /  in [1,2]         age IN (1, 2)  /  age IN [1, 2]
    ///  5  FEEL range          [21 .. 65]                    age >= 21 AND age &lt;= 65
    ///  6  comma list          EU, US                        region IN ('EU', 'US')
    ///  7  BETWEEN             between 21 and 65             age BETWEEN 21 AND 65
    ///  8  leading operator    &gt; 18                           age &gt; 18
    ///  9  leading keyword     IS NULL  /  LIKE 'E%'         age IS NULL  /  age LIKE 'E%'
    /// 10  function call       contains(?, 'x')              contains(region, 'x')
    /// 11  full condition      @age*2 &gt; min(1)+1             age*2 &gt; min(1)+1
    /// 12  fallback (value)    EU                            region = 'EU'
    /// </code>
    /// </para>
    /// <para>
    /// ⚠ <b>THE COLUMN IS IMPLICIT ON THE LEFT</b> for branches 4, 5, 6, 7, 8, 9 and 12 — that is what a
    /// decision-table cell IS. Branches 10 and 11 are the ones where the author wrote the left-hand side
    /// out, so nothing is prepended.
    /// </para>
    /// </remarks>
    public static string ParseCondition(string? cell, string column, DmnDialect dialect = DmnDialect.DuckDb,
                                        string? refPrefix = null)
        => ParseCondition(cell, column, dialect, refPrefix ?? string.Empty, 0);

    private static string ParseCondition(string? cell, string column, DmnDialect dialect, string refPrefix, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"decision rule: expression nests deeper than {MaxDepth} levels for column '{column}' — "
                + "this is almost certainly a malformed cell rather than a real rule.");
        }
        // ─── 1. WILDCARD ──────────────────────────────────────────────────────────────────────────
        // CONTEXT : any input cell. A decision table leaves a column open far more often than not.
        // PARSES  : null, empty, whitespace, `*`
        // SAMPLE  : ``            on `age`  ⇒  TRUE
        // TEST    : Wildcard_matches_anything
        // ⚠ It MUST be TRUE. Anything else silently NARROWS every rule that leaves a column open, and
        //   the renderer additionally OMITS a wildcard WHEN branch entirely (verify_decision_render §13).
        var expr = cell?.Trim();
        if (string.IsNullOrEmpty(expr) || expr == "*")
        {
            return "TRUE";
        }

        // ─── 2. PLACEHOLDER ───────────────────────────────────────────────────────────────────────
        // CONTEXT : any input cell that names its own column explicitly.
        // PARSES  : `?` AT THE START  — delegates the REST, so it gets ordinary VALUE treatment
        //           `?` ANYWHERE ELSE — substitutes the column name and parsing continues (RAW: the
        //                                author is writing SQL from here on)
        // SAMPLE  : `? >=a`             on `col`  ⇒  col >= 'a'
        //           `5 >=?`             on `col`  ⇒  5 >=col     (no space — a literal replace)
        //           `contains(?, 'x')`  on `col`  ⇒  contains(col, 'x')
        // TEST    : Placeholder_delegates_at_start_and_substitutes_elsewhere
        //           A_placeholder_inside_a_literal_is_left_alone
        // ⚠ Delegation is what lets `? IS NULL` work: the remainder is a LEADING KEYWORD (branch 9),
        //   which supplies the column itself. See A_placeholder_before_a_keyword_still_gets_the_column.
        if (expr.Contains('?'))
        {
            // `?` AT THE START delegates the REST to the parser, so the remainder gets ordinary value
            // treatment (`? >=a` → col >= 'a'). `?` ANYWHERE ELSE is a plain substitution of the column
            // name and parsing continues (`5 >=?`, `contains(?, 'x')`), which is RAW — the author is
            // writing SQL at that point.
            var lead = PlaceholderAtStart.Match(expr);
            if (lead.Success)
            {
                return ParseCondition(lead.Groups[1].Value, column, dialect, refPrefix, depth + 1);
            }
            expr = SubstitutePlaceholder(expr, column);
        }

        // ─── 3. NOT ───────────────────────────────────────────────────────────────────────────────
        // CONTEXT : any input cell; the operand is re-parsed as a whole cell (RECURSION POINT).
        // PARSES  : `not <cell>`
        // SAMPLE  : `not RU`        on `region`  ⇒  NOT (region = 'RU')
        //           `not like 'E%'` on `region`  ⇒  NOT (region like 'E%')
        // TEST    : Documented_condition_forms, Not_wraps_a_leading_keyword_condition
        // ⚠ It needs WHITESPACE after `not`, so `NOTEBOOK` is still a value. But `not applicable` IS
        //   read as a negation — quote the cell (`'not applicable'`) to force a literal.
        var not = NotPrefix.Match(expr);
        if (not.Success)
        {
            return $"NOT ({ParseCondition(not.Groups[1].Value, column, dialect, refPrefix, depth + 1)})";
        }

        // ─── 4. EXPLICIT IN LIST ──────────────────────────────────────────────────────────────────
        // CONTEXT : any input cell. The column is implicit on the left.
        // PARSES  : `in (a,b)` and `in [a,b]`
        // SAMPLE  : `in (1,2,3,4)` on `age`  ⇒  age IN (1, 2, 3, 4)
        //           `in [1,2,3,4]` on `age`  ⇒  age IN [1, 2, 3, 4]   (DuckDB LIST)
        //                                    ⇒  age IN (1, 2, 3, 4)   (T-SQL — no list literal)
        // TEST    : Documented_condition_forms, Bracketed_in_list_stays_a_list_on_duckdb,
        //           Bracketed_in_list_becomes_parentheses_on_tsql
        // ⚠⚠ THE BRACKET REWRITE IS THE ONLY CONSTRUCT THE DIALECT CHANGES — see DmnDialect.
        var inList = InList.Match(expr);
        if (inList.Success)
        {
            var (open, close) = ListBrackets(inList.Groups[1].Value, inList.Groups[3].Value, dialect);
            return $"{column} IN {open}{string.Join(", ", ParseListItems(inList.Groups[2].Value, dialect, refPrefix))}{close}";
        }

        // ─── 5. DMN FEEL RANGE ────────────────────────────────────────────────────────────────────
        // CONTEXT : any input cell. Each BOUND is re-parsed as its own comparison (RECURSION POINT).
        // PARSES  : `[a .. b]` `(a .. b]` `[a .. b)` `(a .. b)` — bracket picks >=/> and <=/<
        // SAMPLE  : `[21 .. 65]`  on `age`     ⇒  age >= 21 AND age <= 65
        //           `(0 .. 100]`  on `age`     ⇒  age > 0 AND age <= 100
        //           `[0 .. @age]` on `amount`  ⇒  amount >= 0 AND amount <= age
        // TEST    : Documented_condition_forms, Feel_range_wins_over_comma_splitting
        // ⚠⚠ BEFORE the comma check, and that ORDER IS LOAD-BEARING: `[0 .. func(1,2,?)]` would
        //   otherwise split on the ARGUMENT comma into a three-item IN list — which is still VALID SQL,
        //   so nothing downstream would notice.
        var range = FeelRange.Match(expr);
        if (range.Success)
        {
            var lowOp = range.Groups[1].Value == "[" ? ">=" : ">";
            var highOp = range.Groups[4].Value == "]" ? "<=" : "<";
            var low = ParseCondition($"{lowOp} {range.Groups[2].Value.Trim()}", column, dialect, refPrefix, depth + 1);
            var high = ParseCondition($"{highOp} {range.Groups[3].Value.Trim()}", column, dialect, refPrefix, depth + 1);
            return $"{low} AND {high}";
        }

        // ─── 6. BARE COMMA LIST ───────────────────────────────────────────────────────────────────
        // CONTEXT : any input cell. The commonest membership spelling in a real decision table.
        // PARSES  : `a, b, c` — TOP-LEVEL commas only (quotes and parentheses are skipped)
        // SAMPLE  : `EU, US`    on `region`  ⇒  region IN ('EU', 'US')
        //           `'a,b,c'`   on `region`  ⇒  region = 'a,b,c'   (quoted: ONE value)
        // TEST    : Documented_condition_forms, Function_argument_comma_is_not_a_list
        if (HasTopLevelComma(expr))
        {
            return $"{column} IN ({string.Join(", ", ParseListItems(expr, dialect, refPrefix))})";
        }

        // ─── 7. BETWEEN ───────────────────────────────────────────────────────────────────────────
        // CONTEXT : any input cell. Both bounds are VALUES, so they may be expressions.
        // PARSES  : `between <lo> and <hi>`
        // SAMPLE  : `between 21 and 65`            on `age`  ⇒  age BETWEEN 21 AND 65
        //           `between min(1) and max(100)`  on `age`  ⇒  age BETWEEN min(1) AND max(100)
        // TEST    : Documented_condition_forms
        var between = BetweenAnd.Match(expr);
        if (between.Success)
        {
            var lo = ParseValue(between.Groups[1].Value.Trim(), dialect, refPrefix, depth + 1);
            var hi = ParseValue(between.Groups[2].Value.Trim(), dialect, refPrefix, depth + 1);
            return $"{column} BETWEEN {lo} AND {hi}";
        }

        // ─── 8. LEADING COMPARISON OPERATOR ───────────────────────────────────────────────────────
        // CONTEXT : any input cell. The right-hand side is a VALUE, so a bare word gets QUOTED.
        // PARSES  : `!= <v>` `<> <v>` `>= <v>` `<= <v>` `> <v>` `< <v>` `= <v>`
        // SAMPLE  : `> 18`    on `age`     ⇒  age > 18
        //           `> @age`  on `amount`  ⇒  amount > age
        //           `<> 'x'`  on `region`  ⇒  region <> 'x'
        // TEST    : Documented_condition_forms, Every_leading_operator
        // ⚠ The operator is passed through VERBATIM rather than normalised — `!=` stays `!=`.
        var op = LeadingOperator.Match(expr);
        if (op.Success)
        {
            return $"{column} {op.Groups[1].Value} {ParseValue(op.Groups[2].Value.Trim(), dialect, refPrefix, depth + 1)}";
        }

        // ─── 9. LEADING OPERATOR KEYWORD ──────────────────────────────────────────────────────────
        // CONTEXT : any input cell. Same family as 4/7/8 — THE COLUMN IS IMPLICIT ON THE LEFT, which is
        //           what a decision-table cell IS: a predicate fragment about its own column.
        // PARSES  : LIKE ILIKE GLOB REGEXP | SIMILAR TO | IS [NOT] NULL | IS [NOT] DISTINCT FROM
        // SAMPLE  : `IS NULL`           on `region`  ⇒  region IS NULL
        //           `LIKE 'E%'`         on `region`  ⇒  region LIKE 'E%'
        //           `SIMILAR TO 'E.*'`  on `region`  ⇒  region SIMILAR TO 'E.*'
        //           `LIKE E5`           on `region`  ⇒  region LIKE 'E5'   (the RHS is a VALUE)
        // TEST    : A_leading_keyword_takes_the_column_on_its_left,
        //           A_leading_keywords_right_hand_side_is_a_value,
        //           A_function_name_starting_with_a_keyword_is_not_an_operator
        // ⚠ A pattern containing `%` or `*` must be QUOTED by the author — those are value operators,
        //   so an unquoted `E%` reads as an expression and passes through raw.
        var keyword = LeadingConditionKeyword.Match(expr);
        if (keyword.Success)
        {
            var rest = expr.Substring(keyword.Length).Trim();
            return rest.Length == 0
                ? $"{column} {keyword.Groups[1].Value}"
                : $"{column} {keyword.Groups[1].Value} {ParseValue(rest, dialect, refPrefix, depth + 1)}";
        }

        // ─── 10. FUNCTION CALL AT THE START ───────────────────────────────────────────────────────
        // CONTEXT : an input cell where the author wrote the WHOLE condition, so NOTHING is prepended.
        // PARSES  : `name(` at position 0
        // SAMPLE  : `contains(?, 'foo')`      on `col`  ⇒  contains(col, 'foo')
        //           `CHARINDEX('x', ?) > 0`   on `col`  ⇒  CHARINDEX('x', col) > 0   (T-SQL vocabulary)
        // TEST    : Documented_condition_forms, Cell_function_vocabulary_is_never_translated
        // ⚠ The cell's FUNCTION VOCABULARY is never translated between dialects — that is the feature.
        if (FunctionCallAtStart.IsMatch(expr))
        {
            return ResolveRefs(expr, refPrefix);
        }

        // ─── 11. COMPLETE CONDITION ───────────────────────────────────────────────────────────────
        // CONTEXT : an input cell whose LEFT-HAND SIDE is written out, so nothing is prepended. This is
        //           the counterpart of branch 9: `IS NULL` vs `@region IS NULL` mean the same thing.
        // PARSES  : anything carrying a comparison operator or one of ConditionKeywords
        // SAMPLE  : `@age*2 > min(1)+1`  on `age`     ⇒  age*2 > min(1)+1
        //           `@region IS NULL`    on `region`  ⇒  region IS NULL
        // TEST    : Documented_condition_forms, A_keyword_makes_a_cell_a_full_condition,
        //           Pattern_and_null_operators_are_full_conditions
        //           (and verify_decision_render §2's equivalence row, which asserts 9 and 11 agree)
        // ⚠ A data value containing one of those keywords lands here and passes through as RAW SQL,
        //   which the target refuses at CREATE time — the SAFE direction. Quote the cell to escape.
        if (IsFullCondition(expr))
        {
            return ResolveRefs(expr, refPrefix);
        }

        // ─── 12. FALLBACK: EQUALITY AGAINST A VALUE ───────────────────────────────────────────────
        // CONTEXT : any input cell that reached the end — the commonest cell of all.
        // PARSES  : whatever is left, as a VALUE (see ParseValue for how it is typed or quoted)
        // SAMPLE  : `EU`   on `region`  ⇒  region = 'EU'
        //           `42`   on `age`     ⇒  age = 42
        // TEST    : Documented_condition_forms
        return $"{column} = {ParseValue(expr, dialect, refPrefix, depth + 1)}";
    }

    /// <summary>
    /// Parses an OUTPUT cell (or any bound/value position) into a SQL value expression.
    /// </summary>
    /// <remarks>
    /// The processing ORDER is the contract, and each step exists to stop the next one firing wrongly:
    /// a bare <c>@ref</c> becomes the column; an already-quoted string passes through untouched; a number
    /// passes through; <c>TRUE</c>/<c>FALSE</c>/<c>NULL</c> pass through; anything detected as an
    /// EXPRESSION passes through with <c>@refs</c> resolved; a parenthesised cell is parsed INSIDE and
    /// re-wrapped; and only what survives all of that is quoted as a string literal.
    /// <para>
    /// <b>WHERE IT IS CALLED FROM — four value POSITIONS, not just the output cell.</b> Getting this wrong
    /// is how a rule ends up comparing against the TEXT of an expression.
    /// <code>
    /// an OUTPUT cell                 decision / risk / label   ⇒  ParseValue(cell)
    /// the RHS of a leading operator  `> 18`                    ⇒  ParseValue("18")
    /// a BETWEEN bound                `between min(1) and 5`    ⇒  ParseValue("min(1)"), ParseValue("5")
    /// an IN / comma list item        `EU, US`                  ⇒  ParseValue("EU"), ParseValue("US")
    /// the RHS of a leading keyword   `LIKE E5`                 ⇒  ParseValue("E5")
    /// </code>
    /// </para>
    /// <para>
    /// <b>THE DISPATCH, IN ORDER.</b> Each branch below carries CONTEXT / PARSES / SAMPLE / TEST.
    /// <code>
    ///  #  branch            a cell that reaches it     renders
    ///  1  bare @ref         @age                       age
    ///  2  quoted string     'approve'                  'approve'
    ///  3  number            42, -3, 0.5                42, -3, 0.5
    ///  4  keyword literal   true, null                 TRUE, NULL
    ///  5  expression        @amount * 0.01             amount * 0.01
    ///  6  parenthesised     (func(@x))                 (func(x))
    ///  7  fallback (quote)  approve                    'approve'
    /// </code>
    /// </para>
    /// <para>
    /// ⚠ <c>?</c> IS NOT SUBSTITUTED HERE. In an output cell it has no meaning; in an output
    /// PREPROCESSING expression it means the OUTPUT COLUMN and is substituted by the renderer, not by the
    /// parser (<c>decision_render.sql</c>, <c>post_select_sql</c>).
    /// </para>
    /// </remarks>
    public static string ParseValue(string? cell, DmnDialect dialect = DmnDialect.DuckDb,
                                    string? refPrefix = null)
        => ParseValue(cell, dialect, refPrefix ?? string.Empty, 0);

    private static string ParseValue(string? cell, DmnDialect dialect, string refPrefix, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"decision rule: value expression nests deeper than {MaxDepth} levels — "
                + "this is almost certainly a malformed cell rather than a real rule.");
        }
        var val = (cell ?? string.Empty).Trim();

        // ─── 1. BARE COLUMN REFERENCE ─────────────────────────────────────────────────────────────
        // CONTEXT : any value position. The ONLY place refPrefix is applied outside ResolveRefs.
        // PARSES  : `@name` and nothing else
        // SAMPLE  : `@col`  ⇒  col        (or `preprocessed.col` when a prefix is in force)
        // TEST    : Documented_value_forms, Ref_prefix_reaches_value_expressions_too
        if (BareColumnRef.IsMatch(val))
        {
            return refPrefix + val.Substring(1);
        }
        // ─── 2. ALREADY-QUOTED STRING ─────────────────────────────────────────────────────────────
        // CONTEXT : any value position. This is ALSO the author's ESCAPE HATCH: quoting a cell forces it
        //           to be a literal, past every keyword and comma rule above.
        // PARSES  : text that starts and ends with a single quote
        // SAMPLE  : `'approve'`  ⇒  'approve'
        //           `'a,b,c'`    ⇒  'a,b,c'        (the comma is data, not a list separator)
        // TEST    : Documented_value_forms, Documented_condition_forms,
        //           A_data_value_containing_a_keyword_passes_through_and_quoting_is_the_escape
        if (val.Length >= 2 && val.StartsWith("'", StringComparison.Ordinal) && val.EndsWith("'", StringComparison.Ordinal))
        {
            return val;
        }
        // ─── 3. NUMBER ────────────────────────────────────────────────────────────────────────────
        // CONTEXT : any value position.
        // PARSES  : optional sign, digits, optional decimal point — NO EXPONENT
        // SAMPLE  : `42` ⇒ 42     `-3` ⇒ -3     `0.5` ⇒ 0.5
        // TEST    : Documented_value_forms, Scientific_notation_is_not_a_number
        // ⚠ `1e5` is therefore NOT a number and falls to branch 7 — `= 1e5` compares against the TEXT.
        //   FAITHFUL to the ported engine (identical regex). Write `100000` or `1e5::DOUBLE`.
        if (NumericLiteral.IsMatch(val))
        {
            return val;
        }
        // ─── 4. KEYWORD LITERAL ───────────────────────────────────────────────────────────────────
        // CONTEXT : any value position. Upper-cased, so a lower-case rules table renders canonical SQL.
        // PARSES  : TRUE / FALSE / NULL, any casing
        // SAMPLE  : `true` ⇒ TRUE     `null` ⇒ NULL
        // TEST    : Documented_value_forms, Keyword_literals_are_upper_cased
        // ⚠ A `NULL` OUTPUT cell is rendered by the RENDERER, not here: a blank output cell becomes SQL
        //   NULL in decision_render's introspection, which is a question about the DATA, not the grammar.
        if (val.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || val.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
            || val.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return val.ToUpperInvariant();
        }
        // ─── 5. SQL EXPRESSION ────────────────────────────────────────────────────────────────────
        // CONTEXT : any value position. THIS IS THE BRANCH THAT MAKES A DECISION TABLE *CODE*: whatever
        //           the target engine can compute may appear in a cell, and it is passed through.
        // PARSES  : a function call, a binary operator (+ - is NOT one; see below), or a `::` cast
        // SAMPLE  : `@amount * 0.01`   ⇒  amount * 0.01
        //           `@age::varchar`    ⇒  age::varchar
        //           `'20010101'::date` ⇒  '20010101'::date
        // TEST    : Documented_value_forms
        // ⚠ @refs and quoted strings are STRIPPED before the test, so `'a+b'` is a string and `@a+@b` is
        //   an expression. ⚠ The operator set is `+ * / % || ::` — a LEADING `-` is a number (branch 3).
        if (IsValueExpression(val))
        {
            return ResolveRefs(val, refPrefix);
        }
        // ─── 6. PARENTHESISED ─────────────────────────────────────────────────────────────────────
        // CONTEXT : any value position. The inside is re-parsed as a whole value (RECURSION POINT).
        // PARSES  : `( … )`
        // SAMPLE  : `(func(@x))`  ⇒  (func(x))
        // TEST    : Documented_value_forms, Pathological_nesting_fails_with_a_message
        if (val.Length >= 2 && val.StartsWith("(", StringComparison.Ordinal) && val.EndsWith(")", StringComparison.Ordinal))
        {
            return $"({ParseValue(val.Substring(1, val.Length - 2), dialect, refPrefix, depth + 1)})";
        }
        // ─── 7. FALLBACK: QUOTE AS A STRING LITERAL ───────────────────────────────────────────────
        // CONTEXT : any value position. THE ONLY PLACE A CELL BECOMES A LITERAL — everything above is
        //           passed through, which is why a decision table is CODE rather than data.
        // PARSES  : whatever is left
        // SAMPLE  : `approve`   ⇒  'approve'
        //           `O'Brien`   ⇒  'O''Brien'
        // TEST    : Documented_value_forms, Bare_string_is_quoted_and_escaped
        // ⚠ Doubling the quote is the COMPLETE escape for a SQL string in both dialects here — there is
        //   no backslash escaping to also handle.
        return "'" + val.Replace("'", "''") + "'";
    }

    /// <summary>
    /// TRUE when an output preprocessing expression contains an aggregate, which switches the whole table
    /// into aggregate mode.
    /// </summary>
    /// <remarks>
    /// ⚠ It scans EVERY function call in the expression, not just the outermost one, because the nesting is
    /// the normal shape — <c>EXP(SUM(LOG(?)))</c> is an aggregate and its outer call is not.
    /// </remarks>
    public static bool IsAggregate(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
        {
            return false;
        }
        foreach (Match m in NamedFunctionCall.Matches(expr))
        {
            if (AggregateFunctions.Contains(m.Groups[1].Value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A SQL expression rather than a bare value: a function call, a binary operator, or a cast.</summary>
    /// <remarks>⚠ @refs and quoted strings are stripped FIRST, so `'a+b'` is a string and `@a+@b` is not.</remarks>
    private static bool IsValueExpression(string val)
    {
        var stripped = QuotedString.Replace(ResolveRefs(val, string.Empty), string.Empty);
        return AnyFunctionCall.IsMatch(stripped) || ValueOperators.IsMatch(stripped);
    }

    /// <summary>A COMPLETE condition: it already contains a comparison operator or a condition keyword.</summary>
    private static bool IsFullCondition(string val)
    {
        var stripped = QuotedString.Replace(ResolveRefs(val, string.Empty), string.Empty);
        return ComparisonOperators.IsMatch(stripped) || ConditionKeywords.IsMatch(stripped);
    }

    /// <summary><c>@name</c> → <c>name</c>. A column reference always resolves to the bare column name.</summary>
    /// <summary>Resolves <c>@name</c> to <paramref name="refPrefix"/> + <c>name</c>.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ THE PREFIX EXISTS FOR ONE MEASURED DuckDB RULE: <b>a macro PARAMETER shadows a column of the same
    /// name anywhere in the macro's body</b>, so a generated macro whose parameter is <c>age</c> and whose
    /// preprocessing CTE also produces <c>age</c> reads the RAW PARAMETER everywhere downstream — the
    /// preprocessed value is computed and silently never used. MEASURED both ways: a bare reference yields
    /// the parameter, and a QUALIFIED one (<c>preprocessed.age</c>) yields the CTE column. So the renderer
    /// qualifies, and <c>@refs</c> have to be qualified with it or a cross-column rule would read one value
    /// while the column's own cell read another.
    /// </para>
    /// <para>
    /// ⚠ A <see cref="System.Text.RegularExpressions.MatchEvaluator"/> rather than a <c>"$1"</c> replacement
    /// string, because the prefix is caller-supplied text and <c>$</c> is a substitution metacharacter there.
    /// </para>
    /// <para>
    /// ⚠ The two DETECTORS (<see cref="IsValueExpression"/>, <see cref="IsFullCondition"/>) deliberately call
    /// this with an EMPTY prefix: they strip refs only to decide WHICH branch a cell takes, and that decision
    /// must not depend on how references are spelled.
    /// </para>
    /// </remarks>
    private static string ResolveRefs(string expr, string refPrefix) =>
        ColumnRef.Replace(expr, m => refPrefix + m.Groups[1].Value);

    /// <summary>
    /// TRUE when the cell has a comma at the TOP level — i.e. one that separates list items rather than
    /// function arguments.
    /// </summary>
    /// <remarks>
    /// ⚠ Commas inside quotes and inside parentheses do NOT count, which is what lets
    /// <c>'a,b,c'</c> stay one string and <c>func(1,2)</c> stay one call. Hand-written rather than a regex
    /// because nesting is not a regular language.
    /// </remarks>
    /// <summary>
    /// Replaces every <c>?</c> placeholder OUTSIDE a quoted string literal with <paramref name="column"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠⚠ THE QUOTE AWARENESS IS A FIX, NOT A FLOURISH, AND `GLOB` IS WHAT MADE IT MATTER.</b> A blind
    /// <c>string.Replace</c> — which is what the ported engine does and what this did until 2026-09-16 —
    /// rewrites a <c>?</c> that belongs to the AUTHOR'S PATTERN. MEASURED before the fix:
    /// <c>GLOB 'US?'</c> became <c>region GLOB 'USregion'</c>, and <c>?</c> is GLOB's single-character
    /// WILDCARD, so that is a silently different pattern rather than a syntax error. <c>LIKE 'why?%'</c>
    /// and <c>contains(?, 'a?b')</c> were corrupted the same way.
    /// </para>
    /// <para>
    /// ⚠ <c>''</c> inside a literal is SQL's escape for one quote and is handled by the flip-flop: the pair
    /// toggles out and straight back in, so the scanner stays inside the literal — which is the same rule
    /// <see cref="HasTopLevelComma"/> relies on.
    /// </para>
    /// <para>
    /// Sample: <c>contains(?, 'a?b')</c> over column <c>region</c> ⇒ <c>contains(region, 'a?b')</c>.
    /// Pinned by <c>DecisionRuleParserTests.A_placeholder_inside_a_literal_is_left_alone</c>.
    /// </para>
    /// </remarks>
    private static string SubstitutePlaceholder(string expr, string column)
    {
        var sb = new StringBuilder(expr.Length + column.Length);
        bool inQuote = false;
        foreach (var ch in expr)
        {
            if (ch == '\'')
            {
                inQuote = !inQuote;
                sb.Append(ch);
            }
            else if (ch == '?' && !inQuote)
            {
                sb.Append(column);
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static bool HasTopLevelComma(string s)
    {
        bool inQuote = false;
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == '\'')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == ',' && depth == 0) return true;
            }
        }
        return false;
    }

    private static List<string> ParseListItems(string raw, DmnDialect dialect, string refPrefix)
    {
        var items = new List<string>();
        foreach (var item in raw.Split(','))
        {
            items.Add(ParseValue(item.Trim(), dialect, refPrefix, 0));
        }
        return items;
    }

    /// <summary>
    /// The brackets an <c>IN</c> list keeps.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ THE ONE PLACE THE DIALECT ACTUALLY CHANGES THE OUTPUT. <c>IN [a,b]</c> is a DuckDB LIST literal
    /// and <c>IN (a,b)</c> is an ordinary SQL IN-list; T-SQL has no list literal at all, so a bracketed cell
    /// is rewritten to parentheses rather than rendered into a syntax error. ⚠ They are NOT equivalent on
    /// DuckDB — a LIST is a value and an IN-list is not — which is why the rewrite is dialect-gated instead
    /// of applied everywhere.
    /// </remarks>
    private static (string Open, string Close) ListBrackets(string open, string close, DmnDialect dialect)
        => dialect == DmnDialect.TSql ? ("(", ")") : (open, close);
}
