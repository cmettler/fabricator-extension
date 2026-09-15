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
    private static readonly Regex FunctionCallAtStart = new(@"^[a-zA-Z_]\w*\s*\(");
    private static readonly Regex ColumnRef = new(@"@(\w+)");
    private static readonly Regex BareColumnRef = new(@"^@\w+$");
    private static readonly Regex NumericLiteral = new(@"^-?(\d+\.?\d*|\.\d+)$");
    private static readonly Regex QuotedString = new(@"'[^']*'");
    private static readonly Regex AnyFunctionCall = new(@"\b[a-zA-Z_]\w*\s*\(");
    private static readonly Regex ValueOperators = new(@"[+*/%]|\|\||::");
    private static readonly Regex ComparisonOperators = new(@">=|<=|<>|!=|(?<![<>!])=|(?<!=)[><](?!=)");
    private static readonly Regex ConditionKeywords = new(@"\b(?:BETWEEN|LIKE|ILIKE|REGEXP|IN)\b", RegexOptions.IgnoreCase);
    private static readonly Regex NamedFunctionCall = new(@"\b([a-zA-Z_]\w*)\s*\(");

    /// <summary>⚠ Guards the three recursion points against a pathological cell. A rule is authored, so a
    /// real one nests two or three deep; this exists so a malformed cell fails with a sentence instead of a
    /// StackOverflowException, which no catch can turn back into an error message.</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Parses an INPUT CONDITION cell into a SQL boolean expression over <paramref name="column"/>.
    /// </summary>
    /// <remarks>
    /// Empty, null, whitespace or <c>*</c> is a WILDCARD and yields <c>TRUE</c> — it must, because a
    /// decision table's blank cell means "any value", and rendering anything else would silently narrow
    /// every rule that leaves a column open.
    /// <para>⚠ ORDER IS LOAD-BEARING IN ONE PLACE: the FEEL range is matched BEFORE the comma check, or
    /// <c>[0 .. func(1,2,?)]</c> is split on the argument comma and parsed as a three-item IN list.</para>
    /// </remarks>
    public static string ParseCondition(string? cell, string column, DmnDialect dialect = DmnDialect.DuckDb)
        => ParseCondition(cell, column, dialect, 0);

    private static string ParseCondition(string? cell, string column, DmnDialect dialect, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"decision rule: expression nests deeper than {MaxDepth} levels for column '{column}' — "
                + "this is almost certainly a malformed cell rather than a real rule.");
        }
        var expr = cell?.Trim();
        if (string.IsNullOrEmpty(expr) || expr == "*")
        {
            return "TRUE";
        }

        if (expr.Contains('?'))
        {
            // `?` AT THE START delegates the REST to the parser, so the remainder gets ordinary value
            // treatment (`? >=a` → col >= 'a'). `?` ANYWHERE ELSE is a plain substitution of the column
            // name and parsing continues (`5 >=?`, `contains(?, 'x')`), which is RAW — the author is
            // writing SQL at that point.
            var lead = PlaceholderAtStart.Match(expr);
            if (lead.Success)
            {
                return ParseCondition(lead.Groups[1].Value, column, dialect, depth + 1);
            }
            expr = expr.Replace("?", column);
        }

        var not = NotPrefix.Match(expr);
        if (not.Success)
        {
            return $"NOT ({ParseCondition(not.Groups[1].Value, column, dialect, depth + 1)})";
        }

        var inList = InList.Match(expr);
        if (inList.Success)
        {
            var (open, close) = ListBrackets(inList.Groups[1].Value, inList.Groups[3].Value, dialect);
            return $"{column} IN {open}{string.Join(", ", ParseListItems(inList.Groups[2].Value, dialect))}{close}";
        }

        // ⚠ BEFORE the comma check — see the remark on ParseCondition.
        var range = FeelRange.Match(expr);
        if (range.Success)
        {
            var lowOp = range.Groups[1].Value == "[" ? ">=" : ">";
            var highOp = range.Groups[4].Value == "]" ? "<=" : "<";
            var low = ParseCondition($"{lowOp} {range.Groups[2].Value.Trim()}", column, dialect, depth + 1);
            var high = ParseCondition($"{highOp} {range.Groups[3].Value.Trim()}", column, dialect, depth + 1);
            return $"{low} AND {high}";
        }

        if (HasTopLevelComma(expr))
        {
            return $"{column} IN ({string.Join(", ", ParseListItems(expr, dialect))})";
        }

        var between = BetweenAnd.Match(expr);
        if (between.Success)
        {
            var lo = ParseValue(between.Groups[1].Value.Trim(), dialect);
            var hi = ParseValue(between.Groups[2].Value.Trim(), dialect);
            return $"{column} BETWEEN {lo} AND {hi}";
        }

        var op = LeadingOperator.Match(expr);
        if (op.Success)
        {
            return $"{column} {op.Groups[1].Value} {ParseValue(op.Groups[2].Value.Trim(), dialect)}";
        }

        // A bare function call is a condition the author wrote in full — resolve @refs and pass through.
        if (FunctionCallAtStart.IsMatch(expr))
        {
            return ResolveRefs(expr);
        }

        // A COMPLETE condition (`@age*2 > min(1)+1`) — pass through with @refs resolved.
        if (IsFullCondition(expr))
        {
            return ResolveRefs(expr);
        }

        return $"{column} = {ParseValue(expr, dialect)}";
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
    /// </remarks>
    public static string ParseValue(string? cell, DmnDialect dialect = DmnDialect.DuckDb)
        => ParseValue(cell, dialect, 0);

    private static string ParseValue(string? cell, DmnDialect dialect, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"decision rule: value expression nests deeper than {MaxDepth} levels — "
                + "this is almost certainly a malformed cell rather than a real rule.");
        }
        var val = (cell ?? string.Empty).Trim();

        if (BareColumnRef.IsMatch(val))
        {
            return val.Substring(1);
        }
        if (val.Length >= 2 && val.StartsWith("'", StringComparison.Ordinal) && val.EndsWith("'", StringComparison.Ordinal))
        {
            return val;
        }
        if (NumericLiteral.IsMatch(val))
        {
            return val;
        }
        if (val.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || val.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
            || val.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return val.ToUpperInvariant();
        }
        if (IsValueExpression(val))
        {
            return ResolveRefs(val);
        }
        if (val.Length >= 2 && val.StartsWith("(", StringComparison.Ordinal) && val.EndsWith(")", StringComparison.Ordinal))
        {
            return $"({ParseValue(val.Substring(1, val.Length - 2), dialect, depth + 1)})";
        }
        // ⚠ The ONLY place a cell becomes a LITERAL. Doubling the quote is the complete escape for a SQL
        // string in every dialect here — there is no backslash escaping to also handle.
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
        var stripped = QuotedString.Replace(ResolveRefs(val), string.Empty);
        return AnyFunctionCall.IsMatch(stripped) || ValueOperators.IsMatch(stripped);
    }

    /// <summary>A COMPLETE condition: it already contains a comparison operator or a condition keyword.</summary>
    private static bool IsFullCondition(string val)
    {
        var stripped = QuotedString.Replace(ResolveRefs(val), string.Empty);
        return ComparisonOperators.IsMatch(stripped) || ConditionKeywords.IsMatch(stripped);
    }

    /// <summary><c>@name</c> → <c>name</c>. A column reference always resolves to the bare column name.</summary>
    private static string ResolveRefs(string expr) => ColumnRef.Replace(expr, "$1");

    /// <summary>
    /// TRUE when the cell has a comma at the TOP level — i.e. one that separates list items rather than
    /// function arguments.
    /// </summary>
    /// <remarks>
    /// ⚠ Commas inside quotes and inside parentheses do NOT count, which is what lets
    /// <c>'a,b,c'</c> stay one string and <c>func(1,2)</c> stay one call. Hand-written rather than a regex
    /// because nesting is not a regular language.
    /// </remarks>
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

    private static List<string> ParseListItems(string raw, DmnDialect dialect)
    {
        var items = new List<string>();
        foreach (var item in raw.Split(','))
        {
            items.Add(ParseValue(item.Trim(), dialect));
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
