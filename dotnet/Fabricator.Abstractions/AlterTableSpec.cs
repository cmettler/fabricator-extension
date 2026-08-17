using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Fabricator.Bridge;

/// <summary>Which ALTER TABLE variant an <see cref="AlterTableSpec"/> describes. The wire spelling is the
/// snake_case name listed on <c>table_alter</c> in abi.h; <see cref="AlterTableSpec.Parse"/> is the only
/// place the two vocabularies meet.</summary>
public enum AlterTableKind
{
    RenameTable,
    RenameColumn,
    AddColumn,
    DropColumn,
    ColumnType,
    SetNotNull,
    DropNotNull,
    SetDefault,
    DropDefault,
    /// <summary>Nested STRUCT-field evolution (<c>ALTER TABLE t ADD COLUMN s.f &lt;type&gt;</c>) — the path
    /// names the CONTAINING struct, the new field rides the type channel.</summary>
    AddField,
    DropField,
    RenameField,
    SetSortedBy,
    SetPartitionedBy,
}

/// <summary>
/// One ALTER TABLE request, parsed from the <c>table_alter</c> JSON doc (ABI v74). It replaces the old
/// <c>alter_table</c> entry's <c>alterKind</c> int plus <c>arg1</c>/<c>arg2</c>/<c>flags</c>, where every
/// carrier meant something different per kind — a column name in one, a JSON path in another, a
/// base64-with-a-sentinel-prefix default literal in a third — so no reader could tell from a signature what
/// any of them held.
/// </summary>
/// <remarks>
/// <para>The doc names its variant and carries ONLY that variant's fields, so an absent property here is
/// genuinely "this kind has none" rather than "not filled in". The <c>Require*</c> accessors exist for the
/// fields a given kind promises: they turn a malformed doc into one clear message at the point of use
/// instead of a per-provider null check (there were fourteen of those across three providers).</para>
/// <para><b>The new column/field TYPE is not here.</b> It travels beside the doc as an Arrow field, because
/// a VARIANT column is identified by Arrow field METADATA (<c>ew.variant_transport</c>) that a type NAME
/// cannot carry — see the type-channel note on <c>table_alter</c>.</para>
/// <para>BCL-only by design (System.Text.Json and collections), so the tier-0 test project can link this
/// file directly and gate the parse offline.</para>
/// </remarks>
public sealed class AlterTableSpec
{
    /// <summary>Which variant this is. Every other property is meaningful only for the kinds that define it.</summary>
    public required AlterTableKind Kind { get; init; }

    /// <summary>The target column's name (the top-level column kinds); null for the kinds that address a
    /// nested field via <see cref="Path"/>, a whole table, or a column list.</summary>
    public string? Column { get; init; }

    /// <summary>The rename target — a table, column or field name; null for every non-rename kind.</summary>
    public string? NewName { get; init; }

    /// <summary>A nested field's path as SEGMENTS (<c>["s","inner","f"]</c>) — a field name may contain
    /// dots, so a joined string would be ambiguous. For <see cref="AlterTableKind.AddField"/> this is the
    /// CONTAINING struct's path; for the others the field's own. Null for the non-nested kinds.</summary>
    public IReadOnlyList<string>? Path { get; init; }

    /// <summary>The column list of SET SORTED BY / SET PARTITIONED BY. An EMPTY list is meaningful — it is
    /// the RESET spelling — so this is null (not empty) for every other kind.</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>The statement's if-(not-)exists guard, false when it carried none. Deliberately ONE property
    /// although the wire has two honest keys (<c>if_not_exists</c> on the ADD kinds, <c>if_exists</c> on the
    /// DROP kinds): the doc must be unambiguous to read, while every use site wants the one question "was
    /// this guarded?" — and a single property is one a provider cannot answer from the wrong key.</summary>
    public bool Guard { get; init; }

    /// <summary>SET DEFAULT's literal as TEXT, or null for <c>DEFAULT NULL</c>. The <c>default</c> key is
    /// REQUIRED by that kind, so null here is the NULL default and never "unset" — the distinction the old
    /// <c>arg2</c> spelled <c>"-"</c> vs <c>"b"</c>+base64(text), a hack that existed only because a C
    /// string cannot tell empty from absent.</summary>
    public string? DefaultLiteral { get; init; }

    /// <summary>The target column's name, or a clear error when the doc omitted it.</summary>
    public string RequireColumn() => Column ?? throw MissingField("column");

    /// <summary>The rename target, or a clear error when the doc omitted it.</summary>
    public string RequireNewName() => NewName ?? throw MissingField("new_name");

    /// <summary>The nested field path, or a clear error when the doc omitted it / sent it empty (a path
    /// always has at least one segment).</summary>
    public IReadOnlyList<string> RequirePath() =>
        Path is { Count: > 0 } path ? path : throw MissingField("path");

    /// <summary>The column list, or a clear error when the doc omitted it. EMPTY is a valid answer (RESET).</summary>
    public IReadOnlyList<string> RequireColumns() => Columns ?? throw MissingField("columns");

    private InvalidOperationException MissingField(string field) =>
        new($"fabricator: ALTER TABLE ({WireName(Kind)}) is missing its '{field}'.");

    /// <summary>Parses a <c>table_alter</c> doc. Throws on a doc this build cannot act on — an unknown or
    /// absent kind, or a SET DEFAULT with no <c>default</c> key — rather than degrading, because every one
    /// of those means the host and the bridge disagree about the contract and the safe move is to refuse
    /// the DDL, not to guess at it.</summary>
    public static AlterTableSpec Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("fabricator: ALTER TABLE request is not a JSON object.");
        }
        if (!root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("fabricator: ALTER TABLE request has no 'kind'.");
        }
        string wire = kindElement.GetString()!;
        var kind = ParseKind(wire);

        string? defaultLiteral = null;
        if (kind == AlterTableKind.SetDefault)
        {
            if (!root.TryGetProperty("default", out var defaultElement))
            {
                throw new InvalidOperationException(
                    "fabricator: ALTER TABLE (set_default) is missing its 'default' (use JSON null for DEFAULT NULL).");
            }
            defaultLiteral = defaultElement.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => defaultElement.GetString(),
                _ => throw new InvalidOperationException(
                    "fabricator: ALTER TABLE (set_default) 'default' must be a string or null."),
            };
        }

        return new AlterTableSpec
        {
            Kind = kind,
            Column = ReadString(root, "column"),
            NewName = ReadString(root, "new_name"),
            Path = ReadStringList(root, "path"),
            Columns = ReadStringList(root, "columns"),
            // The kind fixes which spelling is present, so reading both is not ambiguity — it is the one
            // place that knows the mapping, keeping every consumer on a single question.
            Guard = ReadBool(root, "if_not_exists") || ReadBool(root, "if_exists"),
            DefaultLiteral = defaultLiteral,
        };
    }

    /// <summary>The wire spelling of a kind — the inverse of <see cref="ParseKind"/>, used in messages so an
    /// error names what the host actually sent.</summary>
    public static string WireName(AlterTableKind kind) => kind switch
    {
        AlterTableKind.RenameTable => "rename_table",
        AlterTableKind.RenameColumn => "rename_column",
        AlterTableKind.AddColumn => "add_column",
        AlterTableKind.DropColumn => "drop_column",
        AlterTableKind.ColumnType => "column_type",
        AlterTableKind.SetNotNull => "set_not_null",
        AlterTableKind.DropNotNull => "drop_not_null",
        AlterTableKind.SetDefault => "set_default",
        AlterTableKind.DropDefault => "drop_default",
        AlterTableKind.AddField => "add_field",
        AlterTableKind.DropField => "drop_field",
        AlterTableKind.RenameField => "rename_field",
        AlterTableKind.SetSortedBy => "set_sorted_by",
        AlterTableKind.SetPartitionedBy => "set_partitioned_by",
        _ => kind.ToString(),
    };

    private static AlterTableKind ParseKind(string wire) => wire switch
    {
        "rename_table" => AlterTableKind.RenameTable,
        "rename_column" => AlterTableKind.RenameColumn,
        "add_column" => AlterTableKind.AddColumn,
        "drop_column" => AlterTableKind.DropColumn,
        "column_type" => AlterTableKind.ColumnType,
        "set_not_null" => AlterTableKind.SetNotNull,
        "drop_not_null" => AlterTableKind.DropNotNull,
        "set_default" => AlterTableKind.SetDefault,
        "drop_default" => AlterTableKind.DropDefault,
        "add_field" => AlterTableKind.AddField,
        "drop_field" => AlterTableKind.DropField,
        "rename_field" => AlterTableKind.RenameField,
        "set_sorted_by" => AlterTableKind.SetSortedBy,
        "set_partitioned_by" => AlterTableKind.SetPartitionedBy,
        _ => throw new InvalidOperationException($"fabricator: unknown ALTER TABLE kind '{wire}'."),
    };

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string>? ReadStringList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var items = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"fabricator: ALTER TABLE '{name}' must be an array of strings.");
            }
            items.Add(item.GetString()!);
        }
        return items;
    }
}
