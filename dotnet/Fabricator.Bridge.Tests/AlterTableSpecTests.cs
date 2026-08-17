using System;
using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// The <c>table_alter</c> request doc (ABI v74) — the only ABI JSON doc the HOST writes and the bridge
/// reads, so nothing short of a full build exercises the pair and these tests are what pin the consumer
/// half. The docs below are written the way <c>FabricatorRenderAlterJson</c> emits them (src/catalog/
/// fabricator_metadata.cpp); keep the two in step.
/// </summary>
public class AlterTableSpecTests
{
    [Theory]
    [InlineData("rename_table", AlterTableKind.RenameTable)]
    [InlineData("rename_column", AlterTableKind.RenameColumn)]
    [InlineData("add_column", AlterTableKind.AddColumn)]
    [InlineData("drop_column", AlterTableKind.DropColumn)]
    [InlineData("column_type", AlterTableKind.ColumnType)]
    [InlineData("set_not_null", AlterTableKind.SetNotNull)]
    [InlineData("drop_not_null", AlterTableKind.DropNotNull)]
    [InlineData("drop_default", AlterTableKind.DropDefault)]
    [InlineData("add_field", AlterTableKind.AddField)]
    [InlineData("drop_field", AlterTableKind.DropField)]
    [InlineData("rename_field", AlterTableKind.RenameField)]
    [InlineData("set_sorted_by", AlterTableKind.SetSortedBy)]
    [InlineData("set_partitioned_by", AlterTableKind.SetPartitionedBy)]
    public void ParsesEveryKind(string wire, AlterTableKind expected)
    {
        Assert.Equal(expected, AlterTableSpec.Parse($"{{\"kind\":\"{wire}\"}}").Kind);
        // The two vocabularies must round-trip, or an error message would name a kind the host never sent.
        Assert.Equal(wire, AlterTableSpec.WireName(expected));
    }

    [Fact]
    public void SetDefaultRoundTripsThroughItsOwnDoc()
    {
        // set_default is the only kind whose wire name cannot be tested by the theory above: it REQUIRES
        // the `default` key, so a bare {"kind":...} doc is (correctly) rejected.
        var spec = AlterTableSpec.Parse("""{"kind":"set_default","column":"c","default":"7"}""");
        Assert.Equal(AlterTableKind.SetDefault, spec.Kind);
        Assert.Equal("set_default", AlterTableSpec.WireName(spec.Kind));
    }

    [Fact]
    public void UnknownKindIsRefused()
    {
        // Refused rather than ignored: an unknown kind means the host and the bridge disagree about the
        // contract, and guessing at DDL is worse than failing it.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AlterTableSpec.Parse("""{"kind":"set_comment"}"""));
        Assert.Contains("set_comment", ex.Message);
    }

    [Fact]
    public void MissingOrNonStringKindIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => AlterTableSpec.Parse("""{"column":"c"}"""));
        Assert.Throws<InvalidOperationException>(() => AlterTableSpec.Parse("""{"kind":7}"""));
    }

    [Fact]
    public void NonObjectRootIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => AlterTableSpec.Parse("""["rename_table"]"""));
    }

    [Fact]
    public void RenameColumnCarriesBothNames()
    {
        var spec = AlterTableSpec.Parse("""{"kind":"rename_column","column":"a","new_name":"b"}""");
        Assert.Equal("a", spec.RequireColumn());
        Assert.Equal("b", spec.RequireNewName());
        Assert.False(spec.Guard);
        Assert.Null(spec.Path);
        Assert.Null(spec.Columns);
    }

    [Fact]
    public void GuardReadsWhicheverSpellingTheKindDefines()
    {
        // The wire keeps two honest keys — an ADD guards on ABSENCE, a DROP on PRESENCE — while every use
        // site wants one question. This is the single place that knows the mapping.
        Assert.True(AlterTableSpec.Parse("""{"kind":"add_column","column":"c","if_not_exists":true}""").Guard);
        Assert.True(AlterTableSpec.Parse("""{"kind":"drop_column","column":"c","if_exists":true}""").Guard);
        Assert.False(AlterTableSpec.Parse("""{"kind":"add_column","column":"c"}""").Guard);
        Assert.False(AlterTableSpec.Parse("""{"kind":"drop_column","column":"c"}""").Guard);
    }

    [Fact]
    public void DefaultNullIsAValueNotAnAbsence()
    {
        // The distinction the old arg2 spelled "-" vs "b"+base64(text). JSON null is DEFAULT NULL.
        var nullDefault = AlterTableSpec.Parse("""{"kind":"set_default","column":"c","default":null}""");
        Assert.Null(nullDefault.DefaultLiteral);

        var literal = AlterTableSpec.Parse("""{"kind":"set_default","column":"c","default":"hi"}""");
        Assert.Equal("hi", literal.DefaultLiteral);

        // The EMPTY literal is why the base64 hack existed at all — a C string cannot tell it from absent.
        var empty = AlterTableSpec.Parse("""{"kind":"set_default","column":"c","default":""}""");
        Assert.Equal("", empty.DefaultLiteral);
    }

    [Fact]
    public void SetDefaultWithoutItsDefaultKeyIsRefused()
    {
        // Absent is NOT DEFAULT NULL: null is a value here, so a missing key can only mean a malformed doc.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AlterTableSpec.Parse("""{"kind":"set_default","column":"c"}"""));
        Assert.Contains("default", ex.Message);
    }

    [Fact]
    public void SetDefaultWithANonStringDefaultIsRefused()
    {
        // The host renders the literal's TEXT, always. A number here would mean the two sides disagree
        // about the encoding, not that a numeric default arrived.
        Assert.Throws<InvalidOperationException>(() =>
            AlterTableSpec.Parse("""{"kind":"set_default","column":"c","default":7}"""));
    }

    [Fact]
    public void FieldPathKeepsItsSegments()
    {
        // The path is an ARRAY because a field name may contain dots — joined, this one is ambiguous.
        var spec = AlterTableSpec.Parse("""{"kind":"drop_field","path":["s","in.ner","f"]}""");
        Assert.Equal(new[] { "s", "in.ner", "f" }, spec.RequirePath());
    }

    [Fact]
    public void IdentifiersSurviveEveryEscapeTheHostMustEmit()
    {
        // The defect this crossing fixed: the hand-rolled builder it replaced escaped only `"` and `\`, so
        // a legal DuckDB identifier carrying a CONTROL character produced invalid JSON and the ALTER died
        // inside this parser naming a byte position. These are the exact bytes yyjson emits for them.
        var spec = AlterTableSpec.Parse("""{"kind":"set_sorted_by","columns":["a\tb","q\"uote","back\\slash","nl\n"]}""");
        Assert.Equal(new[] { "a\tb", "q\"uote", "back\\slash", "nl\n" }, spec.RequireColumns());
    }

    [Fact]
    public void EmptyColumnListIsTheResetSpellingAndNotAnAbsence()
    {
        var reset = AlterTableSpec.Parse("""{"kind":"set_sorted_by","columns":[]}""");
        Assert.NotNull(reset.Columns);
        Assert.Empty(reset.RequireColumns());

        // …whereas a kind with no list at all reports null, so the two can never be confused.
        Assert.Null(AlterTableSpec.Parse("""{"kind":"drop_not_null","column":"c"}""").Columns);
    }

    [Fact]
    public void ANonStringArrayElementIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AlterTableSpec.Parse("""{"kind":"set_sorted_by","columns":["a",7]}"""));
    }

    [Fact]
    public void RequireAccessorsNameTheKindAndTheMissingField()
    {
        // These replace fourteen per-provider null checks; the message must say what the DOC lacked, since
        // that is the only thing a malformed request can be missing.
        var spec = AlterTableSpec.Parse("""{"kind":"rename_field"}""");
        Assert.Contains("rename_field", Assert.Throws<InvalidOperationException>(() => spec.RequireColumn()).Message);
        Assert.Contains("new_name", Assert.Throws<InvalidOperationException>(() => spec.RequireNewName()).Message);
        Assert.Contains("path", Assert.Throws<InvalidOperationException>(() => spec.RequirePath()).Message);
        Assert.Contains("columns", Assert.Throws<InvalidOperationException>(() => spec.RequireColumns()).Message);
    }

    [Fact]
    public void AnEmptyPathIsRefusedBecauseAPathHasAtLeastOneSegment()
    {
        var spec = AlterTableSpec.Parse("""{"kind":"drop_field","path":[]}""");
        Assert.Throws<InvalidOperationException>(() => spec.RequirePath());
    }

    [Fact]
    public void UnknownKeysAreIgnoredSoTheDocCanGrow()
    {
        // Additive keys must not break an older bridge — the same forward-compat rule the rowid entries
        // note on ITableBinding relies on.
        var spec = AlterTableSpec.Parse("""{"kind":"drop_column","column":"c","future_key":{"a":1}}""");
        Assert.Equal("c", spec.RequireColumn());
    }
}
