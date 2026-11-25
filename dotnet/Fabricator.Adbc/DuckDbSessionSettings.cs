// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Adbc;

/// <summary>
/// The settings that change what the written SQL means, and the engine version, of one DuckDB session: the server's
/// (read once at ATTACH) or the host session's (read per translation). The same SQL means the same only when both
/// agree on every one of them.
/// </summary>
internal sealed class DuckDbSessionSettings
{
    /// <summary>
    /// Settings a DuckDB SQL text depends on: TIMESTAMPTZ arithmetic, casts and parts (TimeZone, Calendar — absent
    /// without ICU, which is a difference too), string comparison (default_collation), a scalar subquery with several
    /// rows (an error or any row), how the server binds the implicit casts the text leaves to it, and a division by zero
    /// or a float outside its domain. NULL order and sort direction are always written out.
    /// </summary>
    public static readonly string[] Compared =
    {
        "TimeZone", "Calendar", "default_collation", "scalar_subquery_error_on_multiple_rows", "old_implicit_casting",
        "ieee_floating_point_ops", "error_on_division_by_zero",
    };

    /// <summary>
    /// One query that reads them and the version as ONE row, a column each (NULL for a setting this session does not
    /// have — Calendar without ICU). current_setting, not duckdb_settings(): that scans every setting, ~7.6 ms
    /// against ~1.2 ms, paid before every plan with expressions.
    /// </summary>
    public static readonly string Query =
        "SELECT " + string.Join(", ", Compared.Select(n => "try(CAST(current_setting('" + n + "') AS VARCHAR))"))
        + ", library_version FROM pragma_version()";

    public static readonly DuckDbSessionSettings Default = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ieee_floating_point_ops"] = "true", ["error_on_division_by_zero"] = "true",
    });

    public DuckDbSessionSettings(IReadOnlyDictionary<string, string> values) => Values = values;

    public IReadOnlyDictionary<string, string> Values { get; }

    public bool IeeeFloatingPointOps => Flag("ieee_floating_point_ops");
    public bool ErrorOnDivisionByZero => Flag("error_on_division_by_zero");

    private bool Flag(string name) => Values.TryGetValue(name, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the rows of <see cref="Query"/>.</summary>
    public static DuckDbSessionSettings Read(Apache.Arrow.Ipc.IArrowArrayStream stream)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
        {
            using (batch)
            {
                if (batch.Length == 0)
                    continue;
                for (int c = 0; c < Compared.Length; c++)
                {
                    var column = (Apache.Arrow.StringArray)batch.Column(c);
                    if (!column.IsNull(0))
                        values[Compared[c]] = column.GetString(0) ?? "";
                }
                values["#version"] = ((Apache.Arrow.StringArray)batch.Column(Compared.Length)).GetString(0) ?? "";
            }
        }
        return new DuckDbSessionSettings(values);
    }

    /// <summary>The first setting the two sessions disagree on, as a reason; null when they agree.</summary>
    public string? Difference(DuckDbSessionSettings host)
    {
        foreach (var name in Compared.Prepend("#version"))
        {
            Values.TryGetValue(name, out var server);
            host.Values.TryGetValue(name, out var local);
            if (!string.Equals(server, local, StringComparison.Ordinal))
                return name == "#version"
                    ? $"the server runs DuckDB {server ?? "?"}, this session {local ?? "?"}"
                    : $"setting {name} is '{local ?? "(not set)"}' here and '{server ?? "(not set)"}' on the server";
        }
        return null;
    }
}
