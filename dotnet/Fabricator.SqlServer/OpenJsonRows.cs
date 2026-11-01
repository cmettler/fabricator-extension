// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// Rows sent as ONE parameter: a JSON array of arrays, read on the server with
/// <c>OPENJSON(@j) WITH ([c0] &lt;declared type&gt; '$[0]', …)</c> and joined with the table. The rowid DELETE and
/// UPDATE use it instead of one OR term or one statement per row — MEASURED on 100 000 of 200 000 rows: a DELETE
/// 2.44 → 0.61 s (rowstore), 49.7 → 0.94 s (a two-column key), an UPDATE 3.73 → 0.80 s, on a clustered columnstore
/// 344 → 2.5 s (docs/duckdb-2.0-remote-execution.md §5.4).
/// </summary>
/// <remarks>
/// <para>⚠ Each JSON column is declared with its TABLE column's type, read from <c>sys.columns</c> on the statement's
/// own connection — a string with its collation — so a key compares exactly as the server stores it. A type that does
/// not travel exactly as JSON (binary, sql_variant, xml, spatial, rowversion, …) makes <see cref="Columns"/> answer
/// null, and the caller keeps its per-row path. Not cached: a stale type would make a key silently match nothing.</para>
/// <para>Values travel as their exact text where a JSON number would round: decimal, money, dates and times.</para>
/// </remarks>
internal static class OpenJsonRows
{
    /// <summary>Rows per statement. The parameter is an nvarchar(max); the block bounds one statement's work and its
    /// log line, not a server limit (one statement of 100 000 rows measured the same as ten of 10 000).</summary>
    internal const int BlockRows = 10000;

    /// <summary>One JSON column: its T-SQL declaration for OPENJSON's WITH clause, and how its values are written —
    /// <see cref="TextFormat"/> null: a JSON number or boolean where the value is one; else the value as text, a date or
    /// time in this format (legacy datetime takes at most 3 fractional digits from text, smalldatetime none: the server
    /// then rounds as it does for a bound parameter).</summary>
    internal sealed record Column(string Declaration, bool AsText, string? TextFormat = null);

    /// <summary>The OPENJSON columns for these fields of <c>[schema].[table]</c>, in field order; null when one of
    /// them is not a column of the table or has a type that does not travel as JSON.</summary>
    internal static Column[]? Columns(SqlConnection connection, SqlTransaction? transaction, string qualified,
                                      IReadOnlyList<Field> fields)
    {
        var declared = new Dictionary<string, (string Type, int Length, int Precision, int Scale, string? Collation)>(
            StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, " +
                              "c.collation_name FROM sys.columns c WHERE c.object_id = OBJECT_ID(@obj)";
            cmd.Parameters.Add(new SqlParameter("@obj", System.Data.SqlDbType.NVarChar, 4000) { Value = qualified });
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                declared[reader.GetString(0)] = (reader.IsDBNull(1) ? "" : reader.GetString(1).ToLowerInvariant(),
                    reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }
        var result = new Column[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            if (!declared.TryGetValue(fields[i].Name, out var d) || Declare(d) is not { } column)
            {
                return null;
            }
            result[i] = column;
        }
        return result;
    }

    private static Column? Declare((string Type, int Length, int Precision, int Scale, string? Collation) d)
    {
        string collate = d.Collation is null ? "" : " COLLATE " + d.Collation;
        string Chars(int bytesPerChar) => d.Length == -1 ? "max" : (d.Length / bytesPerChar).ToString(CultureInfo.InvariantCulture);
        return d.Type switch
        {
            "bit" or "tinyint" or "smallint" or "int" or "bigint" => new Column(d.Type, AsText: false),
            "real" or "float" => new Column(d.Type, AsText: false),
            "decimal" or "numeric" => new Column($"{d.Type}({d.Precision},{d.Scale})", AsText: true),
            "money" or "smallmoney" => new Column(d.Type, AsText: true),
            "date" => new Column(d.Type, AsText: true, "yyyy-MM-dd"),
            "datetime" => new Column(d.Type, AsText: true, "yyyy-MM-dd'T'HH:mm:ss.fff"),
            "smalldatetime" => new Column(d.Type, AsText: true, "yyyy-MM-dd'T'HH:mm:ss"),
            "datetime2" => new Column($"{d.Type}({d.Scale})", AsText: true, "yyyy-MM-dd'T'HH:mm:ss.fffffff"),
            "time" => new Column($"{d.Type}({d.Scale})", AsText: true, @"hh\:mm\:ss\.fffffff"),
            "datetimeoffset" => new Column($"{d.Type}({d.Scale})", AsText: true, "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"),
            "uniqueidentifier" => new Column(d.Type, AsText: true),
            "char" or "varchar" => new Column($"{d.Type}({Chars(1)}){collate}", AsText: true),
            "nchar" or "nvarchar" => new Column($"{d.Type}({Chars(2)}){collate}", AsText: true),
            _ => null,
        };
    }

    /// <summary><c>OPENJSON(@name) WITH ([c0] … '$[0]', …) AS j</c> for <paramref name="columns"/>.</summary>
    internal static string Source(string parameter, IReadOnlyList<Column> columns)
    {
        var sb = new StringBuilder("OPENJSON(").Append(parameter).Append(") WITH (");
        for (int i = 0; i < columns.Count; i++)
        {
            sb.Append(i == 0 ? "" : ", ").Append("[c").Append(i).Append("] ").Append(columns[i].Declaration)
              .Append(" '$[").Append(i).Append("]'");
        }
        return sb.Append(") AS j").ToString();
    }

    /// <summary>The rows as one JSON array of arrays, ready to bind as an nvarchar(max) parameter.</summary>
    internal static SqlParameter Parameter(string name, IReadOnlyList<object?[]> rows, IReadOnlyList<Column> columns)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var row in rows)
            {
                json.WriteStartArray();
                for (int c = 0; c < columns.Count; c++)
                {
                    WriteValue(json, row[c], columns[c]);
                }
                json.WriteEndArray();
            }
            json.WriteEndArray();
        }
        return new SqlParameter(name, System.Data.SqlDbType.NVarChar, -1) { Value = Encoding.UTF8.GetString(buffer.ToArray()) };
    }

    private static void WriteValue(Utf8JsonWriter json, object? value, Column column)
    {
        bool asText = column.AsText;
        string? format = column.TextFormat;
        switch (value)
        {
            case null or DBNull:
                json.WriteNullValue();
                return;
            case bool b:
                json.WriteBooleanValue(b);
                return;
            case string s:
                json.WriteStringValue(s);
                return;
        }
        if (!asText)
        {
            switch (value)
            {
                case long l: json.WriteNumberValue(l); return;
                case int i: json.WriteNumberValue(i); return;
                case short s: json.WriteNumberValue(s); return;
                case byte b: json.WriteNumberValue(b); return;
                case sbyte sb: json.WriteNumberValue(sb); return;
                case ushort us: json.WriteNumberValue(us); return;
                case uint ui: json.WriteNumberValue(ui); return;
                case ulong ul: json.WriteNumberValue(ul); return;
                case double d: json.WriteNumberValue(d); return;
                case float f: json.WriteNumberValue(f); return;
                case decimal m: json.WriteNumberValue(m); return;
            }
        }
        json.WriteStringValue(value switch
        {
            DateTime dt => dt.ToString(format ?? "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            DateTimeOffset dto => format is null || format.EndsWith("zzz", StringComparison.Ordinal)
                ? dto.ToString(format ?? "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)
                : dto.UtcDateTime.ToString(format, CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeSpan ts => ts.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        });
    }
}
