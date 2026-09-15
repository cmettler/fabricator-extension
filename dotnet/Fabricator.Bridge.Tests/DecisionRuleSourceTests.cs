// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Fabricator.Functions;
using Xunit;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// <see cref="DecisionRuleSource"/> — the <c>rules</c> argument to a FROM clause.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ EVERY ASSERTION HERE IS ABOUT TEXT THAT GETS SPLICED INTO A GENERATED STATEMENT, which is why the
/// classifier is C# at all: a wrong answer is not an exception, it is a statement that runs and reads the
/// wrong thing. Offline, no DuckDB, no rules table, no render.
/// </para>
/// <para>
/// ⚠ The FORMS below (<c>read_csv(..., all_varchar = true)</c>, the <c>from_json</c>/<c>json_structure</c>
/// pair) were MEASURED against the shipped build before being written here — a reader signature that only
/// looks right is a branch that fails at the call site, which is exactly what this file cannot catch.
/// </para>
/// </remarks>
public class DecisionRuleSourceTests
{
    // ── auto-classification ──────────────────────────────────────────────────────────────────────────
    // ⚠⚠ AUTO NEVER ANSWERS `Sql`, and that is the design: no cheap test separates a relation EXPRESSION
    // from a relation NAME, so guessing would splice a table called `rules(2024)`.
    [Theory]
    [InlineData("decision_rules", DmnRulesSource.Table)]
    [InlineData("db.main.decision_rules", DmnRulesSource.Table)]
    [InlineData("  spaced_name  ", DmnRulesSource.Table)]
    [InlineData("rules.csv", DmnRulesSource.File)]
    [InlineData("rules.CSV", DmnRulesSource.File)]
    [InlineData("rules.parquet", DmnRulesSource.File)]
    [InlineData("rules.csv.gz", DmnRulesSource.File)]
    [InlineData("C:/x/rules", DmnRulesSource.File)]
    [InlineData("C:\\x\\rules", DmnRulesSource.File)]
    [InlineData("s3://bucket/rules.json", DmnRulesSource.File)]
    [InlineData("[{\"rulepos\":1}]", DmnRulesSource.Json)]
    [InlineData("  {\"rulepos\":1}", DmnRulesSource.Json)]
    public void Auto_classifies(string rules, DmnRulesSource expected)
        => Assert.Equal(expected, DecisionRuleSource.Classify(rules));

    // ⚠ A DOTTED NAME IS NOT A PATH. `db.main.rules` is the everyday qualified relation, so the file test
    // keys on a SEPARATOR or a KNOWN DATA EXTENSION — never on the presence of a dot.
    [Fact]
    public void Dotted_relation_name_is_not_a_file()
        => Assert.Equal("\"db\".\"main\".\"decision_rules\"",
            DecisionRuleSource.Relation("db.main.decision_rules"));

    // ── the four relations ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Table_is_quoted_per_part()
        => Assert.Equal("\"decision_rules\"", DecisionRuleSource.Relation("decision_rules"));

    // ⚠ A name that is NOT a bare dotted identifier is quoted WHOLE, so a relation called `odd name` is
    // reachable. Splitting it on dots would invent qualification the caller never wrote.
    [Fact]
    public void Odd_table_name_is_quoted_whole()
        => Assert.Equal("\"odd name\"", DecisionRuleSource.Relation("odd name", "table"));

    [Fact]
    public void Embedded_quote_in_a_table_name_is_doubled()
        => Assert.Equal("\"od\"\"d\"", DecisionRuleSource.Relation("od\"d", "table"));

    // ⚠⚠ `all_varchar = true` IS THE ONE OPTION WITH A MEASURED REASON: without it DuckDB's CSV sniffer
    // types a column of bare numbers as BIGINT, and a decision table's cells stop being text.
    [Theory]
    [InlineData("rules.csv")]
    [InlineData("rules.tsv")]
    [InlineData("rules.txt")]
    [InlineData("rules.CSV")]
    public void Csv_is_read_all_varchar(string path)
        => Assert.Equal($"read_csv('{path}', all_varchar = true)", DecisionRuleSource.Relation(path));

    // ⚠ A compression suffix is stripped BEFORE the extension is read, so a gzipped csv is still a csv.
    [Fact]
    public void Compressed_csv_is_still_a_csv()
        => Assert.Equal("read_csv('rules.csv.gz', all_varchar = true)",
            DecisionRuleSource.Relation("rules.csv.gz"));

    // ⚠ Everything else is the BARE literal — DuckDB's replacement scan. Deliberately NOT a hand-written
    // reader call per format: `read_xlsx` is not even registered in this build, so a branch naming it could
    // only fail at the call site.
    [Theory]
    [InlineData("rules.parquet")]
    [InlineData("rules.json")]
    [InlineData("rules.xlsx")]
    [InlineData("s3://b/rules.parquet")]
    public void Other_files_are_the_bare_literal(string path)
        => Assert.Equal($"'{path}'", DecisionRuleSource.Relation(path));

    [Fact]
    public void A_path_with_an_apostrophe_is_escaped()
        => Assert.Equal("'/tmp/o''brien.parquet'", DecisionRuleSource.Relation("/tmp/o'brien.parquet"));

    // ⚠ The JSON form names the literal TWICE — once for the data and once for json_structure, which is
    // what makes DuckDB infer the columns instead of us declaring them.
    [Fact]
    public void Json_infers_its_own_columns()
        => Assert.Equal(
            "(SELECT unnest(_dmn_row) FROM (SELECT unnest(from_json('[{\"a\":1}]', "
            + "json_structure('[{\"a\":1}]'))) AS _dmn_row))",
            DecisionRuleSource.Relation("[{\"a\":1}]"));

    // ⚠ `sql` is VERBATIM. It is REQUEST-ONLY for a reason (see the auto theory above), and it is the
    // escape hatch for everything the other three cannot spell.
    [Fact]
    public void Sql_is_spliced_verbatim()
        => Assert.Equal("(VALUES (1)) v(rulepos)",
            DecisionRuleSource.Relation("(VALUES (1)) v(rulepos)", "sql"));

    // ⚠⚠ THE OVERRIDE BEATS THE CLASSIFIER, AND A DOTTED NAME IS STILL READ AS QUALIFIED — which is the
    // standard SQL reading and is what someone writing `source := 'table'` on `sales.rules` means. So the
    // override takes `rules.csv` OUT of the file branch but does NOT make it one identifier: that shape
    // (a relation literally NAMED `rules.csv`) needs `source := 'sql'` with the name pre-quoted. Pinned
    // because "the override makes any name reachable" is the plausible reading, and it is false.
    [Fact]
    public void Source_override_beats_the_classifier()
        => Assert.Equal("\"rules\".\"csv\"", DecisionRuleSource.Relation("rules.csv", "table"));

    [Fact]
    public void A_relation_named_like_a_path_needs_the_sql_form()
        => Assert.Equal("\"rules.csv\"", DecisionRuleSource.Relation("\"rules.csv\"", "sql"));

    // ── refusals ─────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_rules_argument_is_refused(string? rules)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DecisionRuleSource.Relation(rules));
        Assert.Contains("rules argument is empty", ex.Message, StringComparison.Ordinal);
    }

    // ⚠ An unknown source is refused BY NAME rather than falling back to auto: it was written to SELECT
    // something, so a silent fallback would read the rules from somewhere the caller did not ask for.
    [Fact]
    public void An_unknown_source_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DecisionRuleSource.Relation("t", "spreadsheet"));
        Assert.Contains("auto, table, file, json, sql", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, DmnRulesSource.Auto)]
    [InlineData("", DmnRulesSource.Auto)]
    [InlineData("AUTO", DmnRulesSource.Auto)]
    [InlineData("Table", DmnRulesSource.Table)]
    [InlineData("file", DmnRulesSource.File)]
    [InlineData("json", DmnRulesSource.Json)]
    [InlineData("sql", DmnRulesSource.Sql)]
    public void Source_names_are_case_insensitive(string? source, DmnRulesSource expected)
        => Assert.Equal(expected, DecisionRuleSource.ParseSource(source));
}
