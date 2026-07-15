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

    private string Param(int valueIndex)
    {
        var value = _values[valueIndex] ?? (object)DBNull.Value;
        var name = "@p" + Parameters.Count;
        Parameters.Add(new SqlParameter(name, value));
        return name;
    }
}
