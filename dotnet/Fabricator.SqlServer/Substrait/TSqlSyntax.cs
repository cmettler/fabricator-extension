// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;

namespace Clast.Drivers.SqlServer.Substrait;

/// <summary>What a SQL expression contains, as far as T-SQL's placement rules care.</summary>
[Flags]
internal enum SqlProps
{
    None = 0,
    /// <summary>Refers to a column (so it can be a GROUP BY or ORDER BY item).</summary>
    Column = 1,
    /// <summary>Contains a subquery (not allowed in GROUP BY or inside an aggregate).</summary>
    Subquery = 2,
    /// <summary>Contains an aggregate function.</summary>
    Aggregate = 4,
    /// <summary>Refers to a column of an enclosing query (a correlated subquery's outer reference).</summary>
    Outer = 8,
}

/// <summary>
/// A T-SQL expression. T-SQL has no boolean values: a condition is a
/// predicate (<c>a = b</c>, <c>EXISTS (...)</c>), and a stored boolean is a
/// <c>bit</c>. A boolean expression keeps whichever form it was written in and
/// converts to the other on demand, so <see cref="Value"/> and
/// <see cref="Predicate"/> are both always available for booleans.
/// </summary>
internal sealed class SqlExpr
{
    private string? _value;
    private readonly string? _predicate;

    public SqlExpr(string? value, string? predicate, SType type, SqlProps props)
    {
        _value = value;
        _predicate = predicate;
        Type = type;
        Props = props;
    }

    public static SqlExpr Of(string value, SType type, SqlProps props) => new(value, null, type, props);

    public SqlExpr With(SType type, SqlProps props) => new(_value, _predicate, type, props);

    public static SqlExpr Condition(string predicate, SqlProps props) => new(null, predicate, SType.Bool, props);

    public SType Type { get; }

    public SqlProps Props { get; }

    /// <summary>The expression as a value; for a condition, a nullable bit (three-valued).</summary>
    public string Value => _value ??=
        $"CASE WHEN {_predicate} THEN CAST(1 AS bit) WHEN NOT {_predicate} THEN CAST(0 AS bit) END";

    /// <summary>The expression as a condition; only meaningful for booleans.</summary>
    public string Predicate => _predicate ?? $"({_value} = 1)";

    /// <summary>True when the expression was written as a condition rather than a value.</summary>
    public bool IsPredicate => _predicate != null;
}

/// <summary>A column of a relation's output, as visible to Substrait field references.</summary>
internal sealed class SqlColumn
{
    public SqlColumn(string name, SqlExpr expr, bool isBare, string? raw = null)
    {
        Name = name;
        Expr = expr;
        IsBare = isBare;
        Raw = raw;
    }

    /// <summary>The name to give the column where a select list must name it.</summary>
    public string Name { get; }

    /// <summary>The column's value, in terms of the FROM clause of the block it belongs to.</summary>
    public SqlExpr Expr { get; }

    public SType Type => Expr.Type;

    /// <summary>
    /// True when the expression is a plain column reference, which is null when
    /// the row it comes from is missing. Only such columns can be the
    /// null-supplying side of an outer join without a derived table.
    /// </summary>
    public bool IsBare { get; }

    /// <summary>
    /// For a bare string column that <see cref="Expr"/> gives the query's
    /// collation: the column reference without it, to use in select lists.
    /// </summary>
    public string? Raw { get; }

    public SqlColumn WithType(SType type) => new(Name, Expr.IsPredicate
        ? new SqlExpr(null, Expr.Predicate, type, Expr.Props)
        : SqlExpr.Of(Expr.Value, type, Expr.Props), IsBare, Raw);
}

/// <summary>An ORDER BY item: an expression and a direction.</summary>
internal readonly struct OrderItem
{
    public OrderItem(string sql, bool descending)
    {
        Sql = sql;
        Descending = descending;
    }

    public string Sql { get; }

    public bool Descending { get; }

    public override string ToString() => Descending ? Sql + " DESC" : Sql;
}

/// <summary>
/// One SELECT statement under construction. Relations are added to the
/// current block while T-SQL's clause order allows it (for example, a filter
/// becomes WHERE unless the block already groups or limits rows); otherwise
/// the block becomes a derived table in the FROM clause of a new block.
/// </summary>
internal sealed class SelectBlock
{
    /// <summary>The FROM clause, without the keyword; empty for a SELECT without FROM.</summary>
    public string From = "";
    /// <summary>True when <see cref="From"/> is a join, which needs parentheses as the right side of another join.</summary>
    public bool FromIsJoin;
    public List<SqlColumn> Columns = new();
    public List<string> Where = new();
    /// <summary>The GROUP BY clause, without the keywords.</summary>
    public string? GroupBy;
    /// <summary>True when the columns are aggregates or grouping keys (a GROUP BY or a global aggregate).</summary>
    public bool Aggregated;
    public bool Distinct;
    public List<OrderItem> OrderBy = new();
    public string? Top;
    public bool WithTies;
    public string? Offset;
    public string? Fetch;

    public bool HasFetch => Top != null || Offset != null || Fetch != null;

    /// <summary>True when a WHERE condition can be added (it applies before grouping and row limits).</summary>
    public bool CanFilter => !Aggregated && !Distinct && !HasFetch;

    /// <summary>True when the block can be joined, or aggregated, as it stands.</summary>
    public bool IsPlain => !Aggregated && !Distinct && !HasFetch;
}

/// <summary>T-SQL lexical helpers.</summary>
internal static class TSql
{
    public static string Identifier(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Unicode(string value) => "N'" + value.Replace("'", "''") + "'";

    public static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Binary(byte[] bytes)
    {
        var sb = new StringBuilder(2 + bytes.Length * 2).Append("0x");
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The largest precision of SQL Server's time types (100 ns).</summary>
    public const int MaxTimePrecision = 7;

    public const int MaxDecimalPrecision = 38;

    /// <summary>The T-SQL type for a Substrait type, or null if there is none.</summary>
    public static string? TypeName(SType type) => type.Kind switch
    {
        TypeKind.Bool => "bit",
        TypeKind.I16 => "smallint",
        TypeKind.I32 => "int",
        TypeKind.I64 => "bigint",
        TypeKind.Fp32 => "real",
        TypeKind.Fp64 => "float",
        TypeKind.String => "nvarchar(max)",
        TypeKind.VarChar when type.Precision is > 0 and <= 4000 => $"nvarchar({type.Precision})",
        TypeKind.VarChar => "nvarchar(max)",
        TypeKind.FixedChar when type.Precision is > 0 and <= 4000 => $"nchar({type.Precision})",
        TypeKind.Binary => "varbinary(max)",
        TypeKind.FixedBinary when type.Precision is > 0 and <= 8000 => $"binary({type.Precision})",
        TypeKind.Date => "date",
        TypeKind.PrecisionTime when type.Precision is >= 0 and <= MaxTimePrecision => $"time({type.Precision})",
        TypeKind.PrecisionTimestamp when type.Precision is >= 0 and <= MaxTimePrecision => $"datetime2({type.Precision})",
        TypeKind.PrecisionTimestampTz when type.Precision is >= 0 and <= MaxTimePrecision => $"datetimeoffset({type.Precision})",
        TypeKind.Decimal when type.Precision is >= 1 and <= MaxDecimalPrecision && type.Scale >= 0 && type.Scale <= type.Precision
            => $"decimal({type.Precision},{type.Scale})",
        TypeKind.Uuid => "uniqueidentifier",
        _ => null,
    };

    public static string RequireTypeName(SType type) => TypeName(type)
        ?? throw SubstraitTranslationException.Unsupported($"Type '{type}' has no SQL Server equivalent.");

    /// <summary>Parenthesizes a nested query, putting it on lines of its own.</summary>
    public static string Parenthesize(string query) => "(\n" + query + "\n)";
}
