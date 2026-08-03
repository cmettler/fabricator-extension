using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// The Fabric variable-library definition format: decoding it, resolving values through a value set, and
/// building the documents the write functions send back.
/// </summary>
/// <remarks>
/// <para><b>Why this has a test project at all.</b> There is no Fabric API that returns a variable's effective
/// value — the values live in the item DEFINITION as base64 parts, so resolution is ours to implement, and it
/// is implemented against documentation that <i>contradicts itself in four places</i> (the value-set folder
/// name, the casing of <c>type</c>, which types exist, and the JSON type of an override's value). Every one of
/// those is a silent wrong answer if guessed, and none of them is reachable by a live call unless a variable
/// library happens to exist on the tenant — which is a tenant configuration, not something a test can arrange
/// (creating one is refused for a service principal). See docs/fabric-api-functions.md §9j.</para>
/// <para><b>The four documentation contradictions each have a named test below</b>, so a future "simplification"
/// that trusts the docs fails here and says which page misled it.</para>
/// </remarks>
public class VariableLibraryFormatTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // A definition shaped like the documented one, spelled the awkward ways the docs allow.
    // `type` casing varies on purpose: the REST page's own example has "String" and lowercase "boolean".
    private const string Variables = """
    {
      "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/variableLibrary/definition/variables/1.0.0/schema.json",
      "variables": [
        { "name": "target_lakehouse", "type": "ItemReference", "note": "where models land",
          "value": { "workspaceId": "aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb",
                     "itemId": "bbbbbbbb-1111-2222-3333-cccccccccccc" } },
        { "name": "env_name", "type": "String", "value": "dev" },
        { "name": "batch_size", "type": "Integer", "value": 500 },
        { "name": "strict", "type": "boolean", "value": true },
        { "name": "cutoff", "type": "DateTime", "value": "2025-01-20T15:30:00Z" },
        { "name": "ratio", "type": "Number", "value": 1.5 }
      ]
    }
    """;

    // Overrides an OBJECT-valued variable, and names a variable with different casing than declared.
    private const string ProdSet = """
    {
      "name": "prod",
      "description": "production",
      "variableOverrides": [
        { "name": "TARGET_LAKEHOUSE",
          "value": { "workspaceId": "11111111-0000-0000-0000-000000000000",
                     "itemId": "22222222-0000-0000-0000-000000000000" } },
        { "name": "env_name", "value": "prod" },
        { "name": "batch_size", "value": 50000 }
      ]
    }
    """;

    // The declared name deliberately differs from the file stem — the doc only says they must be "similar".
    private const string TestSet = """
    { "name": "test", "variableOverrides": [ { "name": "env_name", "value": "test" } ] }
    """;

    private const string Settings = """{ "valueSetsOrder": [ "prod", "test" ] }""";

    /// <summary>
    /// The sample definition, using BOTH documented spellings of the value-set folder and a backslash
    /// separator, plus two parts that must be tolerated rather than fail the decode.
    /// </summary>
    private static VariableLibraryDefinition Sample() => VariableLibraryFormat.Decode(new[]
    {
        ("variables.json", B64(Variables)),
        (@"valueSets\prod.json", B64(ProdSet)),      // plural folder + backslash (the parts TABLE's spelling)
        ("valueSet/test-set.json", B64(TestSet)),    // singular folder + stem mismatch (the EXAMPLE's spelling)
        ("settings.json", B64(Settings)),
        (".platform", B64("{\"metadata\":{}}")),     // must be ignored, not fail
        ("junk.json", "not-valid-base64!!"),         // must be skipped, not throw
    });

    private static Dictionary<string, ResolvedVariable> Resolved(
        VariableLibraryDefinition def, string? valueSet = null) =>
        def.Resolve(valueSet, out _).ToDictionary(v => v.Name, v => v);

    // ── the decode itself ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_reads_every_variable_and_value_set()
    {
        // Guards every other test in the file: if the decode silently found nothing, assertions about
        // individual values would pass vacuously.
        var def = Sample();
        Assert.Equal(6, def.Defaults.Count);
        Assert.Equal(2, def.ValueSets.Count);
    }

    [Fact]
    public void Decode_tolerates_parts_it_does_not_understand()
    {
        // `.platform` is a legal part we have no use for, and an undecodable payload must not take the
        // whole definition down — a library is still readable when one part is junk.
        var def = VariableLibraryFormat.Decode(new[]
        {
            ("variables.json", B64(Variables)),
            (".platform", B64("{\"metadata\":{}}")),
            ("junk.json", "not-valid-base64!!"),
        });
        Assert.Equal(6, def.Defaults.Count);
    }

    /// <summary>
    /// CONTRADICTION 1: the value-set folder is spelled `valueSets\valueSetName.json` in the parts table and
    /// `valueSet/valueSet1.json` in the payload example on the SAME page. Accepting only one yields zero value
    /// sets and NO error, so every override silently stops applying.
    /// </summary>
    [Theory]
    [InlineData(@"valueSets\prod.json")]
    [InlineData("valueSets/prod.json")]
    [InlineData(@"valueSet\prod.json")]
    [InlineData("valueSet/prod.json")]
    public void Decode_accepts_both_documented_value_set_folder_spellings(string path)
    {
        var def = VariableLibraryFormat.Decode(new[]
        {
            ("variables.json", B64(Variables)),
            (path, B64(ProdSet)),
        });
        Assert.True(def.ValueSets.ContainsKey("prod"), $"'{path}' should decode as the value set 'prod'");
    }

    [Fact]
    public void Decode_prefers_the_declared_value_set_name_over_the_file_stem()
    {
        // The doc says the file name must be "similar to" the set name — which is not "equal to".
        Assert.True(Sample().ValueSets.ContainsKey("test"));
    }

    [Fact]
    public void Decode_ignores_a_json_file_outside_a_value_set_folder()
    {
        var def = VariableLibraryFormat.Decode(new[]
        {
            ("variables.json", B64(Variables)),
            ("somewhere/prod.json", B64(ProdSet)),
        });
        Assert.Empty(def.ValueSets);
    }

    // ── resolution ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_without_a_value_set_returns_the_declared_defaults()
    {
        var def = Sample();
        def.ActiveValueSet = null;
        var rows = def.Resolve(null, out var effective).ToList();
        Assert.Equal(string.Empty, effective);
        Assert.All(rows, r => Assert.False(r.IsOverridden));

        var d = rows.ToDictionary(v => v.Name, v => v);
        Assert.Equal("dev", d["env_name"].Value);
        Assert.Equal("500", d["batch_size"].Value);
        Assert.Equal("1.5", d["ratio"].Value);
        Assert.Equal("true", d["strict"].Value);
        Assert.Equal("2025-01-20T15:30:00Z", d["cutoff"].Value);
        Assert.Equal("where models land", d["target_lakehouse"].Note);
    }

    [Fact]
    public void Value_is_bare_text_for_a_string_but_value_json_stays_quoted()
    {
        // The pair exists so `value` is usable directly in SQL while `value_json` remains parseable.
        var d = Resolved(Sample());
        Assert.Equal("dev", d["env_name"].Value);
        Assert.Equal("\"dev\"", d["env_name"].ValueJson);
    }

    [Fact]
    public void An_object_valued_variable_renders_as_its_json_not_as_null()
    {
        // Returning NULL for object types would make ItemReference — the variable kind this whole feature
        // exists for — unreadable through the scalar.
        var d = Resolved(Sample());
        Assert.Contains("bbbbbbbb-1111", d["target_lakehouse"].Value);
        Assert.Equal(d["target_lakehouse"].Value, d["target_lakehouse"].ValueJson);
    }

    /// <summary>
    /// CONTRADICTION 2: the same example carries <c>"type": "String"</c> and lowercase
    /// <c>"type": "boolean"</c>. Types are therefore matched case-insensitively and reported VERBATIM.
    /// </summary>
    [Fact]
    public void Declared_type_casing_is_reported_verbatim_and_still_decodes()
    {
        var d = Resolved(Sample());
        Assert.Equal("boolean", d["strict"].Type);   // NOT normalized to "Boolean"
        Assert.Equal("true", d["strict"].Value);     // but still decoded
    }

    [Fact]
    public void Resolve_applies_only_the_overrides_of_the_named_value_set()
    {
        var def = Sample();
        var rows = def.Resolve("prod", out var effective).ToList();
        Assert.Equal("prod", effective);

        var d = rows.ToDictionary(v => v.Name, v => v);
        Assert.Equal("prod", d["env_name"].Value);
        Assert.True(d["env_name"].IsOverridden);
        Assert.Equal("50000", d["batch_size"].Value);

        // Untouched variables keep their defaults AND must not be flagged.
        Assert.False(d["cutoff"].IsOverridden);
        Assert.Equal("2025-01-20T15:30:00Z", d["cutoff"].Value);
        Assert.False(d["ratio"].IsOverridden);
    }

    [Fact]
    public void An_overridden_variable_keeps_its_declared_type()
    {
        // A value set carries values, never types — so the declaration remains authoritative.
        Assert.Equal("ItemReference", Resolved(Sample(), "prod")["target_lakehouse"].Type);
    }

    [Fact]
    public void Variable_names_are_matched_case_insensitively_when_overriding()
    {
        // Fabric documents variable names as NOT case sensitive; the sample's prod set spells the variable
        // TARGET_LAKEHOUSE where the declaration says target_lakehouse.
        var d = Resolved(Sample(), "prod");
        Assert.True(d["target_lakehouse"].IsOverridden);
        Assert.Contains("22222222-0000", d["target_lakehouse"].Value);
    }

    [Fact]
    public void A_sparse_value_set_leaves_everything_it_does_not_mention_alone()
    {
        var d = Resolved(Sample(), "test");
        Assert.Equal("test", d["env_name"].Value);
        Assert.Equal("500", d["batch_size"].Value);
    }

    [Fact]
    public void Resolve_uses_the_active_value_set_when_none_is_named()
    {
        var def = Sample();
        def.ActiveValueSet = "prod";
        var rows = def.Resolve(null, out var effective).ToList();
        Assert.Equal("prod", effective);
        Assert.Equal("prod", rows.Single(v => v.Name == "env_name").Value);
    }

    [Fact]
    public void An_active_value_set_with_no_file_falls_back_to_the_defaults()
    {
        // The DEFAULT value set has no file under valueSets/ — it IS variables.json — so a library whose
        // active set is the default one lands here legitimately and must not throw.
        var def = Sample();
        def.ActiveValueSet = "Default value set";
        Assert.Equal("dev", Resolved(def)["env_name"].Value);
    }

    [Fact]
    public void A_named_value_set_that_does_not_exist_throws_and_lists_the_known_ones()
    {
        // Falling back to the defaults would report plausible values for the WRONG environment, which is the
        // worst available outcome for a function whose purpose is telling environments apart.
        var ex = Assert.Throws<NotSupportedException>(() => Sample().Resolve("staging", out _).ToList());
        Assert.Contains("staging", ex.Message);
        Assert.Contains("prod", ex.Message);
    }

    [Fact]
    public void Value_sets_are_ordered_by_the_declared_order_then_alphabetically()
    {
        Assert.Equal(new[] { "prod", "test" }, Sample().OrderedValueSets());

        // settings.json may be partial or absent; the service appends what it omits alphabetically.
        var noSettings = VariableLibraryFormat.Decode(new[]
        {
            ("variables.json", B64(Variables)),
            ("valueSets/zeta.json", B64("""{ "name": "zeta", "variableOverrides": [] }""")),
            ("valueSets/alpha.json", B64("""{ "name": "alpha", "variableOverrides": [] }""")),
        });
        Assert.Equal(new[] { "alpha", "zeta" }, noSettings.OrderedValueSets());
    }

    // ── stored formatting must not leak into the value ─────────────────────────────────────────────────

    /// <summary>
    /// Found by LIVE validation, and the offline round trip was structurally blind to it:
    /// <c>JsonElement.GetRawText()</c> returns the raw SOURCE SPAN, so a pretty-printed object value arrived
    /// in a SQL column as <c>{\r\n        "workspaceId": …}</c>. Any producer may indent — the portal, a git
    /// sync, our own writer — so normalizing belongs on the READ side.
    /// </summary>
    [Fact]
    public void Pretty_printed_storage_does_not_leak_newlines_into_the_value()
    {
        const string Indented = "{\r\n  \"variables\": [\r\n    {\r\n      \"name\": \"target\",\r\n"
            + "      \"type\": \"ItemReference\",\r\n      \"value\": {\r\n        \"workspaceId\": \"w1\",\r\n"
            + "        \"itemId\": \"i1\"\r\n      }\r\n    },\r\n    {\r\n      \"name\": \"n\",\r\n"
            + "      \"type\": \"Integer\",\r\n      \"value\": 7\r\n    }\r\n  ]\r\n}";

        var d = Resolved(VariableLibraryFormat.Decode(
            new[] { (VariableLibraryFormat.VariablesPath, B64(Indented)) }));

        Assert.Equal(2, d.Count);                                       // positive control
        Assert.Equal("{\"workspaceId\":\"w1\",\"itemId\":\"i1\"}", d["target"].ValueJson);
        Assert.Equal(d["target"].ValueJson, d["target"].Value);
        Assert.DoesNotContain('\n', d["target"].ValueJson!);
        Assert.DoesNotContain('\r', d["target"].ValueJson!);
        Assert.Equal("7", d["n"].ValueJson);
    }

    // ── typed value rendering (the write side) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("String", "dev", "\"dev\"")]
    [InlineData("DateTime", "2025-01-20T15:30:00Z", "\"2025-01-20T15:30:00Z\"")]
    [InlineData("Guid", "aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb", "\"aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb\"")]
    [InlineData("Integer", "500", "500")]
    [InlineData("Number", "1.1", "1.1")]
    [InlineData("Boolean", "true", "true")]
    public void ValueFor_renders_each_documented_type(string type, string text, string expected) =>
        Assert.Equal(expected, VariableLibraryFormat.ValueFor(type, text)!.ToJsonString());

    [Fact]
    public void A_numeric_looking_string_stays_a_string()
    {
        // The typing trap on the way IN: storing "500" where the library expects 500 produces a variable
        // that reads back as a string and compares wrong everywhere it is used.
        Assert.Equal("\"500\"", VariableLibraryFormat.ValueFor("String", "500")!.ToJsonString());
    }

    [Fact]
    public void Number_keeps_the_literal_the_caller_typed()
    {
        // decimal, not double — 1.1 through a double prints as 1.1000000000000001.
        Assert.Equal("1.1", VariableLibraryFormat.ValueFor("Number", "1.1")!.ToJsonString());
    }

    [Theory]
    [InlineData("Integer", "abc")]
    [InlineData("Integer", "1.5")]
    [InlineData("Number", "abc")]
    [InlineData("Boolean", "yes")]
    public void ValueFor_refuses_a_value_that_does_not_match_the_declared_type(string type, string text) =>
        Assert.Throws<NotSupportedException>(() => VariableLibraryFormat.ValueFor(type, text));

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("[1,2]")]
    public void An_item_reference_must_be_a_json_object(string text) =>
        Assert.Throws<NotSupportedException>(() => VariableLibraryFormat.ValueFor("ItemReference", text));

    /// <summary>
    /// CONTRADICTION 3: the REST page's type table omits <c>Guid</c> and <c>ConnectionReference</c>, which the
    /// concept page lists — so a closed enum here would reject a legitimate type. An unrecognized type parses
    /// as JSON and falls back to a string, with the service validating against the real type.
    /// </summary>
    [Theory]
    [InlineData("42", "42")]
    [InlineData("hello", "\"hello\"")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    public void An_unrecognized_type_parses_as_json_and_falls_back_to_a_string(string text, string expected) =>
        Assert.Equal(expected, VariableLibraryFormat.ValueFor("SomeFutureType", text)!.ToJsonString());

    [Fact]
    public void ValueFor_maps_a_null_value_to_json_null() =>
        Assert.Null(VariableLibraryFormat.ValueFor("String", null));

    // ── document construction and upsert (the write side) ──────────────────────────────────────────────

    [Fact]
    public void UpsertVariable_reports_created_then_updated_and_never_duplicates()
    {
        var doc = VariableLibraryFormat.NewVariablesDoc();
        Assert.True(VariableLibraryFormat.UpsertVariable(
            doc, "env_name", "String", VariableLibraryFormat.ValueFor("String", "dev"), null));

        // Case-varied name: an upsert that matched case-sensitively would append a SECOND entry, and a
        // library with two variables of the same name is invalid.
        Assert.False(VariableLibraryFormat.UpsertVariable(
            doc, "ENV_NAME", "String", VariableLibraryFormat.ValueFor("String", "dev2"), null));
        Assert.Equal(1, VariableLibraryFormat.VariableCount(doc));
    }

    [Fact]
    public void UpsertVariable_keeps_an_existing_note_when_none_is_supplied()
    {
        var doc = VariableLibraryFormat.NewVariablesDoc();
        VariableLibraryFormat.UpsertVariable(doc, "n", "Integer",
            VariableLibraryFormat.ValueFor("Integer", "1"), "keep me");
        VariableLibraryFormat.UpsertVariable(doc, "n", "Integer",
            VariableLibraryFormat.ValueFor("Integer", "2"), null);

        var d = Resolved(VariableLibraryFormat.Decode(
            new[] { (VariableLibraryFormat.VariablesPath, B64(doc.ToJsonString())) }));
        Assert.Equal("keep me", d["n"].Note);
        Assert.Equal("2", d["n"].Value);
    }

    [Fact]
    public void DeclaredType_and_HasVariable_are_case_insensitive()
    {
        var doc = VariableLibraryFormat.NewVariablesDoc();
        VariableLibraryFormat.UpsertVariable(doc, "batch_size", "Integer",
            VariableLibraryFormat.ValueFor("Integer", "500"), null);

        Assert.Equal("Integer", VariableLibraryFormat.DeclaredType(doc, "BATCH_SIZE"));
        Assert.True(VariableLibraryFormat.HasVariable(doc, "BaTcH_SiZe"));
        Assert.False(VariableLibraryFormat.HasVariable(doc, "nope"));
        Assert.Null(VariableLibraryFormat.DeclaredType(doc, "nope"));
    }

    [Fact]
    public void UpsertOverride_reports_created_then_updated()
    {
        var set = VariableLibraryFormat.NewValueSetDoc("prod");
        Assert.True(VariableLibraryFormat.UpsertOverride(set, "env_name", JsonValue.Create("prod")));
        Assert.False(VariableLibraryFormat.UpsertOverride(set, "ENV_NAME", JsonValue.Create("prod2")));
    }

    [Fact]
    public void ValueSetPath_writes_the_plural_spelling()
    {
        // We read either spelling but emit ONE, so a library we wrote has a predictable layout.
        Assert.Equal("valueSets/prod.json", VariableLibraryFormat.ValueSetPath("prod"));
    }

    // ── write → read round trip ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The strongest check available without a tenant: build documents with the write helpers, then read them
    /// with the decoder. It proves the two halves agree on the format rather than each being self-consistent.
    /// </summary>
    /// <remarks>
    /// ⚠ Note what it CANNOT prove, learned the hard way: it serializes compactly, so it is blind to any
    /// defect involving stored FORMATTING. That is why
    /// <see cref="Pretty_printed_storage_does_not_leak_newlines_into_the_value"/> exists separately — a round
    /// trip only tests the shapes you generate.
    /// </remarks>
    [Fact]
    public void Documents_built_by_the_writers_read_back_identically()
    {
        var doc = VariableLibraryFormat.NewVariablesDoc();
        VariableLibraryFormat.UpsertVariable(doc, "env_name", "String",
            VariableLibraryFormat.ValueFor("String", "dev"), null);
        VariableLibraryFormat.UpsertVariable(doc, "batch_size", "Integer",
            VariableLibraryFormat.ValueFor("Integer", "500"), "rows per batch");
        VariableLibraryFormat.UpsertVariable(doc, "ratio", "Number",
            VariableLibraryFormat.ValueFor("Number", "1.1"), null);
        VariableLibraryFormat.UpsertVariable(doc, "strict", "Boolean",
            VariableLibraryFormat.ValueFor("Boolean", "true"), null);
        VariableLibraryFormat.UpsertVariable(doc, "target", "ItemReference",
            VariableLibraryFormat.ValueFor("ItemReference",
                """{"workspaceId":"w1","itemId":"i1"}"""), null);

        // An override's value is rendered from the DECLARED type — which is why the setter takes no type.
        var set = VariableLibraryFormat.NewValueSetDoc("prod");
        VariableLibraryFormat.UpsertOverride(set, "batch_size",
            VariableLibraryFormat.ValueFor(VariableLibraryFormat.DeclaredType(doc, "batch_size"), "50000"));
        VariableLibraryFormat.UpsertOverride(set, "env_name",
            VariableLibraryFormat.ValueFor(VariableLibraryFormat.DeclaredType(doc, "env_name"), "prod"));

        var round = VariableLibraryFormat.Decode(new[]
        {
            (VariableLibraryFormat.VariablesPath, B64(doc.ToJsonString())),
            (VariableLibraryFormat.ValueSetPath("prod"), B64(set.ToJsonString())),
        });

        Assert.Equal(5, round.Defaults.Count);
        Assert.True(round.ValueSets.ContainsKey("prod"));

        var defaults = Resolved(round);
        Assert.Equal("500", defaults["batch_size"].ValueJson);         // unquoted — still an Integer
        Assert.Equal("1.1", defaults["ratio"].ValueJson);              // exact, no float noise
        Assert.Equal("true", defaults["strict"].ValueJson);
        Assert.Equal("\"dev\"", defaults["env_name"].ValueJson);       // quoted — still a String
        Assert.Equal("rows per batch", defaults["batch_size"].Note);
        Assert.Equal("""{"workspaceId":"w1","itemId":"i1"}""", defaults["target"].ValueJson);

        var prod = Resolved(round, "prod");
        Assert.Equal("50000", prod["batch_size"].ValueJson);           // the override stayed typed
        Assert.True(prod["batch_size"].IsOverridden);
        Assert.Equal("prod", prod["env_name"].Value);
        Assert.False(prod["target"].IsOverridden);
    }

    /// <summary>
    /// CONTRADICTION 4: the schema table types an override's <c>value</c> as <c>String</c>. It is wrong for
    /// objects — and mutation-testing showed it is wrong for INTEGER too, so it is wrong for every non-string
    /// type. Overrides therefore keep the raw JSON value.
    /// </summary>
    [Fact]
    public void A_non_string_override_survives_despite_the_documented_string_type()
    {
        var d = Resolved(Sample(), "prod");

        // object-valued
        Assert.Contains("22222222-0000", d["target_lakehouse"].Value);
        Assert.True(d["target_lakehouse"].IsOverridden);

        // and the one the docs mislead about most quietly
        Assert.Equal("50000", d["batch_size"].ValueJson);
    }
}
