// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Data.Common;
using Fabricator.Bridge.Conversion;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// Offline tests for <see cref="SqlTypeMetadata"/>: the declared SQL Server type carried on an Arrow field
/// (SqlArrowMapping.ToArrowField puts this dictionary on the field). The
/// <see cref="DbColumn"/> values mirror what SqlClient's <c>GetColumnSchema</c> reports (ColumnSize in characters
/// for n-types, IsLong for <c>max</c>, NumericScale as the fractional-second digits of the date/time types).
/// </summary>
public class SqlTypeMetadataTests
{
    private sealed class Col : DbColumn
    {
        public Col(string name, string type, int? size = null, int? precision = null, int? scale = null,
                   bool isLong = false)
        {
            ColumnName = name;
            DataTypeName = type;
            ColumnSize = size;
            NumericPrecision = precision;
            NumericScale = scale;
            IsLong = isLong;
            AllowDBNull = true;
        }
    }

    [Fact]
    public void Datetime2_keeps_its_fractional_digits()
    {
        var f = SqlTypeMetadata.Describe(new Col("d", "datetime2", precision: 23, scale: 3));
        Assert.Equal("datetime2", SqlTypeMetadata.Type(f));
        Assert.Equal(3, SqlTypeMetadata.Scale(f));
        Assert.Null(SqlTypeMetadata.Precision(f));
        Assert.Null(SqlTypeMetadata.Length(f));
    }

    [Fact]
    public void Char_reports_its_length_and_max_reports_minus_one()
    {
        var c = SqlTypeMetadata.Describe(new Col("c", "char", size: 5));
        Assert.Equal(5, SqlTypeMetadata.Length(c));
        var m = SqlTypeMetadata.Describe(new Col("m", "nvarchar", size: int.MaxValue, isLong: true));
        Assert.Equal(-1, SqlTypeMetadata.Length(m));
    }

    [Fact]
    public void Decimal_reports_precision_and_scale()
    {
        var f = SqlTypeMetadata.Describe(new Col("x", "DECIMAL", precision: 10, scale: 2));
        Assert.Equal("decimal", SqlTypeMetadata.Type(f));
        Assert.Equal(10, SqlTypeMetadata.Precision(f));
        Assert.Equal(2, SqlTypeMetadata.Scale(f));
    }

    [Fact]
    public void Unreported_parts_stay_absent()
    {
        var f = SqlTypeMetadata.Describe(new Col("i", "int", size: 4, precision: 10, scale: 0));
        Assert.Equal("int", SqlTypeMetadata.Type(f));
        Assert.Null(SqlTypeMetadata.Length(f));
        Assert.Null(SqlTypeMetadata.Scale(f));
        var none = SqlTypeMetadata.Describe(new Col("n", ""));
        Assert.Empty(none);
    }
}
