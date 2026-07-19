using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Fabricator.Bridge;

/// <summary>
/// The Delta provider's view of a <c>CREATE TABLE [AS] ... WITH (key='value', ...)</c> options clause
/// (the flat JSON object the host passes on create_table/begin_bulk, ABI v67). Three kinds of keys:
///
/// <para><b>Write tuning (CTAS only)</b> — <c>parquet_compression</c>/<c>compression</c>,
/// <c>parquet_row_group_size</c>/<c>row_group_size</c>, <c>parquet_bloom_filter_columns</c>/
/// <c>bloom_filter_columns</c> (DuckLake-parity names + our bare aliases): applied to THIS statement's
/// write, winning over the <c>delta_write_options</c> session setting and the ATTACH defaults. Rejected
/// on an empty CREATE (there is no write to tune; persist a property or use the setting instead).</para>
///
/// <para><b>Per-table create-flag overrides</b> — <c>deletion_vectors</c>, <c>column_mapping</c>,
/// <c>row_tracking</c>, <c>change_data_feed</c>, <c>in_commit_timestamps</c>: the same values as the
/// ATTACH options, overriding the catalog default for THIS table's creation (e.g. the
/// protocol-1.0 recipe <c>WITH (deletion_vectors=false, column_mapping='none')</c> without a dedicated
/// ATTACH).</para>
///
/// <para><b>Table properties</b> — <c>delta.*</c> / <c>fabricator.*</c> keys (quote dotted keys in SQL:
/// <c>WITH ("delta.isolationLevel"='Serializable')</c>): stamped into the CREATE's table configuration
/// (one commit — no create-then-set_tblproperties two-step). Original key case is preserved (Delta
/// config keys are case-sensitive). Feature-enabling spellings are rejected with a pointer to the
/// explicit override keys (one spelling per feature, with the protocol-declaration wiring).</para>
///
/// Unknown keys are REJECTED — a WITH option is never silently ignored. <c>table_type='DELTA'</c> and
/// <c>format='parquet'</c> are accepted as validated no-ops (the Iceberg-style DDL shape).
/// </summary>
internal sealed record DeltaWithOptions
{
    public string? Compression { get; init; }
    public int? RowGroupSize { get; init; }
    public IReadOnlyList<string>? BloomFilterColumns { get; init; }

    public bool? DeletionVectors { get; init; }
    public EngineeredWood.DeltaLake.Schema.ColumnMappingMode? ColumnMapping { get; init; }
    public bool? RowTracking { get; init; }
    public bool? ChangeDataFeed { get; init; }
    public bool? InCommitTimestamps { get; init; }

    /// <summary>delta.* / fabricator.* extras, original key case, merged into the CREATE config LAST
    /// (a WITH property wins over a derived key, e.g. delta.isolationLevel).</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    public bool HasWriteTuning => Compression is not null || RowGroupSize is not null
                                  || BloomFilterColumns is not null;

    public bool HasCreateFlagOverride => DeletionVectors is not null || ColumnMapping is not null
                                         || RowTracking is not null || ChangeDataFeed is not null
                                         || InCommitTimestamps is not null;

    /// <summary>Parses + validates the WITH-options JSON; null for absent/empty. Throws a clear error for
    /// unknown keys, guarded keys, and malformed values.</summary>
    public static DeltaWithOptions? Parse(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return null;
        }
        string? compression = null;
        int? rowGroup = null;
        IReadOnlyList<string>? bloom = null;
        bool? dv = null, rt = null, cdf = null, ict = null;
        EngineeredWood.DeltaLake.Schema.ColumnMappingMode? cm = null;
        Dictionary<string, string>? props = null;

        using var doc = JsonDocument.Parse(optionsJson);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            string key = p.Name;
            string value = p.Value.ValueKind == JsonValueKind.String
                ? p.Value.GetString() ?? string.Empty
                : p.Value.ToString();
            switch (key.ToLowerInvariant())
            {
                case "parquet_compression":
                case "compression":
                    compression = value;
                    break;
                case "parquet_row_group_size":
                case "row_group_size":
                    rowGroup = ParseIntValue(key, value);
                    break;
                case "parquet_bloom_filter_columns":
                case "bloom_filter_columns":
                    bloom = SplitList(value);
                    break;
                case "deletion_vectors":
                    dv = ParseBoolValue(key, value);
                    break;
                case "row_tracking":
                    rt = ParseBoolValue(key, value);
                    break;
                case "change_data_feed":
                    cdf = ParseBoolValue(key, value);
                    break;
                case "in_commit_timestamps":
                    ict = ParseBoolValue(key, value);
                    break;
                case "column_mapping":
                    cm = value.Trim().ToLowerInvariant() switch
                    {
                        "id" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Id,
                        "name" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name,
                        "none" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None,
                        _ => throw new ArgumentException(
                            $"WITH column_mapping: unknown mode '{value}' (expected 'id', 'name', or 'none')."),
                    };
                    break;
                case "table_type":
                    // The Iceberg-style DDL shape: accepted as a validated no-op (this catalog IS Delta).
                    if (!value.Equals("delta", StringComparison.OrdinalIgnoreCase)
                        && !value.Equals("deltalake", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            $"WITH table_type: '{value}' is not supported by the delta provider "
                            + "(only 'DELTA'; ICEBERG has no writer).");
                    }
                    break;
                case "format":
                    if (!value.Equals("parquet", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            $"WITH format: '{value}' is not supported (Delta data files are parquet).");
                    }
                    break;
                case "location":
                    throw new ArgumentException(
                        "WITH location is not supported on a Delta catalog — the table's path IS its "
                        + "identity (<catalog root>/<schema>/<table>); ATTACH the desired root instead.");
                case "partitioned_by":
                case "partition_by":
                case "sorted_by":
                case "sort_by":
                    throw new ArgumentException(
                        $"WITH {key}: use the native clause instead — "
                        + "CREATE TABLE ... PARTITIONED BY (cols) / SORTED BY (cols).");
                default:
                    if (key.StartsWith("delta.", StringComparison.OrdinalIgnoreCase)
                        || key.StartsWith("fabricator.", StringComparison.OrdinalIgnoreCase))
                    {
                        GuardPropertyKey(key);
                        props ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        // DuckDB's parser LOWERCASES every WITH option key (transformer.cpp
                        // TransformTableOptions — quoting does not help), but Delta config keys are
                        // case-sensitive — re-case the well-known keys to their spec spelling. An unknown
                        // key stays as given (all-lowercase); for a case-sensitive CUSTOM key use
                        // fabricator_delta_set_tblproperties (its JSON preserves case).
                        props[CanonicalPropertyKey(key)] = value;
                        break;
                    }
                    throw new ArgumentException(
                        $"unknown CREATE TABLE WITH option '{key}' for the delta provider (supported: "
                        + "parquet_compression, parquet_row_group_size, parquet_bloom_filter_columns, "
                        + "deletion_vectors, column_mapping, row_tracking, change_data_feed, "
                        + "in_commit_timestamps, table_type, format, and quoted delta.*/fabricator.* "
                        + "table properties).");
            }
        }
        return new DeltaWithOptions
        {
            Compression = compression,
            RowGroupSize = rowGroup,
            BloomFilterColumns = bloom,
            DeletionVectors = dv,
            ColumnMapping = cm,
            RowTracking = rt,
            ChangeDataFeed = cdf,
            InCommitTimestamps = ict,
            Properties = props,
        };
    }

    // The canonical (spec-cased) spellings of the well-known Delta/fabricator table properties — the
    // WITH keys arrive lowercased (DuckDB parser), and Spark/Delta read config keys CASE-SENSITIVELY.
    private static readonly Dictionary<string, string> CanonicalKeys = BuildCanonicalKeys(
        "delta.isolationLevel",
        "delta.appendOnly",
        "delta.targetFileSize",
        "delta.checkpointInterval",
        "delta.logRetentionDuration",
        "delta.deletedFileRetentionDuration",
        "delta.setTransactionRetentionDuration",
        "delta.dataSkippingNumIndexedCols",
        "delta.dataSkippingStatsColumns",
        "delta.parquet.compression.codec",
        "delta.tuneFileSizesForRewrites",
        "fabricator.targetCubeSize");

    private static Dictionary<string, string> BuildCanonicalKeys(params string[] keys)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            map[k] = k;
        }
        return map;
    }

    private static string CanonicalPropertyKey(string key)
        => CanonicalKeys.TryGetValue(key, out var canonical) ? canonical : key;

    // One spelling per feature: the delta.enable*/delta.columnMapping.* property spellings need
    // protocol-declaration wiring the explicit WITH keys already have — reject with the pointer
    // (mirrors the fabricator_delta_set_tblproperties guard). fabricator.sortedBy has a native clause.
    private static void GuardPropertyKey(string key)
    {
        if (key.StartsWith("delta.enable", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"WITH \"{key}\": feature-enabling properties need a protocol declaration — use the "
                + "explicit WITH keys instead (deletion_vectors / row_tracking / change_data_feed / "
                + "in_commit_timestamps).");
        }
        if (key.StartsWith("delta.columnMapping", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"WITH \"{key}\": use the column_mapping WITH key instead ('id' / 'name' / 'none').");
        }
        if (key.StartsWith("delta.rowTracking.", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"WITH \"{key}\": the materialized row-tracking columns are declared automatically by "
                + "row_tracking=true (or the deletion_vectors default).");
        }
        if (key.Equals("fabricator.sortedBy", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "WITH \"fabricator.sortedBy\": use the native CREATE TABLE ... SORTED BY (cols) clause "
                + "(it also declares the liquid-clustering domain).");
        }
    }

    // 't'/'f': a bare `true`/`false` literal parses as CAST('t'/'f' AS BOOLEAN) (the postgres parser),
    // and the host's constant extraction unwraps one CAST level — so the boolean arrives as its postgres
    // text form.
    private static bool ParseBoolValue(string key, string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "t" or "1" => true,
        "false" or "f" or "0" => false,
        _ => throw new ArgumentException($"WITH {key}: expected a boolean (true/false), got '{value}'."),
    };

    private static int ParseIntValue(string key, string value)
    {
        if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out var n)
            || n < 1 || n > int.MaxValue)
        {
            throw new ArgumentException($"WITH {key}: expected a positive integer, got '{value}'.");
        }
        return (int)n;
    }

    private static IReadOnlyList<string> SplitList(string value)
    {
        var parts = new List<string>();
        foreach (var s in value.Split(','))
        {
            var t = s.Trim();
            if (t.Length > 0)
            {
                parts.Add(t);
            }
        }
        return parts;
    }
}
