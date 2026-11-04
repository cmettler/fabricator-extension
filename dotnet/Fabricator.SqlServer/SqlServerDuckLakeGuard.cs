// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Fabricator.SqlServer;

public sealed partial class SqlServerCatalog
{
    /// <summary>
    /// DuckLake with its metadata in this catalog: right before the COMMIT of a transaction that inserted rows into
    /// <c>ducklake_column</c> (a schema change), <c>ducklake_data_file</c> (new files — after a rename they may carry the
    /// new column name) or <c>ducklake_partition_info</c> (a partitioning change: every SET and RESET PARTITIONED BY
    /// inserts one, measured), checks every table the newest snapshot's rows belong to that <c>ducklake_delta_sync</c> mirrors (the marker table in the same schema). Runs on
    /// DuckLake's own connection and transaction, so it sees the uncommitted change; a refusal throws and DuckLake's
    /// commit rolls back whole. A schema without the marker table costs one OBJECT_ID. See docs/ducklake-catalog.md §11.11.
    /// </summary>
    private static bool IsDuckLakeMirrorTable(string tableName) =>
        string.Equals(tableName, "ducklake_column", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tableName, "ducklake_data_file", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tableName, "ducklake_partition_info", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a DuckLake commit whose metadata lives on a warehouse engine (Fabric Warehouse, Synapse dedicated).
    /// DuckLake detects a concurrent commit through a PRIMARY KEY violation on <c>ducklake_snapshot</c>, and a
    /// warehouse declares keys NOT ENFORCED, so two writers could both commit the same snapshot id without an error.
    /// Every writing DuckLake commit, the first one included, inserts its snapshot row here, so refusing that insert
    /// refuses every write and leaves reading alone. See docs/ducklake-catalog.md §7.
    /// </summary>
    private void RefuseDuckLakeOnWarehouse(string tableName)
    {
        if (string.Equals(tableName, "ducklake_snapshot", StringComparison.OrdinalIgnoreCase) && Profile.IsWarehouse)
        {
            throw new NotSupportedException(
                "DuckLake cannot keep its metadata on Fabric Warehouse or Synapse: their PRIMARY KEYs are not enforced, "
                + "and DuckLake relies on one to detect two writers committing at once. Use a Fabric SQL database, "
                + "Azure SQL or SQL Server for the metadata.");
        }
    }

    /// <summary>
    /// A partitioning change at <paramref name="snapshot"/> must keep every partition column the mirror declared as an
    /// identity key: Delta cannot change a table's partition columns, and the sync would refuse the first file written
    /// without one. Adding keys is fine (the sync leaves them undeclared). Nothing to check when the snapshot did not
    /// change the partitioning.
    /// </summary>
    private static void CheckDuckLakePartitioning(Func<string, SqlCommand> cmd, string s, long tableId, string table,
        string declaredIds, List<object?[]> columns, long snapshot)
    {
        long? partitionId = null;
        using (var p = cmd($"SELECT partition_id FROM {s}.[ducklake_partition_info] WHERE table_id = @t AND begin_snapshot = @s"))
        {
            p.Parameters.AddWithValue("@t", tableId);
            p.Parameters.AddWithValue("@s", snapshot);
            if (p.ExecuteScalar() is { } v and not DBNull)
            {
                partitionId = Convert.ToInt64(v);
            }
        }
        if (partitionId is null)
        {
            return;
        }
        var keys = new HashSet<long>();
        using (var k = cmd($"SELECT column_id FROM {s}.[ducklake_partition_column] WHERE partition_id = @p AND table_id = @t " +
                           "AND transform = N'identity'"))
        {
            k.Parameters.AddWithValue("@p", partitionId.Value);
            k.Parameters.AddWithValue("@t", tableId);
            using var r = k.ExecuteReader();
            while (r.Read())
            {
                keys.Add(Convert.ToInt64(r.GetValue(0)));
            }
        }
        var lost = new List<string>();
        foreach (var part in declaredIds.Split(','))
        {
            long id = long.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (!keys.Contains(id))
            {
                lost.Add(ColumnName(columns, id, snapshot) ?? ("column id " + part));
            }
        }
        if (lost.Count > 0)
        {
            throw new InvalidOperationException(
                $"DuckLake change refused: {table} is mirrored to Delta partitioned by [{string.Join(", ", lost)}], and the new " +
                "partitioning drops it; Delta cannot change a table's partition columns. To change it, first write the mirror " +
                $"anew without partitions: SELECT * FROM ducklake_delta_sync('<lake>', table_name := '{table}', " +
                "rebuild := true, partitioning := false)");
        }
    }

    // The column's name at the snapshot (column_id, begin, end, …, column_name at index 4), for the message.
    private static string? ColumnName(List<object?[]> columns, long id, long snapshot)
    {
        foreach (var c in columns)
        {
            if (Convert.ToInt64(c[0]) == id && Convert.ToInt64(c[1]) <= snapshot
                && (c[2] is null || Convert.ToInt64(c[2]) > snapshot))
            {
                return c[4]?.ToString();
            }
        }
        return null;
    }

    private void CheckDuckLakeMirror(SqlConnection connection, SqlTransaction? transaction, string schemaName)
    {
        string s = Quote(schemaName);
        string marker = Quote(DuckLakeMirrorGuard.MarkerTable);
        string objectLiteral = "N'" + (schemaName + "." + DuckLakeMirrorGuard.MarkerTable).Replace("'", "''") + "'";

        SqlCommand Cmd(string sql)
        {
            var c = connection.CreateCommand();
            c.Transaction = transaction;
            c.CommandTimeout = ResolveCommandTimeout();
            c.CommandText = sql;
            return c;
        }

        using (var probe = Cmd($"SELECT OBJECT_ID({objectLiteral}, 'U')"))
        {
            if (probe.ExecuteScalar() is null or DBNull)
            {
                return;
            }
        }
        long snapshot;
        using (var max = Cmd($"SELECT MAX(snapshot_id) FROM {s}.[ducklake_snapshot]"))
        {
            snapshot = Convert.ToInt64(max.ExecuteScalar());
        }

        // A marker from before 2026-11-04 has no partition column ids; the next sync adds them.
        bool hasParts;
        using (var col = Cmd($"SELECT COL_LENGTH({objectLiteral}, 'partition_column_ids')"))
        {
            hasParts = col.ExecuteScalar() is not (null or DBNull);
        }

        // the mirrored tables the new rows belong to
        var tables = new List<(long Id, string Mapping, string Timestamps, bool Dvs, string? Parts)>();
        using (var touched = Cmd(
                   $"SELECT DISTINCT m.table_id, m.column_mapping, m.timestamp_mapping, m.deletion_vectors, " +
                   $"{(hasParts ? "m.partition_column_ids" : "CAST(NULL AS varchar(1))")} FROM {s}.{marker} m " +
                   $"WHERE m.table_id IN (SELECT table_id FROM {s}.[ducklake_column] WHERE begin_snapshot = @s " +
                   $"UNION SELECT table_id FROM {s}.[ducklake_data_file] WHERE begin_snapshot = @s " +
                   $"UNION SELECT table_id FROM {s}.[ducklake_partition_info] WHERE begin_snapshot = @s)"))
        {
            touched.Parameters.AddWithValue("@s", snapshot);
            using var r = touched.ExecuteReader();
            while (r.Read())
            {
                tables.Add((r.GetInt64(0), r.IsDBNull(1) ? "name" : r.GetString(1), r.IsDBNull(2) ? "utc" : r.GetString(2),
                            !r.IsDBNull(3) && Convert.ToBoolean(r.GetValue(3)), r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        foreach (var t in tables)
        {
            string name = t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using (var n = Cmd($"SELECT TOP (1) sc.schema_name + N'.' + tb.table_name FROM {s}.[ducklake_table] tb " +
                               $"JOIN {s}.[ducklake_schema] sc ON sc.schema_id = tb.schema_id WHERE tb.table_id = @t " +
                               "ORDER BY tb.begin_snapshot DESC"))
            {
                n.Parameters.AddWithValue("@t", t.Id);
                if (n.ExecuteScalar() is string found)
                {
                    name = found;
                }
            }
            var columns = new List<object?[]>();
            using (var c = Cmd("SELECT column_id, begin_snapshot, end_snapshot, column_order, column_name, column_type, " +
                               $"initial_default, nulls_allowed, parent_column FROM {s}.[ducklake_column] WHERE table_id = @t"))
            {
                c.Parameters.AddWithValue("@t", t.Id);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var row = new object?[9];
                    for (int i = 0; i < 9; i++)
                    {
                        row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                    }
                    columns.Add(row);
                }
            }
            var begins = new List<long>();
            using (var f = Cmd($"SELECT begin_snapshot FROM {s}.[ducklake_data_file] WHERE table_id = @t AND begin_snapshot <= @s " +
                               "AND (end_snapshot IS NULL OR end_snapshot > @s)"))
            {
                f.Parameters.AddWithValue("@t", t.Id);
                f.Parameters.AddWithValue("@s", snapshot);
                using var r = f.ExecuteReader();
                while (r.Read())
                {
                    begins.Add(Convert.ToInt64(r.GetValue(0)));
                }
            }
            Log.LogDebug("ducklake mirror check {Table} at snapshot {Snapshot}: {Columns} column rows, {Files} live files",
                         name, snapshot, columns.Count, begins.Count);
            DuckLakeMirrorGuard.Check(name, columns, begins, snapshot, t.Mapping, t.Timestamps, t.Dvs);
            if (!string.IsNullOrEmpty(t.Parts))
            {
                CheckDuckLakePartitioning(Cmd, s, t.Id, name, t.Parts!, columns, snapshot);
            }
        }
    }
}
