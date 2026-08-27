// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Fabricator.SqlServer;

/// <summary>
/// The Fabric Warehouse bulk-load path that goes through <b>staged parquet + <c>COPY INTO</c></b> instead of
/// <see cref="SqlBulkCopy"/>.
///
/// <para><b>Why it exists.</b> <c>SqlBulkCopy</c> streams rows over TDS one buffer at a time; against a
/// Fabric Warehouse that is a round trip per buffer across a WAN, and it is the same single connection the
/// rest of the statement needs. A <c>COPY INTO</c> load inverts that: DuckDB writes the whole result to
/// parquet in the staging area — in parallel, with its own writer — and the warehouse then ingests those
/// files with its own engine, in ONE statement. The data never crosses TDS.</para>
///
/// <para><b>Opt-in, and the opt-in is the staging location.</b> There is no sensible default for "where may
/// this extension write temporary files", so the setting that names it is also the switch. Nothing here is
/// ever inferred: a warehouse attach with no staging location keeps <c>SqlBulkCopy</c> exactly as before.
/// That also means there is no size threshold — <c>COPY INTO</c> has seconds of fixed cost and would be a
/// pessimisation for a ten-row INSERT, so the choice stays the user's rather than a guess made from a row
/// count we do not know until the stream has already been consumed.</para>
/// </summary>
internal static class WarehouseCopyInto
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql");

    /// <summary>True when a staged load can actually run: it needs the host query surface (to write the
    /// parquet) as well as a configured location.</summary>
    internal static bool CanStage => HostParquetStaging.Available;

    /// <summary>
    /// Writes <paramref name="data"/> to a fresh directory under the staging root and loads it into
    /// <paramref name="qualifiedTarget"/> with one <c>COPY INTO</c>, then removes the staged files.
    /// Returns the number of rows loaded.
    /// </summary>
    /// <remarks>
    /// Runs on the SAME connection and transaction the <see cref="SqlBulkCopy"/> path would have used, so the
    /// load's transaction semantics are unchanged by choosing this path — an explicit DuckDB
    /// <c>BEGIN … ROLLBACK</c> still governs it, and autocommit still commits per statement.
    /// </remarks>
    internal static long Load(SqlConnection connection, SqlTransaction? transaction, string qualifiedTarget,
                              StagingLocation staging, IArrowArrayStream data, int commandTimeout)
    {
        // Captured BEFORE the stream is consumed — after the COPY the schema is no longer reachable.
        var columns = new List<string>();
        foreach (var field in data.Schema.FieldsList)
        {
            columns.Add(field.Name);
        }

        var subdirectory = Guid.NewGuid().ToString("N");
        var clientDirectory = staging.ClientRoot + "/" + subdirectory;
        var loadDirectory = staging.LoadRoot + "/" + subdirectory + "/";

        try
        {
            long staged = HostParquetStaging.WriteDirectory(clientDirectory, data);
            if (staged == 0)
            {
                // COPY INTO over an empty folder is an error on some engines and a no-op on others; either
                // way there is nothing to load, and reporting 0 is the truthful answer for an INSERT that
                // produced no rows.
                Log.LogDebug("copy into {Table}: source produced no rows — load skipped", qualifiedTarget);
                return 0;
            }

            var sql = BuildStatement(qualifiedTarget, columns, loadDirectory);
            Log.LogDebug("copy into {Table}: {Sql}", qualifiedTarget, sql);
            using var load = connection.CreateCommand();
            load.Transaction = transaction;
            load.CommandText = sql;
            load.CommandTimeout = commandTimeout;
            load.ExecuteNonQuery();

            Log.LogInformation("copy into {Table}: {Rows} rows loaded from {Files}",
                               qualifiedTarget, staged, loadDirectory);
            // The STAGED count is the loaded count: MAXERRORS defaults to 0, so COPY INTO either ingests
            // every row or fails the statement — there is no partial-load state for the two to disagree on.
            return staged;
        }
        finally
        {
            HostParquetStaging.RemoveDirectory(clientDirectory);
        }
    }

    /// <summary>
    /// Builds the <c>COPY INTO</c> statement. The column list is <b>never omitted</b>, and that is a
    /// correctness requirement rather than clarity: without one, <c>COPY INTO</c> maps the source's fields to
    /// the target's columns <b>by ordinal</b>, so an INSERT whose stream happens to be ordered differently
    /// from the table would load every value into the wrong column and succeed. Naming the columns in the
    /// stream's own order makes the ordinal and by-name readings agree, because the parquet we stage is
    /// written in exactly that order.
    /// </summary>
    internal static string BuildStatement(string qualifiedTarget, IReadOnlyList<string> columns, string location)
    {
        var sb = new StringBuilder();
        sb.Append("COPY INTO ").Append(qualifiedTarget).Append(" (");
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            sb.Append(SqlServerCatalog.Quote(columns[i]));
        }
        sb.Append(") FROM '").Append(location.Replace("'", "''")).Append("' ");
        // FILE_TYPE is the only option we set. CREDENTIAL is deliberately absent: OneLake as a COPY INTO
        // source accepts EntraID only — no SAS and no account key — so the statement authenticates as the
        // warehouse connection's own identity, which is the identity the ATTACH already established.
        sb.Append("WITH (FILE_TYPE = 'PARQUET')");
        return sb.ToString();
    }
}
