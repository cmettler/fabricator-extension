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

    /// <summary>One query that reads them (and the version) as (name, value) rows; an absent setting has no row.</summary>
    public static readonly string Query =
        "SELECT name, CAST(value AS VARCHAR) AS value FROM duckdb_settings() WHERE lower(name) IN ("
        + string.Join(", ", Compared.Select(n => "'" + n.ToLowerInvariant() + "'"))
        + ") UNION ALL SELECT '#version', library_version FROM pragma_version()";

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
                var names = (Apache.Arrow.StringArray)batch.Column(0);
                var vals = (Apache.Arrow.StringArray)batch.Column(1);
                for (int i = 0; i < batch.Length; i++)
                    values[names.GetString(i)] = vals.GetString(i) ?? "";
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
