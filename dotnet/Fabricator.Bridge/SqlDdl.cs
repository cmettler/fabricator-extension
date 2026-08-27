// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// Heuristics over raw T-SQL run through <c>fabricator_exec</c>. The DDL detection
/// lives here (C#) so the host only has to act on the boolean signal: after an
/// exec that may have changed schema, the catalog cache can be invalidated so a
/// later read / CREATE IF NOT EXISTS sees the real server-side state.
/// </summary>
public static class SqlDdl
{
    // CREATE / DROP / ALTER / TRUNCATE / RENAME / EXEC — EXEC included because a
    // stored procedure may itself run DDL. Over-detection only costs a metadata
    // refresh; under-detection would leave the cache stale, so we err toward true.
    private static readonly string[] SchemaKeywords =
        { "CREATE", "DROP", "ALTER", "TRUNCATE", "RENAME", "EXEC" };

    /// <summary>
    /// True if the statement may change schema/catalog metadata (keyword heuristic,
    /// case-insensitive). Conservative by design — a false positive just refreshes.
    /// </summary>
    public static bool MayChangeSchema(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return false;
        }
        var upper = sql.ToUpperInvariant();
        foreach (var keyword in SchemaKeywords)
        {
            if (upper.Contains(keyword))
            {
                return true;
            }
        }
        return false;
    }
}
