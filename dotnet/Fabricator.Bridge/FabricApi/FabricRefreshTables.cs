// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Fabricator.Bridge;

/// <summary>
/// The <c>tables</c> argument of <c>fabric.refresh_sql_endpoint</c>, parsed: a comma-separated list of
/// <c>schema.table</c> or a bare <c>table</c> (schema <c>dbo</c>), each name optionally in double quotes or brackets,
/// grouped by schema in batches of at most <see cref="MaxTablesPerRequest"/> tables (the service's limit). BCL-only, so
/// the offline tier tests it.
/// </summary>
internal static class FabricRefreshTables
{
    /// <summary>The service's limit on tables per refresh request (Fabric.Api 2.27,
    /// <c>SqlEndpointRefreshMetadataRequest.Tables</c>).</summary>
    internal const int MaxTablesPerRequest = 25;

    /// <summary>Batches of (schema, tables) pairs, or null when <paramref name="tables"/> is null or blank, which
    /// refreshes every table.</summary>
    internal static List<List<(string Schema, List<string> Tables)>>? Parse(string? tables)
    {
        if (string.IsNullOrWhiteSpace(tables))
        {
            return null;
        }
        var entries = new List<(string Schema, string Table)>();
        foreach (var raw in tables!.Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }
            var (schema, table) = Split(entry);
            if (schema.Length == 0 || table.Length == 0)
            {
                throw new ArgumentException(
                    $"refresh_sql_endpoint: '{entry}' is not a table (expected schema.table or table).");
            }
            entries.Add((schema, table));
        }
        if (entries.Count == 0)
        {
            return null;
        }
        var batches = new List<List<(string Schema, List<string> Tables)>>();
        for (int i = 0; i < entries.Count; i += MaxTablesPerRequest)
        {
            batches.Add(entries.Skip(i).Take(MaxTablesPerRequest)
                .GroupBy(e => e.Schema, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Select(e => e.Table).ToList()))
                .ToList());
        }
        return batches;
    }

    /// <summary>An entry as (schema, table): the first dot outside quotes or brackets separates them.</summary>
    private static (string Schema, string Table) Split(string entry)
    {
        char? close = null;
        for (int i = 0; i < entry.Length; i++)
        {
            char c = entry[i];
            if (close is { } end)
            {
                if (c == end)
                {
                    close = null;
                }
            }
            else if (c == '"')
            {
                close = '"';
            }
            else if (c == '[')
            {
                close = ']';
            }
            else if (c == '.')
            {
                return (Unquote(entry.Substring(0, i)), Unquote(entry.Substring(i + 1)));
            }
        }
        return ("dbo", Unquote(entry));
    }

    private static string Unquote(string name)
    {
        name = name.Trim();
        if (name.Length >= 2
            && ((name[0] == '"' && name[name.Length - 1] == '"') || (name[0] == '[' && name[name.Length - 1] == ']')))
        {
            name = name.Substring(1, name.Length - 2);
        }
        return name;
    }
}
