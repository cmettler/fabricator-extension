// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// Writes an Arrow stream to a DIRECTORY of parquet files through the host's own <c>COPY</c>, and removes it
/// again — the produce half of any "stage the data where the engine can read it, then tell the engine to
/// ingest the folder" load.
///
/// <para>Provider-agnostic on purpose. Its first consumer is the SQL Server backend's Fabric Warehouse
/// <c>COPY INTO</c> path, but nothing here knows about SQL Server: the same shape serves any engine whose
/// fastest ingest is "here is a folder of parquet" rather than a row stream over its wire protocol. It lives
/// beside <see cref="ExternalTableRouting"/> rather than inside it because that class is about a table whose
/// storage IS the destination, while this one is about a scratch area on the way somewhere else.</para>
/// </summary>
public static class HostParquetStaging
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql");

    /// <summary>True when the host query surface needed to write the parquet is available.</summary>
    public static bool Available => Host.CanQuery;

    /// <summary>
    /// Writes <paramref name="data"/> into <paramref name="directory"/> as one or more parquet files and
    /// returns the row count.
    /// </summary>
    /// <remarks>
    /// <para><c>PER_THREAD_OUTPUT</c> is what makes the target a DIRECTORY of <c>data_&lt;n&gt;.parquet</c>
    /// files rather than a single file, written by DuckDB's own parallel writer. Both halves matter: a folder
    /// is what a folder-consuming load wants (it ingests the whole thing recursively in one statement), and
    /// the produce side then scales with DuckDB's threads instead of with a single serialising writer.</para>
    /// <para>⚠ The generated names (<c>data_0.parquet</c>) are load-bearing in one respect worth knowing:
    /// several engines — Fabric <c>COPY INTO</c> among them — SKIP files whose name begins with <c>_</c> or
    /// <c>.</c>. DuckDB's do not, so the folder loads; a future change to the naming would have to hold that.
    /// The staging ROOT is checked separately, where the user supplies it.</para>
    /// </remarks>
    public static long WriteDirectory(string directory, IArrowArrayStream data)
    {
        // A FIXED name needs no uniquing and no cleanup: a bound input is a TEMPORARY view on this call's
        // own fresh connection, so it cannot be seen by — let alone collide with — any other host query, and
        // it dies with the connection. See RegisterArrowInputView.
        const string inputView = "__fabricator_stage_src";
        var sql = $"COPY (SELECT * FROM \"{inputView}\") TO '{directory.Replace("'", "''")}' "
                  + "(FORMAT parquet, PER_THREAD_OUTPUT true)";
        Log.LogDebug("stage parquet: {Sql}", sql);
        using var result = Host.Query(sql, new (string, IArrowArrayStream)[] { (inputView, data) });
        var batch = result.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
        long rows = batch is { ColumnCount: > 0, Length: > 0 } && batch.Column(0) is Int64Array c
                    && c.GetValue(0) is long v ? v : 0;
        batch?.Dispose();
        return rows;
    }

    /// <summary>
    /// Removes a staged directory. <b>Never throws.</b> By the time this runs the load has already succeeded
    /// or already failed, so turning a cleanup failure into the statement's error would report the wrong
    /// thing entirely. A leaked staging directory is visible, harmless and deletable by hand; a successful
    /// load reported as a failure is neither.
    /// </summary>
    /// <remarks>
    /// ⚠ <b><c>RemoveDirectory</c> IS UNIMPLEMENTED ON THE ONE FILESYSTEM THIS MATTERS MOST FOR — measured
    /// live 2026-08-10</b>: staging to OneLake and cleaning up raises
    /// <c>AzureDfsStorageFileSystem: RemoveDirectory is not implemented!</c>, so every load would leak its
    /// staged parquet on Fabric — the only platform the <c>COPY INTO</c> path runs on. Hence the per-file
    /// fallback, which is the same shape <c>DeltaCatalog.RemoveTableFolder</c> already needed for
    /// <c>DROP TABLE</c> on abfss and s3. That precedent was in the tree the whole time; the leak was found
    /// by running the feature, not by reading for it.
    /// </remarks>
    public static void RemoveDirectory(string directory)
    {
        var opener = AmbientOpener.Current;
        try
        {
            if (HostFs.CanRemoveDir)
            {
                HostFs.RemoveDir(opener, directory);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug("stage parquet: RemoveDirectory failed for {Dir} ({Message}) — per-file fallback",
                         directory, ex.Message);
        }
        RemoveByFiles(opener, directory);
    }

    /// <summary>Glob every object under the prefix and remove them one by one — object-store directories are
    /// implicit, and <c>RemoveFile</c> IS implemented where <c>RemoveDirectory</c> is not. Best-effort per
    /// object, so one undeletable file does not strand the rest.</summary>
    /// <remarks>
    /// ⚠ Paths come back from the glob, never from string arithmetic here, and on abfss that matters: the
    /// glob NORMALISES the URL it was handed, returning <c>abfss://&lt;host&gt;/&lt;fs&gt;/&lt;path&gt;</c>
    /// for an <c>abfss://&lt;fs&gt;@&lt;host&gt;/&lt;path&gt;</c> pattern — the workspace moves out of the
    /// authority and into the path. Round-tripping the filesystem's own answer sidesteps the question of
    /// which spelling the remove wants.
    /// </remarks>
    private static void RemoveByFiles(nint opener, string directory)
    {
        int removed = 0, failed = 0;
        try
        {
            var json = HostFs.Glob(opener, directory + "/**");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var path = element.GetProperty("path").GetString();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                try { HostFs.Remove(opener, path); removed++; }
                catch { failed++; }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("stage parquet: could not list {Dir} to clean up: {Message}", directory, ex.Message);
            return;
        }
        // The zero-byte directory-marker key some backends leave behind (best-effort, like the objects).
        try { HostFs.Remove(opener, directory + "/"); } catch { }
        if (failed > 0)
        {
            Log.LogWarning("stage parquet: {Dir} — removed {Removed}, {Failed} left behind",
                           directory, removed, failed);
        }
        else
        {
            Log.LogDebug("stage parquet: removed {Removed} staged file(s) under {Dir}", removed, directory);
        }
    }
}
