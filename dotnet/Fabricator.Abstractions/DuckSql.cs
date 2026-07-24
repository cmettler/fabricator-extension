using System.Globalization;

namespace Fabricator.Bridge;

/// <summary>
/// Quoting helpers for authors of SQL-generating table functions (<see cref="ISqlTableFunction"/> /
/// <see cref="ICatalogSqlTableFunction"/>), whose <c>GenerateSql</c> splices argument values into DuckDB SQL
/// text. Everything a generator emits is re-parsed by DuckDB in the CALLER'S session, so this is a
/// correctness/robustness concern (identifiers with spaces or quotes, strings with apostrophes, culture-safe
/// numbers), not a privilege boundary — the generated SQL runs as the calling user either way.
///
/// <para>Prefer these over hand-rolled interpolation. See docs/macros-and-sqlgen-functions.md §2.</para>
/// </summary>
public static class DuckSql
{
    /// <summary>
    /// Quotes an IDENTIFIER (table/column/schema/alias) for DuckDB: wrapped in double quotes with embedded
    /// quotes doubled — <c>my "odd" tbl</c> → <c>"my ""odd"" tbl"</c>. Also makes the identifier
    /// case-SENSITIVE and immune to keyword collisions.
    /// </summary>
    public static string QuoteIdent(string identifier) =>
        "\"" + (identifier ?? throw new ArgumentNullException(nameof(identifier))).Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Quotes a possibly-qualified NAME part by part: <c>QuoteName("db", "dbo", "sales")</c> →
    /// <c>"db"."dbo"."sales"</c>. Empty/null parts are skipped, so an unqualified name or an absent catalog
    /// works with the same call.
    /// </summary>
    public static string QuoteName(params string?[] parts) =>
        string.Join(".", (parts ?? System.Array.Empty<string?>())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => QuoteIdent(p!)));

    /// <summary>
    /// Renders a CLR value as a DuckDB SQL LITERAL: <c>NULL</c>; strings/chars/byte-arrays single-quoted
    /// (apostrophes doubled, blobs as <c>'\x..'::BLOB</c>); booleans <c>true</c>/<c>false</c>; numbers with
    /// INVARIANT culture (never a comma decimal separator); dates/times as quoted ISO-8601 with an explicit
    /// cast so the type survives. An unsupported type throws — a generator must not silently emit
    /// <c>ToString()</c> of something it did not mean.
    /// </summary>
    public static string Literal(object? value)
    {
        switch (value)
        {
            case null:
                return "NULL";
            case bool b:
                return b ? "true" : "false";
            case string s:
                return QuoteString(s);
            case char c:
                return QuoteString(c.ToString());
            case byte[] bytes:
                return "'\\x" + Convert.ToHexString(bytes) + "'::BLOB";
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case decimal d:
                return d.ToString(CultureInfo.InvariantCulture);
            case float f:
                // "R" round-trips; a non-finite value has no literal form in SQL.
                return float.IsFinite(f)
                    ? f.ToString("R", CultureInfo.InvariantCulture)
                    : throw new NotSupportedException($"fabricator: cannot render non-finite float literal '{f}'");
            case double dbl:
                return double.IsFinite(dbl)
                    ? dbl.ToString("R", CultureInfo.InvariantCulture)
                    : throw new NotSupportedException($"fabricator: cannot render non-finite double literal '{dbl}'");
            case DateTime dt:
                return QuoteString(dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture))
                       + "::TIMESTAMP";
            case DateTimeOffset dto:
                return QuoteString(dto.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture))
                       + "::TIMESTAMPTZ";
            case DateOnly d:
                return QuoteString(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + "::DATE";
            case TimeOnly t:
                return QuoteString(t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)) + "::TIME";
            case Guid g:
                return QuoteString(g.ToString()) + "::UUID";
            default:
                throw new NotSupportedException(
                    $"fabricator: no SQL literal form for type '{value.GetType().Name}' — convert it explicitly");
        }
    }

    /// <summary>Quotes a STRING literal (single quotes, apostrophes doubled). Use <see cref="Literal"/> unless
    /// the value is known to be a string and must stay untyped.</summary>
    public static string QuoteString(string value) =>
        "'" + (value ?? throw new ArgumentNullException(nameof(value))).Replace("'", "''") + "'";
}
