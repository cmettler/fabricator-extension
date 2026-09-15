// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Text.RegularExpressions;
using Fabricator.Bridge;

namespace Fabricator.Functions;

/// <summary>How the <c>rules</c> argument of a decision-table function names its rules.</summary>
public enum DmnRulesSource
{
    /// <summary>Decide from the text itself — see <see cref="DecisionRuleSource.Classify"/>.</summary>
    Auto,

    /// <summary>A (possibly dotted) relation NAME: <c>decision_rules</c>, <c>db.main.decision_rules</c>.</summary>
    Table,

    /// <summary>A PATH DuckDB can read: <c>rules.csv</c>, <c>C:/x/rules.parquet</c>, <c>s3://b/rules.json</c>.</summary>
    File,

    /// <summary>A JSON ARRAY OF OBJECTS, one object per rules row, as literal text.</summary>
    Json,

    /// <summary>A relation EXPRESSION spliced verbatim — exactly what would follow <c>FROM</c>.</summary>
    Sql,
}

/// <summary>
/// Turns the <c>rules</c> argument into the FROM-clause text the generated introspection reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ IT IS IN C# FOR THE SAME REASON THE PARSER IS: it produces SQL TEXT THAT GETS SPLICED, so it is
/// exactly the kind of thing worth pinning OFFLINE</b> — no DuckDB, no rules table, no render. Every branch
/// below is one tier-0 assertion, where the same logic written in Liquid could only be tested by rendering
/// something and reading the result.
/// </para>
/// <para>
/// <b>⚠⚠ AUTO NEVER GUESSES <see cref="DmnRulesSource.Sql"/>.</b> A relation expression is not separable
/// from a relation name by any cheap test that does not have DuckDB's grammar in it — "contains a
/// parenthesis" would classify a table called <c>rules(2024)</c> as SQL and splice it — so that one mode is
/// REQUEST-ONLY. The three a caller actually reaches for are separated by tests that cannot collide: JSON
/// content starts with a bracket, a path carries a separator or a data extension, and a relation name is
/// what is left.
/// </para>
/// <para>
/// ⚠ A name that IS path-shaped (a table literally called <c>rules.csv</c>) stays reachable with
/// <c>source := 'table'</c>, which is what the override exists for. Preferring the FILE reading in
/// <c>auto</c> is deliberate: that collision is vanishingly rare in one direction and the everyday case in
/// the other.
/// </para>
/// </remarks>
public static class DecisionRuleSource
{
    // ⚠ Deliberately NOT keyed on a dot: `db.main.rules` is an ordinary relation name and must not read as a
    // file. What makes a path a path is a SEPARATOR or a DATA EXTENSION, nothing else.
    private static readonly Regex PathSeparator = new(@"[/\\]|://");

    private static readonly Regex DottedIdentifier =
        new(@"^[A-Za-z_][A-Za-z0-9_$]*(\.[A-Za-z_][A-Za-z0-9_$]*)*$");

    // ⚠ A COMPRESSION suffix is stripped before the extension is read, so `rules.csv.gz` is still a csv.
    private static readonly string[] CompressionSuffixes = { ".gz", ".zst", ".bz2" };

    private static readonly string[] DataExtensions =
    {
        ".csv", ".tsv", ".txt", ".parquet", ".json", ".jsonl", ".ndjson", ".xlsx", ".arrow", ".feather",
    };

    private static readonly string[] CsvExtensions = { ".csv", ".tsv", ".txt" };

    /// <summary>Maps the SQL-level <c>source :=</c> value onto the enum, refusing an unknown one BY NAME.</summary>
    public static DmnRulesSource ParseSource(string? source)
    {
        var s = (source ?? string.Empty).Trim();
        if (s.Length == 0 || s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return DmnRulesSource.Auto;
        if (s.Equals("table", StringComparison.OrdinalIgnoreCase)) return DmnRulesSource.Table;
        if (s.Equals("file", StringComparison.OrdinalIgnoreCase)) return DmnRulesSource.File;
        if (s.Equals("json", StringComparison.OrdinalIgnoreCase)) return DmnRulesSource.Json;
        if (s.Equals("sql", StringComparison.OrdinalIgnoreCase)) return DmnRulesSource.Sql;

        throw new InvalidOperationException(
            $"decision rules: source '{s}' is not one of auto, table, file, json, sql.");
    }

    /// <summary>What <c>auto</c> decides for this text. Never answers <see cref="DmnRulesSource.Sql"/>.</summary>
    public static DmnRulesSource Classify(string rules)
    {
        var r = rules.Trim();

        // JSON content first: it is the one form whose FIRST CHARACTER settles it, and a relation name can
        // never begin with a bracket.
        if (r.StartsWith("[", StringComparison.Ordinal) || r.StartsWith("{", StringComparison.Ordinal))
        {
            return DmnRulesSource.Json;
        }
        if (PathSeparator.IsMatch(r) || HasDataExtension(r))
        {
            return DmnRulesSource.File;
        }
        return DmnRulesSource.Table;
    }

    /// <summary>The FROM-clause text for this rules argument.</summary>
    public static string Relation(string? rules, string? source = null)
    {
        var r = (rules ?? string.Empty).Trim();
        if (r.Length == 0)
        {
            throw new InvalidOperationException("decision rules: the rules argument is empty.");
        }

        var kind = ParseSource(source);
        if (kind == DmnRulesSource.Auto)
        {
            kind = Classify(r);
        }

        switch (kind)
        {
            case DmnRulesSource.Sql:
                // ⚠ VERBATIM, which is the whole point of the mode: the escape hatch for anything the other
                // three cannot spell (a subquery, a VALUES list, a reader with options). A decision table is
                // a TRUSTED AUTHORING SURFACE — see DecisionRuleParser — so splicing here is the trust the
                // cells already carry, not a new one.
                return r;

            case DmnRulesSource.Json:
                // ⚠⚠ THE COLUMNS ARE INFERRED BY DuckDB, NOT BY US: json_structure() reads the array's own
                // shape, from_json() builds a LIST of STRUCT from it, and two unnests turn that into an
                // ordinary wide relation. MEASURED on the shipped build — a two-object array comes back as
                // rulepos/region/decision columns, and a column mixing numbers and text unifies to VARCHAR.
                // That is what keeps every step DOWNSTREAM identical across all four sources: the
                // introspection never learns where its rows came from.
                // ⚠ It needs the json extension, so any suite exercising this branch owes a `require json`.
                var lit = DuckSql.QuoteString(r);
                return "(SELECT unnest(_dmn_row) FROM (SELECT unnest(from_json("
                     + lit + ", json_structure(" + lit + "))) AS _dmn_row))";

            case DmnRulesSource.File:
                // ⚠⚠ `all_varchar = true` ON CSV IS NOT TIDINESS — MEASURED on the shipped build: without it
                // the sniffer types a column of bare numbers as BIGINT, so a rules column whose cells all
                // happen to be numeric stops being text. Every other format carries the types the FILE
                // states and the introspection casts to VARCHAR anyway; only the CSV sniffer invents one.
                // ⚠ Everything else is the BARE literal, i.e. DuckDB's replacement scan — parquet, json and
                // xlsx all resolve there. Deliberately NOT a hand-written reader call per format: a reader
                // signature we cannot test is a branch that fails at the call site, and read_xlsx is not
                // even registered in this build.
                return IsCsv(r)
                    ? "read_csv(" + DuckSql.QuoteString(r) + ", all_varchar = true)"
                    : DuckSql.QuoteString(r);

            default:
                // ⚠ A bare dotted name is quoted PART BY PART, so `db.main.rules` stays three identifiers
                // rather than one identifier containing dots. Anything else is quoted WHOLE, which is what
                // makes a relation called `odd name` reachable; a name needing per-part quoting AND special
                // characters is what `source := 'sql'` is for.
                return DottedIdentifier.IsMatch(r)
                    ? string.Join(".", Array.ConvertAll(r.Split('.'), DuckSql.QuoteIdent))
                    : DuckSql.QuoteIdent(r);
        }
    }

    private static bool IsCsv(string path)
    {
        var p = StripCompression(path);
        foreach (var ext in CsvExtensions)
        {
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasDataExtension(string path)
    {
        var p = StripCompression(path);
        foreach (var ext in DataExtensions)
        {
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string StripCompression(string path)
    {
        foreach (var suffix in CompressionSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(0, path.Length - suffix.Length);
            }
        }
        return path;
    }
}
