// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Adbc;

/// <summary>
/// DuckDB DDL for a DuckDB server, from the Arrow schema the host hands over. The host sends it in DuckDB's LOSSLESS
/// Arrow forms (capability lossless_arrow_boundary), so every DuckDB type comes back as itself: HUGEINT, UUID, TIME_TZ,
/// BIT … as their extension types, BOOLEAN as arrow.bool8. What has no exact DuckDB type is refused.
/// </summary>
internal static class DuckDbDdl
{
    private const string ExtensionName = "ARROW:extension:name";
    private const string ExtensionMetadata = "ARROW:extension:metadata";

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string Table(string schema, string table) =>
        (string.IsNullOrEmpty(schema) ? "" : Quote(schema) + ".") + Quote(table);

    /// <summary>
    /// CREATE TABLE. <paramref name="primaryKey"/>: comma-separated column indexes; <paramref name="uniques"/>: such
    /// groups separated by ';' (the host's encoding).
    /// </summary>
    public static string CreateTable(string schema, string table, Schema columns, bool ifNotExists, bool replace,
                                     string? primaryKey, string? uniques)
    {
        var sql = new StringBuilder(replace ? "CREATE OR REPLACE TABLE " : ifNotExists ? "CREATE TABLE IF NOT EXISTS " : "CREATE TABLE ");
        sql.Append(Table(schema, table)).Append(" (");
        sql.Append(string.Join(", ", columns.FieldsList.Select(f => Quote(f.Name) + " " + TypeName(f) + (f.IsNullable ? "" : " NOT NULL"))));
        if (!string.IsNullOrEmpty(primaryKey))
            sql.Append(", PRIMARY KEY (").Append(ColumnList(columns, primaryKey)).Append(')');
        foreach (var group in (uniques ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            sql.Append(", UNIQUE (").Append(ColumnList(columns, group)).Append(')');
        return sql.Append(')').ToString();
    }

    /// <summary>ALTER TABLE / COMMENT ON. <paramref name="column"/> is the new column or field type (ADD, TYPE).</summary>
    public static string Alter(Fabricator.Bridge.AlterTableSpec spec, string schema, string table, Field? column)
    {
        string target = Table(schema, table);
        string alter = "ALTER TABLE " + target + " ";
        string Path(IReadOnlyList<string> path) => string.Join(".", path.Select(Quote));
        string Type() => TypeName(column ?? throw new InvalidOperationException("adbc: ALTER without the new type"));
        string Literal(string? text) => text is null ? "NULL" : "'" + text.Replace("'", "''") + "'";
        return spec.Kind switch
        {
            Fabricator.Bridge.AlterTableKind.RenameTable => alter + "RENAME TO " + Quote(spec.RequireNewName()),
            Fabricator.Bridge.AlterTableKind.RenameColumn =>
                alter + "RENAME COLUMN " + Quote(spec.RequireColumn()) + " TO " + Quote(spec.RequireNewName()),
            Fabricator.Bridge.AlterTableKind.AddColumn =>
                alter + "ADD COLUMN " + (spec.Guard ? "IF NOT EXISTS " : "") + Quote(spec.RequireColumn()) + " " + Type()
                + (spec.HasDefault ? " DEFAULT " + TypedLiteral(spec.DefaultLiteral, column) : ""),
            Fabricator.Bridge.AlterTableKind.DropColumn =>
                alter + "DROP COLUMN " + (spec.Guard ? "IF EXISTS " : "") + Quote(spec.RequireColumn()),
            Fabricator.Bridge.AlterTableKind.ColumnType =>
                alter + "ALTER COLUMN " + Quote(spec.RequireColumn()) + " TYPE " + Type(),
            Fabricator.Bridge.AlterTableKind.SetNotNull => alter + "ALTER COLUMN " + Quote(spec.RequireColumn()) + " SET NOT NULL",
            Fabricator.Bridge.AlterTableKind.DropNotNull => alter + "ALTER COLUMN " + Quote(spec.RequireColumn()) + " DROP NOT NULL",
            Fabricator.Bridge.AlterTableKind.SetDefault =>
                alter + "ALTER COLUMN " + Quote(spec.RequireColumn()) + " SET DEFAULT " + TypedLiteral(spec.DefaultLiteral, column),
            Fabricator.Bridge.AlterTableKind.DropDefault => alter + "ALTER COLUMN " + Quote(spec.RequireColumn()) + " DROP DEFAULT",
            Fabricator.Bridge.AlterTableKind.AddField =>
                alter + "ADD COLUMN " + (spec.Guard ? "IF NOT EXISTS " : "") + Path(spec.RequirePath()) + "." + Quote(column!.Name) + " " + Type(),
            Fabricator.Bridge.AlterTableKind.DropField =>
                alter + "DROP COLUMN " + (spec.Guard ? "IF EXISTS " : "") + Path(spec.RequirePath()),
            Fabricator.Bridge.AlterTableKind.RenameField =>
                alter + "RENAME COLUMN " + Path(spec.RequirePath()) + " TO " + Quote(spec.RequireNewName()),
            Fabricator.Bridge.AlterTableKind.SetComment => "COMMENT ON TABLE " + target + " IS " + Literal(spec.Comment),
            Fabricator.Bridge.AlterTableKind.SetColumnComment =>
                "COMMENT ON COLUMN " + target + "." + Quote(spec.RequireColumn()) + " IS " + Literal(spec.Comment),
            _ => throw new NotSupportedException($"adbc: ALTER TABLE {spec.Kind} is not supported"),
        };
    }

    /// <summary>A default's text as a literal: a number or a boolean as itself when the column's type is one (DuckDB
    /// then stores the default as the client does), anything else as a string DuckDB casts to the column type.</summary>
    private static string TypedLiteral(string? text, Field? column)
    {
        if (text is null)
            return "NULL";
        string type = column is null ? "" : TypeName(column);
        bool numeric = type is "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "HUGEINT" or "UTINYINT" or "USMALLINT"
            or "UINTEGER" or "UBIGINT" or "UHUGEINT" or "FLOAT" or "DOUBLE" || type.StartsWith("DECIMAL", StringComparison.Ordinal);
        if (numeric && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return text;
        if (type == "BOOLEAN" && (text == "true" || text == "false"))
            return text;
        return "'" + text.Replace("'", "''") + "'";
    }

    private static string ColumnList(Schema columns, string indexes) =>
        string.Join(", ", indexes.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(i => Quote(columns.FieldsList[int.Parse(i, CultureInfo.InvariantCulture)].Name)));

    /// <summary>The DuckDB type of an Arrow field in DuckDB's lossless forms.</summary>
    public static string TypeName(Field field)
    {
        if (field.Metadata is { } metadata && metadata.TryGetValue(ExtensionName, out var extension))
        {
            switch (extension)
            {
                case "arrow.uuid":
                    return "UUID";
                case "arrow.bool8":
                    return "BOOLEAN";
                case "arrow.json":
                    return "JSON";
                case "geoarrow.wkb":
                    return "GEOMETRY";
                case "arrow.opaque":
                {
                    string? name = null, vendor = null;
                    if (metadata.TryGetValue(ExtensionMetadata, out var json) && !string.IsNullOrEmpty(json))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("type_name", out var t))
                            name = t.GetString();
                        if (doc.RootElement.TryGetProperty("vendor_name", out var v))
                            vendor = v.GetString();
                    }
                    if (vendor == "DuckDB")
                    {
                        string? duck = name switch
                        {
                            "hugeint" => "HUGEINT",
                            "uhugeint" => "UHUGEINT",
                            "time_tz" => "TIMETZ",
                            "bit" => "BIT",
                            "bignum" or "varint" => "BIGNUM",
                            _ => null,
                        };
                        if (duck != null)
                            return duck;
                    }
                    throw new NotSupportedException($"adbc: column '{field.Name}': opaque type {name ?? "?"} of {vendor ?? "?"}");
                }
                default:
                    throw new NotSupportedException($"adbc: column '{field.Name}': extension type {extension}");
            }
        }
        return TypeName(field.DataType, field.Name);
    }

    private static string TypeName(IArrowType type, string column) => type switch
    {
        BooleanType => "BOOLEAN",
        Int8Type => "TINYINT",
        Int16Type => "SMALLINT",
        Int32Type => "INTEGER",
        Int64Type => "BIGINT",
        UInt8Type => "UTINYINT",
        UInt16Type => "USMALLINT",
        UInt32Type => "UINTEGER",
        UInt64Type => "UBIGINT",
        FloatType => "FLOAT",
        DoubleType => "DOUBLE",
        Decimal128Type d => $"DECIMAL({d.Precision},{d.Scale})",
        StringType or LargeStringType or StringViewType => "VARCHAR",
        BinaryType or LargeBinaryType or BinaryViewType => "BLOB",
        Date32Type => "DATE",
        Time64Type { Unit: TimeUnit.Microsecond } => "TIME",
        Time64Type { Unit: TimeUnit.Nanosecond } => "TIME_NS",
        TimestampType { Timezone: null or "" } t => t.Unit switch
        {
            TimeUnit.Second => "TIMESTAMP_S",
            TimeUnit.Millisecond => "TIMESTAMP_MS",
            TimeUnit.Microsecond => "TIMESTAMP",
            TimeUnit.Nanosecond => "TIMESTAMP_NS",
            _ => throw Refused(column, type),
        },
        TimestampType { Unit: TimeUnit.Microsecond } => "TIMESTAMPTZ",
        IntervalType { Unit: IntervalUnit.MonthDayNanosecond } => "INTERVAL",
        ListType l => TypeName(l.ValueField) + "[]",
        LargeListType l => TypeName(l.ValueField) + "[]",
        FixedSizeListType l => TypeName(l.ValueField) + "[" + l.ListSize.ToString(CultureInfo.InvariantCulture) + "]",
        MapType m => "MAP(" + TypeName(m.KeyField) + ", " + TypeName(m.ValueField) + ")",
        StructType s => "STRUCT(" + string.Join(", ", s.Fields.Select(f => Quote(f.Name) + " " + TypeName(f))) + ")",
        UnionType u => "UNION(" + string.Join(", ", u.Fields.Select(f => Quote(f.Name) + " " + TypeName(f))) + ")",
        _ => throw Refused(column, type),
    };

    private static NotSupportedException Refused(string column, IArrowType type) =>
        new($"adbc: column '{column}': no DuckDB type for Arrow {type.Name}");
}
