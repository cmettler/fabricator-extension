// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow.Types;

namespace Fabricator.AnalysisServices;

/// <summary>
/// Maps the CLR types reported by <c>AdomdDataReader.GetSchemaTable()</c> (the DAX engine's result-column
/// types) to Arrow types. We resolve schemas by running a zero-row DAX query and reading its schema table
/// (the same no-describe approach the DAX functions use), so the engine itself tells us the types — no
/// guessing of TOM <c>DataType</c> enum integers. See docs/dax-provider.md.
/// </summary>
internal static class DaxTypeMap
{
    /// <summary>Maps a result column's CLR type (+ optional numeric precision/scale) to an Arrow type.</summary>
    public static IArrowType MapClr(Type clr, int? precision, int? scale) => clr.Name switch
    {
        "Boolean" => BooleanType.Default,
        "Byte" or "SByte" or "Int16" or "UInt16" => Int16Type.Default,
        "Int32" or "UInt32" => Int32Type.Default,
        "Int64" or "UInt64" => Int64Type.Default,
        "Single" => FloatType.Default,
        "Double" => DoubleType.Default,
        // DAX "Fixed Decimal"/Currency surfaces as Decimal (scale 4). Keep it exact when precision is known,
        // else fall back to DOUBLE rather than fabricate a precision.
        "Decimal" when precision is > 0 and <= 38 => new Decimal128Type(precision.Value, scale is >= 0 ? scale.Value : 4),
        "Decimal" => DoubleType.Default,
        "DateTime" => new TimestampType(TimeUnit.Millisecond, (string?)null),
        "DateTimeOffset" => new TimestampType(TimeUnit.Millisecond, TimeZoneInfo.Utc),
        "Guid" => StringType.Default,
        _ => StringType.Default,
    };

    /// <summary>
    /// DAX result columns come back bracket-qualified (<c>'Table'[Col]</c> from <c>EVALUATE 'Table'</c>,
    /// <c>[Col]</c> from <c>SELECTCOLUMNS</c>). Strip to the bare name inside the last <c>[...]</c> so DuckDB
    /// column names are clean (and so discovery names match what a <c>SELECTCOLUMNS</c> scan aliases them to).
    /// </summary>
    public static string DebracketColumn(string name)
    {
        int open = name.LastIndexOf('[');
        int close = name.LastIndexOf(']');
        if (open >= 0 && close > open)
        {
            return name.Substring(open + 1, close - open - 1);
        }
        return name;
    }
}
