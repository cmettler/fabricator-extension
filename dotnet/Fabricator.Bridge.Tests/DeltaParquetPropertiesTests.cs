using System;
using System.Collections.Generic;
using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// The persisted parquet tuning format (<c>fabricator.parquet.*</c> table properties).
/// </summary>
/// <remarks>
/// What these tests are FOR, beyond coverage: the property is written by a CREATE and read back by an INSERT /
/// UPDATE post-image / OPTIMIZE, possibly in a different process by a different engine, so the round trip is
/// the contract. And the malformed-value cases are reachable from SQL only through
/// <c>delta.set_tblproperties</c>, so covering them at the SQL tier would cost a service-tier round
/// trip each — while the whole class of "a garbage value must be REFUSED, whereas an unhonourable one must be
/// IGNORED" is decided entirely in this file.
/// </remarks>
public class DeltaParquetPropertiesTests
{
    private static Dictionary<string, string> Cfg(params (string Key, string Value)[] entries)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in entries) { d[k] = v; }
        return d;
    }

    // ── Silence ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_null_config_is_empty()
    {
        Assert.True(ParquetTuning.Parse(null).IsEmpty);
    }

    [Fact]
    public void Parse_config_without_our_keys_is_empty()
    {
        // The common case: a table carrying only Delta's own properties declares no tuning, and must cost
        // nothing — the caller materializes no write spec for it.
        var t = ParquetTuning.Parse(Cfg(
            ("delta.enableDeletionVectors", "true"),
            ("delta.enableChangeDataFeed", "true"),
            ("fabricator.sortedBy", "a,b")));
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void Render_of_empty_tuning_writes_nothing()
    {
        Assert.Empty(new ParquetTuning().Render());
    }

    [Fact]
    public void Absent_key_stays_null_rather_than_becoming_a_default()
    {
        // Load-bearing for precedence: null means "this layer is silent, keep what the layer below resolved".
        // A tuning that defaulted its absent keys would make every table override the session setting.
        var t = ParquetTuning.Parse(Cfg(("fabricator.parquet.compression", "zstd")));
        Assert.Equal("zstd", t.Compression);
        Assert.Null(t.RowGroupSize);
        Assert.Null(t.RowGroupSizeBytes);
        Assert.Null(t.ParquetVersion);
        Assert.Null(t.DictionarySizeLimit);
        Assert.Null(t.BloomFilterColumns);
        Assert.False(t.IsEmpty);
    }

    // ── Round trip ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Round_trip_preserves_every_knob()
    {
        var original = new ParquetTuning(
            Compression: "zstd",
            RowGroupSize: 100000,
            RowGroupSizeBytes: 134217728,
            ParquetVersion: "V2",
            DictionarySizeLimit: 4096,
            RowGroupsPerFile: 7,
            FileSizeBytes: 268435456,
            BloomFilterColumns: new[] { "id", "name" });

        var back = ParquetTuning.Parse(original.Render());

        Assert.Equal("zstd", back.Compression);
        Assert.Equal(100000, back.RowGroupSize);
        Assert.Equal(134217728L, back.RowGroupSizeBytes);
        Assert.Equal("V2", back.ParquetVersion);
        Assert.Equal(4096L, back.DictionarySizeLimit);
        Assert.Equal(7L, back.RowGroupsPerFile);
        Assert.Equal(268435456L, back.FileSizeBytes);
        Assert.Equal(new[] { "id", "name" }, back.BloomFilterColumns);
    }

    [Fact]
    public void Every_knob_persists_including_ones_no_engine_can_honour_today()
    {
        // ⚠ The deliberate departure from "never silently ignore a write option". A persisted property is a
        // DECLARATION ABOUT THE TABLE, read later by a writer that may be a different engine — so the format
        // must carry keys THIS engine cannot honour rather than dropping them at write time. The rotating
        // options in particular are refusable as a statement option today and still belong in the table's
        // declaration: when upstream lifts the limitation they start being honoured with no migration.
        var rendered = new ParquetTuning(
            DictionarySizeLimit: 4096,   // native-only
            RowGroupsPerFile: 7,         // honourable on NO path today
            FileSizeBytes: 268435456)    // honourable on NO path today
            .Render();

        Assert.Equal("4096", rendered["fabricator.parquet.dictionary_size_limit"]);
        Assert.Equal("7", rendered["fabricator.parquet.row_groups_per_file"]);
        Assert.Equal("268435456", rendered["fabricator.parquet.file_size_bytes"]);
    }

    [Fact]
    public void Absent_knobs_are_omitted_not_written_empty()
    {
        // An empty value would read back as a declaration of "" rather than as silence, which the
        // absent-key-stays-null contract above depends on.
        var rendered = new ParquetTuning(Compression: "snappy").Render();
        Assert.Single(rendered);
        Assert.True(rendered.ContainsKey("fabricator.parquet.compression"));
    }

    [Fact]
    public void Keys_all_live_under_the_fabricator_parquet_namespace()
    {
        // Under fabricator.* rather than delta.*: a foreign engine must be free to ignore these, and writing an
        // unrecognised key into the `delta.` namespace claims a protocol feature that does not exist.
        var rendered = new ParquetTuning(
            Compression: "zstd", RowGroupSize: 1, RowGroupSizeBytes: 2, ParquetVersion: "V1",
            DictionarySizeLimit: 3, RowGroupsPerFile: 4, FileSizeBytes: 5,
            BloomFilterColumns: new[] { "c" }).Render();

        Assert.Equal(8, rendered.Count);
        foreach (var key in rendered.Keys)
        {
            Assert.StartsWith("fabricator.parquet.", key, StringComparison.Ordinal);
        }
    }

    // ── Values written by hand ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bloom_filter_columns_split_and_trim()
    {
        var t = ParquetTuning.Parse(Cfg(("fabricator.parquet.bloom_filter_columns", " id , name ,, ")));
        Assert.Equal(new[] { "id", "name" }, t.BloomFilterColumns);
    }

    [Fact]
    public void Whitespace_only_value_reads_as_absent()
    {
        // set_tblproperties can write "" — treat it as unset rather than as a declaration of the empty string,
        // which is the only reading under which "unset this property" works from SQL.
        var t = ParquetTuning.Parse(Cfg(
            ("fabricator.parquet.compression", "   "),
            ("fabricator.parquet.bloom_filter_columns", "")));
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void Numeric_values_are_trimmed()
    {
        var t = ParquetTuning.Parse(Cfg(("fabricator.parquet.row_group_size", " 4096 ")));
        Assert.Equal(4096, t.RowGroupSize);
    }

    // ── Malformed: REFUSED, unlike merely unhonourable ────────────────────────────────────────────────

    [Theory]
    [InlineData("fabricator.parquet.row_group_size", "lots")]
    [InlineData("fabricator.parquet.row_group_size", "0")]
    [InlineData("fabricator.parquet.row_group_size", "-1")]
    [InlineData("fabricator.parquet.row_group_size", "4096.5")]
    [InlineData("fabricator.parquet.row_group_size_bytes", "128MB")]
    [InlineData("fabricator.parquet.dictionary_size_limit", "")]
    [InlineData("fabricator.parquet.row_groups_per_file", "many")]
    [InlineData("fabricator.parquet.file_size_bytes", "-5")]
    public void Malformed_numeric_value_throws_and_names_the_key(string key, string value)
    {
        if (value.Length == 0)
        {
            // The empty case is the exception: it reads as unset (covered above), so assert that rather than
            // pretending it throws.
            Assert.True(ParquetTuning.Parse(Cfg((key, value))).IsEmpty);
            return;
        }
        var ex = Assert.Throws<ArgumentException>(() => ParquetTuning.Parse(Cfg((key, value))));
        // The message must name the offending KEY: the value arrived via set_tblproperties, possibly long ago
        // and from another session, so "some property is wrong" would leave the user grepping their table.
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_value_is_refused_even_when_other_keys_are_fine()
    {
        // It must not degrade to "apply the good ones and skip the bad one" — that is the silent-swallow this
        // whole surface exists to avoid, and it is a DIFFERENT case from an unhonourable-but-well-formed key.
        Assert.Throws<ArgumentException>(() => ParquetTuning.Parse(Cfg(
            ("fabricator.parquet.compression", "zstd"),
            ("fabricator.parquet.row_group_size", "enormous"))));
    }

    [Fact]
    public void Compression_level_and_bloom_fpp_round_trip()
    {
        var back = ParquetTuning.Parse(
            new ParquetTuning(CompressionLevel: 19, BloomFilterFpp: 0.001).Render());
        Assert.Equal(19, back.CompressionLevel);
        Assert.Equal(0.001, back.BloomFilterFpp);
    }

    [Fact]
    public void Bloom_fpp_round_trips_through_scientific_notation()
    {
        // "R" renders 0.000001 as "1E-06", which the SQL suite really does write, so the read side must
        // accept an exponent. (It also has to survive DuckDB's own parser on the COPY, which the SQL gate
        // covers — this is the persisted half.)
        var rendered = new ParquetTuning(BloomFilterFpp: 0.000001).Render();
        Assert.Equal(0.000001, ParquetTuning.Parse(rendered).BloomFilterFpp);
    }

    [Fact]
    public void Bloom_fpp_renders_round_trippably_not_as_a_float_artefact()
    {
        // "R" rather than the default format: 0.001 must not come back as 0.0010000000000000000208, which
        // would still PARSE but would make the stored property unreadable to a human and unstable across
        // a re-render.
        Assert.Equal("0.001", new ParquetTuning(BloomFilterFpp: 0.001).Render()["fabricator.parquet.bloom_filter_false_positive_ratio"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-0.5")]
    [InlineData("1.5")]
    [InlineData("half")]
    public void Bloom_fpp_outside_zero_to_one_is_refused(string value)
    {
        // Both ends refused rather than clamped: 0 asks for a filter with no false positives (impossible),
        // 1 for one that matches everything (useless, and still costs bytes to write and read).
        var ex = Assert.Throws<ArgumentException>(() => ParquetTuning.Parse(
            Cfg(("fabricator.parquet.bloom_filter_false_positive_ratio", value))));
        Assert.Contains("bloom_filter_false_positive_ratio", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bloom_fpp_uses_the_invariant_culture()
    {
        // A stored property must not be read differently on a machine whose locale uses a decimal comma —
        // a "0,3" would otherwise parse as 3 on some hosts and be refused on others.
        Assert.Equal(0.3, ParquetTuning.Parse(
            Cfg(("fabricator.parquet.bloom_filter_false_positive_ratio", "0.3"))).BloomFilterFpp);
        Assert.Throws<ArgumentException>(() => ParquetTuning.Parse(
            Cfg(("fabricator.parquet.bloom_filter_false_positive_ratio", "0,3"))));
    }

    [Fact]
    public void Unknown_compression_and_version_names_pass_through_here()
    {
        // ⚠ Deliberate: this file does not own those vocabularies (the codec enum and the parquet-version enum
        // live on the engine side), so validating them here would duplicate the mapping and let the two
        // disagree. The engine layer rejects an unknown name; a bad value is not silently accepted, it is
        // simply refused one layer up.
        var t = ParquetTuning.Parse(Cfg(
            ("fabricator.parquet.compression", "brotli9000"),
            ("fabricator.parquet.version", "V7")));
        Assert.Equal("brotli9000", t.Compression);
        Assert.Equal("V7", t.ParquetVersion);
    }
}
