// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;

namespace Fabricator.Bridge;

/// <summary>
/// Refuses a DuckLake schema change that the Delta mirror of a table cannot express, at the change itself rather than
/// at the next <c>ducklake_delta_sync</c>. For a catalog that hosts DuckLake's metadata: after DuckLake inserted rows
/// into <c>ducklake_column</c> or <c>ducklake_data_file</c>, the catalog calls this with the table's state as its own
/// transaction sees it, and an exception aborts DuckLake's commit. The rules are the sync's own (SchemaPlan), so the
/// two cannot disagree. See docs/ducklake-catalog.md §11.11.
/// </summary>
public static class DuckLakeMirrorGuard
{
    /// <summary>The marker table's name in the metadata schema.</summary>
    public static string MarkerTable => DuckLakeDeltaSync.MarkerTable;

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the column and the reason when the table's
    /// schema at <paramref name="snapshot"/> cannot be mirrored with the recorded options.</summary>
    public static void Check(string table, IEnumerable<object?[]> columns, IEnumerable<long> liveFileBegins, long snapshot,
        string columnMapping, string timestampMapping, bool deletionVectors)
    {
        try
        {
            DuckLakeDeltaSync.CheckSchema(table, columns, liveFileBegins, snapshot, columnMapping, timestampMapping,
                deletionVectors);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"DuckLake change refused: {table} is mirrored to Delta ({columnMapping} column mapping) and the mirror " +
                $"could not follow it — {ex.Message.Replace("ducklake_delta_sync: ", "")}", ex);
        }
    }
}
