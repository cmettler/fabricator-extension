// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Data.Common;
using System.Globalization;

namespace Fabricator.Bridge.Conversion;

/// <summary>
/// The column's declared SQL Server type, carried on its Arrow field as metadata — the parts the Arrow type
/// itself cannot hold: <c>datetime2(3)</c> and <c>datetime2(6)</c> are both a microsecond timestamp, <c>char(5)</c>
/// and <c>varchar(max)</c> both a string. Whoever needs the exact type (what the server will store for a value,
/// the DDL to recreate the column) reads it from here instead of asking the server again.
/// <list type="bullet">
/// <item><c>fabricator.sql.type</c> — the type name as the server reports it, lower case (<c>datetime2</c>).</item>
/// <item><c>fabricator.sql.length</c> — characters (n-types) or bytes of a char/binary column; <c>-1</c> for
/// <c>max</c>.</item>
/// <item><c>fabricator.sql.precision</c> — decimal/numeric precision.</item>
/// <item><c>fabricator.sql.scale</c> — decimal/numeric scale, or the fractional-second digits of
/// time/datetime2/datetimeoffset/datetime.</item>
/// </list>
/// Absent keys mean "not reported", never a default. DuckDB ignores these keys on import.
/// </summary>
public static class SqlTypeMetadata
{
    public const string TypeKey = "fabricator.sql.type";
    public const string LengthKey = "fabricator.sql.length";
    public const string PrecisionKey = "fabricator.sql.precision";
    public const string ScaleKey = "fabricator.sql.scale";

    /// <summary>The metadata of one result column (a fresh, mutable dictionary; empty when nothing is known).</summary>
    public static Dictionary<string, string> Describe(DbColumn col)
    {
        var md = new Dictionary<string, string>();
        var sql = (col.DataTypeName ?? string.Empty).ToLowerInvariant();
        if (sql.Length == 0)
        {
            return md;
        }
        md[TypeKey] = sql;
        switch (sql)
        {
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
            case "binary":
            case "varbinary":
                if (col.IsLong == true)
                {
                    md[LengthKey] = "-1";
                }
                else if (col.ColumnSize is { } n && n > 0)
                {
                    md[LengthKey] = n.ToString(CultureInfo.InvariantCulture);
                }
                break;
            case "decimal":
            case "numeric":
                if (col.NumericPrecision is { } p && p > 0)
                {
                    md[PrecisionKey] = p.ToString(CultureInfo.InvariantCulture);
                }
                if (col.NumericScale is { } s)
                {
                    md[ScaleKey] = s.ToString(CultureInfo.InvariantCulture);
                }
                break;
            case "time":
            case "datetime2":
            case "datetimeoffset":
            case "datetime":
            case "smalldatetime":
                if (col.NumericScale is { } f)
                {
                    md[ScaleKey] = f.ToString(CultureInfo.InvariantCulture);
                }
                break;
        }
        return md;
    }

    // The readers take the field's metadata (Field.Metadata), so this file stays free of Apache.Arrow and the
    // offline test tier can compile it.
    public static string? Type(IReadOnlyDictionary<string, string>? metadata) => Get(metadata, TypeKey);

    public static int? Length(IReadOnlyDictionary<string, string>? metadata) => Int(metadata, LengthKey);

    public static int? Precision(IReadOnlyDictionary<string, string>? metadata) => Int(metadata, PrecisionKey);

    public static int? Scale(IReadOnlyDictionary<string, string>? metadata) => Int(metadata, ScaleKey);

    private static string? Get(IReadOnlyDictionary<string, string>? metadata, string key)
        => metadata is not null && metadata.TryGetValue(key, out var v) ? v : null;

    private static int? Int(IReadOnlyDictionary<string, string>? metadata, string key)
        => Get(metadata, key) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;
}
