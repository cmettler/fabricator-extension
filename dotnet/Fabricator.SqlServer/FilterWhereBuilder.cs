using System.Text;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// Renders a pushed-down <see cref="FilterNode"/> tree into a parameterized
/// T-SQL <c>WHERE</c> clause. Constants come from the filter value batch (by
/// index) and become <see cref="SqlParameter"/>s — so there is no literal
/// escaping, and SQL Server infers types from the CLR values.
///
/// The C++ host only emits superset-safe predicates, so this builder does not
/// re-check safety; it throws on anything it can't render, and the caller falls
/// back to no WHERE (DuckDB still applies every filter).
/// </summary>
internal sealed class FilterWhereBuilder
{
    private readonly IReadOnlyList<object?> _values;

    public FilterWhereBuilder(IReadOnlyList<object?> values)
    {
        _values = values;
    }

    public List<SqlParameter> Parameters { get; } = new();

    public string Build(FilterNode node) => node.Op switch
    {
        "and" => Conjunction(node, " AND "),
        "or" => Conjunction(node, " OR "),
        "compare" => Compare(node),
        "is_null" => $"{Col(node)} IS NULL",
        "is_not_null" => $"{Col(node)} IS NOT NULL",
        "in" => In(node),
        _ => throw new NotSupportedException($"fabricator: unsupported filter op '{node.Op}'"),
    };

    private string Conjunction(FilterNode node, string sep)
    {
        var children = node.Children ?? throw new InvalidOperationException("conjunction without children");
        var sb = new StringBuilder("(");
        for (int i = 0; i < children.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(sep);
            }
            sb.Append(Build(children[i]));
        }
        return sb.Append(')').ToString();
    }

    private string Compare(FilterNode node)
    {
        var op = node.Cmp switch
        {
            "=" => "=",
            "<>" => "<>",
            "<" => "<",
            "<=" => "<=",
            ">" => ">",
            ">=" => ">=",
            "is_distinct" => "IS DISTINCT FROM",         // SQL Server 2022+
            "is_not_distinct" => "IS NOT DISTINCT FROM", // SQL Server 2022+
            _ => throw new NotSupportedException($"fabricator: unsupported comparison '{node.Cmp}'"),
        };
        var idx = node.Val ?? throw new InvalidOperationException("compare without val");
        return $"{Col(node)} {op} {Param(idx)}";
    }

    private string In(FilterNode node)
    {
        var idxs = node.Vals ?? throw new InvalidOperationException("in without vals");
        return $"{Col(node)} IN ({string.Join(", ", idxs.Select(Param))})";
    }

    private static string Col(FilterNode node)
    {
        var name = node.Col ?? throw new InvalidOperationException("node without col");
        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>
    /// Binds one pushed filter value as a parameter and returns its placeholder.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ A <see cref="DateTime"/> IS PINNED TO <c>datetime2</c>, AND LETTING SqlClient INFER IT IS
    /// A SILENT DATA-LOSS BUG.</b> Inference maps a CLR <c>DateTime</c> to <c>SqlDbType.DateTime</c> — the
    /// LEGACY type, whose resolution is ~3.33 ms — so the value is ROUNDED TO THE NEAREST TICK before the
    /// server ever sees it. MEASURED over 3400 consecutive microsecond offsets: 1732 round DOWN and
    /// <b>1667 round UP</b>. Rounding down is harmless (the pushed predicate admits a superset and DuckDB
    /// re-applies it), but rounding UP makes a <c>&gt;</c> predicate STRICTER, so matching rows are dropped
    /// on the SERVER and never reach the re-application that would have saved them. That breaks the
    /// never-erases rule this whole pushdown rests on.</para>
    /// <para><b>⚠ It was NOT reachable before 2026-08-25</b>, and only by accident:
    /// <c>ArrowValueReader.ReadTimestamp</c> had a ternary-unification bug that made every timestamp arrive
    /// as a <c>DateTimeOffset</c>, which SqlClient maps to <c>datetimeoffset</c> — full precision, so no
    /// loss. Fixing that bug is what made the inference reachable, which is why the two changes belong in
    /// one commit.</para>
    /// <para><b>⚠ <c>datetime2</c> rather than <c>datetime</c> or <c>datetimeoffset</c>, and each rejection
    /// is measured.</b> Against an indexed <c>datetime2</c> column, a <c>datetime2</c> parameter gives a
    /// direct <c>Index Seek(SEEK: dt &gt; @p)</c> that parallelises, where a <c>datetimeoffset</c> one goes
    /// through <c>GetRangeWithMismatchedTypes</c> — a constant scan and a nested loop computing an
    /// equivalent range first — and did not parallelise. Against a legacy <c>datetime</c> or a <c>date</c>
    /// column it is the mismatched-types seek instead, which is what <c>datetimeoffset</c> already was for
    /// BOTH, and the widening conversion is lossless so no row is dropped.</para>
    /// <para>⚠ A <see cref="DateTimeOffset"/> is left to inference deliberately: it maps to
    /// <c>datetimeoffset</c>, which is the MATCHING type for the only column type that produces one.</para>
    /// </remarks>
    private string Param(int valueIndex)
    {
        var value = _values[valueIndex] ?? (object)DBNull.Value;
        var name = "@p" + Parameters.Count;
        Parameters.Add(value is DateTime
            ? new SqlParameter(name, System.Data.SqlDbType.DateTime2) { Value = value }
            : new SqlParameter(name, value));
        return name;
    }
}
