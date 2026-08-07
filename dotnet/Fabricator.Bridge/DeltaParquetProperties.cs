using System;
using System.Collections.Generic;
using System.Globalization;

namespace Fabricator.Bridge;

/// <summary>
/// The PERSISTED parquet write tuning of a Delta table — the <c>fabricator.parquet.*</c> table-configuration
/// keys — and the string form they take in the Delta <c>metaData.configuration</c> map.
/// </summary>
/// <remarks>
/// <para><b>Why a table property at all.</b> A statement's <c>WITH (parquet_compression='zstd')</c> applies to
/// that statement's write and nothing else, so a later plain <c>INSERT</c> — or an <c>OPTIMIZE</c> run by a
/// different catalog — silently reverted to the engine default and the table accumulated MIXED settings. Anyone
/// arriving from Iceberg/DuckLake reads "table property" as durable, and it is the difference between configuring
/// a dbt model once and re-stating the tuning on every incremental run.</para>
///
/// <para><b>⚠ A PERSISTED PROPERTY AND A STATEMENT OPTION ARE DIFFERENT KINDS OF THING, and the whole
/// ignore-vs-refuse asymmetry in this surface follows from the distinction rather than being an exception to
/// it.</b> A <c>WITH</c>/<c>SET</c> option is an ACTIVE REQUEST in THIS statement — "write it this way, now" — so
/// an engine that cannot honour it has left the user's intent unmet and the statement must FAIL (that is why
/// <c>ValidateSpecForEngine</c> throws). A persisted property is a DECLARATION ABOUT THE TABLE — "this is how
/// this table prefers to be written" — set once, by whoever created it, and read later by a writer that may
/// legitimately be a DIFFERENT ENGINE. Failing there would make the table UNWRITABLE by the codec engine merely
/// because someone once set a native-only knob. ⇒ every key persists; the reading engine applies the subset it
/// can honour and ignores the rest (naming what it dropped, at Debug — the choice is invisible from SQL
/// otherwise, and it is correct rather than degraded, so it is not a warning).</para>
///
/// <para><b>⚠ Ignoring a well-formed-but-unhonourable option is correct; swallowing an UNPARSEABLE one is
/// not.</b> <c>fabricator_delta_set_tblproperties</c> can write arbitrary <c>fabricator.*</c> keys, so someone
/// WILL hand-set a garbage value. <see cref="Parse"/> therefore THROWS on a malformed value, naming the key and
/// what was expected. That is not in tension with the paragraph above: the two cases are "this engine cannot do
/// what you asked" (ignore) and "nobody could do what you asked" (refuse).</para>
///
/// <para><b>Only the parquet FILE-FORMAT knobs live here.</b> Deliberately NOT persisted:
/// <c>partition_by</c> (the table's real partitioning is already in the Delta metadata — a second copy could
/// disagree with it), <c>replace_where</c> and <c>schema_mode</c> (per-statement SEMANTICS, not a storage
/// format: persisting "overwrite where p='x'" would make every later write destructive).</para>
///
/// <para>BCL-only by design — no Arrow, no engineered-wood, no DuckDB — so the round trip and every malformed
/// case are testable offline in <c>Fabricator.Bridge.Tests</c> (tier 0). The compression name and parquet
/// version stay STRINGS here for that reason; their enums live on the engine side.</para>
/// </remarks>
internal sealed record ParquetTuning(
    string? Compression = null,
    int? RowGroupSize = null,
    long? RowGroupSizeBytes = null,
    string? ParquetVersion = null,
    long? DictionarySizeLimit = null,
    long? RowGroupsPerFile = null,
    long? FileSizeBytes = null,
    IReadOnlyList<string>? BloomFilterColumns = null)
{
    /// <summary>The <c>fabricator.parquet.</c> namespace. Under <c>fabricator.*</c> rather than <c>delta.*</c>
    /// because these are OUR keys: a foreign engine must be free to ignore them, and writing an unrecognised
    /// key into the <c>delta.</c> namespace claims a protocol feature that does not exist.</summary>
    public const string Prefix = "fabricator.parquet.";

    public const string CompressionKey = Prefix + "compression";
    public const string RowGroupSizeKey = Prefix + "row_group_size";
    public const string RowGroupSizeBytesKey = Prefix + "row_group_size_bytes";
    public const string VersionKey = Prefix + "version";
    public const string DictionarySizeLimitKey = Prefix + "dictionary_size_limit";
    public const string RowGroupsPerFileKey = Prefix + "row_groups_per_file";
    public const string FileSizeBytesKey = Prefix + "file_size_bytes";
    public const string BloomFilterColumnsKey = Prefix + "bloom_filter_columns";

    /// <summary>True when the table declares no parquet tuning at all — the common case, and the one that must
    /// cost nothing (no spec is materialized for it).</summary>
    public bool IsEmpty
        => Compression is null && RowGroupSize is null && RowGroupSizeBytes is null && ParquetVersion is null
           && DictionarySizeLimit is null && RowGroupsPerFile is null && FileSizeBytes is null
           && BloomFilterColumns is not { Count: > 0 };

    /// <summary>Renders the declared knobs as Delta configuration entries (absent knobs are omitted, never
    /// written as an empty string — an empty value would read back as a declaration of "" rather than as
    /// silence).</summary>
    public IReadOnlyDictionary<string, string> Render()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Compression is { } c) { d[CompressionKey] = c; }
        if (RowGroupSize is { } rg) { d[RowGroupSizeKey] = rg.ToString(CultureInfo.InvariantCulture); }
        if (RowGroupSizeBytes is { } rgb) { d[RowGroupSizeBytesKey] = rgb.ToString(CultureInfo.InvariantCulture); }
        if (ParquetVersion is { } v) { d[VersionKey] = v; }
        if (DictionarySizeLimit is { } dsl) { d[DictionarySizeLimitKey] = dsl.ToString(CultureInfo.InvariantCulture); }
        if (RowGroupsPerFile is { } rgpf) { d[RowGroupsPerFileKey] = rgpf.ToString(CultureInfo.InvariantCulture); }
        if (FileSizeBytes is { } fsb) { d[FileSizeBytesKey] = fsb.ToString(CultureInfo.InvariantCulture); }
        if (BloomFilterColumns is { Count: > 0 } bloom) { d[BloomFilterColumnsKey] = string.Join(",", bloom); }
        return d;
    }

    /// <summary>Reads the <c>fabricator.parquet.*</c> keys out of a table's Delta configuration. A key that is
    /// absent stays null (the layer below keeps its value); a key whose value cannot be parsed THROWS.</summary>
    public static ParquetTuning Parse(IReadOnlyDictionary<string, string>? config)
    {
        if (config is null || config.Count == 0)
        {
            return new ParquetTuning();
        }
        return new ParquetTuning(
            Compression: Get(config, CompressionKey),
            RowGroupSize: ParseInt(config, RowGroupSizeKey),
            RowGroupSizeBytes: ParseLong(config, RowGroupSizeBytesKey),
            ParquetVersion: Get(config, VersionKey),
            DictionarySizeLimit: ParseLong(config, DictionarySizeLimitKey),
            RowGroupsPerFile: ParseLong(config, RowGroupsPerFileKey),
            FileSizeBytes: ParseLong(config, FileSizeBytesKey),
            BloomFilterColumns: ParseList(config, BloomFilterColumnsKey));
    }

    private static string? Get(IReadOnlyDictionary<string, string> config, string key)
        => config.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static int? ParseInt(IReadOnlyDictionary<string, string> config, string key)
    {
        if (Get(config, key) is not { } s) { return null; }
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v <= 0)
        {
            throw new ArgumentException(
                $"Delta table property \"{key}\" has the value '{s}', which is not a positive whole number. "
                + "Fix it with fabricator_delta_set_tblproperties, or unset it.");
        }
        return v;
    }

    private static long? ParseLong(IReadOnlyDictionary<string, string> config, string key)
    {
        if (Get(config, key) is not { } s) { return null; }
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v <= 0)
        {
            throw new ArgumentException(
                $"Delta table property \"{key}\" has the value '{s}', which is not a positive whole number. "
                + "Fix it with fabricator_delta_set_tblproperties, or unset it.");
        }
        return v;
    }

    private static IReadOnlyList<string>? ParseList(IReadOnlyDictionary<string, string> config, string key)
    {
        if (Get(config, key) is not { } s) { return null; }
        var list = new List<string>();
        foreach (var part in s.Split(','))
        {
            if (!string.IsNullOrWhiteSpace(part)) { list.Add(part.Trim()); }
        }
        return list.Count > 0 ? list : null;
    }
}
