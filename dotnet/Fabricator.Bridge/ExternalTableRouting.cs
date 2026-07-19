using System;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// Storage-side WRITE routing for a SQL Server <b>external table</b> over S3
/// (docs/create-table-with-options.md slice C): SQL Server itself can never INSERT into an S3 external
/// table (and can never write Delta at all), so a detected DELTA/PARQUET external table's INSERT is
/// served by writing DIRECTLY to the storage the table points at — a Delta append via a transient
/// engineered-wood catalog (the C# mirror of the <c>COPY … (FORMAT delta)</c> transient-catalog shape),
/// or one new parquet file into the folder. SQL Server then serves the reads as usual.
///
/// <para><b>Endpoint asymmetry</b>: the external DATA SOURCE's LOCATION host (<c>s3://minio:9000/</c>) is
/// SQL Server's network view and is DISCARDED — the client-side write resolves a DuckDB <c>s3</c> secret
/// by bucket scope, whose ENDPOINT is authoritative (the FABRICATOR_S3_ENDPOINT vs
/// FABRICATOR_S3_SQL_ENDPOINT split). No matching secret surfaces as the write's own credential error.</para>
///
/// <para><b>Semantics</b>: statement-atomic — the Delta append is parked per (txn, table) inside the
/// transient catalog and flushed as ONE Delta commit at the end of this call; a parquet PUT completes
/// atomically. It does NOT join a surrounding explicit DuckDB transaction (the caller guards).</para>
/// </summary>
public static class ExternalTableRouting
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql.External");

    /// <summary>True when the host query surface is available (needed for the native parquet writes).</summary>
    public static bool CanRoute => Host.CanQuery;

    /// <summary>
    /// Composes the client-side table URI from the SQL-side EXTERNAL DATA SOURCE location
    /// (<c>s3://host[:port]/</c> — host discarded, see class remarks) + the external table's LOCATION
    /// (<c>/bucket/path/table</c>). Null when the data source is not <c>s3://</c> (not routable).
    /// </summary>
    public static string? ComposeS3Uri(string? dataSourceLocation, string? tableLocation)
    {
        if (string.IsNullOrWhiteSpace(dataSourceLocation) || string.IsNullOrWhiteSpace(tableLocation))
        {
            return null;
        }
        if (!dataSourceLocation!.TrimStart().StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var rel = tableLocation!.Replace('\\', '/').Trim('/');
        return rel.Length == 0 ? null : "s3://" + rel;
    }

    /// <summary>
    /// Appends <paramref name="data"/> to the Delta table at <paramref name="tableUri"/> as ONE Delta
    /// commit: a transient delta-provider catalog over the parent folder (flat layout, native_write),
    /// BulkInsert (parks the append) + CommitTransaction (flushes it) — the C# mirror of the COPY
    /// (FORMAT delta) finalize. The ambient opener/txn on THIS thread drive secret resolution + buffer
    /// keying (the SQL bulk consumer re-established them). Appends never change table features, so a
    /// protocol-1.0 SQL-readable table stays readable.
    /// </summary>
    public static long AppendDelta(string tableUri, IArrowArrayStream data, bool checkConstraints, long txnId)
    {
        var uri = tableUri.TrimEnd('/');
        int slash = uri.LastIndexOf('/');
        if (slash <= "s3://".Length)
        {
            throw new ArgumentException($"external delta table URI '{tableUri}' has no parent folder.");
        }
        string root = uri.Substring(0, slash);
        string leaf = uri.Substring(slash + 1);
        Log.LogInformation("external delta append: {Uri} (root={Root}, table={Leaf})", uri, root, leaf);
        var catalog = BackendRegistry.Resolve("delta").OpenCatalog(root, "{\"native_write\":\"true\"}");
        try
        {
            long rows = catalog.BulkInsert("main", leaf, data, createTable: false, replace: false,
                                           checkConstraints, txnId, partitionColumns: null, sortColumns: null,
                                           schemaMode: null, partitionOverwrite: false, optionsJson: null);
            // Flush the parked append as ONE Delta commit (statement-atomic; the transient catalog serves
            // exactly this statement, so the per-txn buffer holds only this append).
            catalog.CommitTransaction();
            return rows;
        }
        catch
        {
            try { catalog.RollbackTransaction(); } catch { /* discard-only backstop */ }
            throw;
        }
        finally
        {
            catalog.Dispose();
        }
    }

    /// <summary>
    /// Appends <paramref name="data"/> as ONE new parquet file (<c>&lt;uuid&gt;.parquet</c>) in the external
    /// table's folder via the host's native COPY. The external table reads all files under its LOCATION, so
    /// the rows appear once the object completes (an S3 PUT/multipart-complete is atomically visible).
    /// The file's columns are the INSERT stream's columns — DuckDB already bound them to the external
    /// table's declared column list, so the schema matches by construction.
    /// </summary>
    public static long AppendParquet(string folderUri, IArrowArrayStream data)
    {
        var file = folderUri.TrimEnd('/') + "/" + Guid.NewGuid().ToString("N") + ".parquet";
        Log.LogInformation("external parquet append: {File}", file);
        const string inputName = "__fabricator_external_insert";
        var sql = $"COPY (SELECT * FROM {inputName}) TO '{file.Replace("'", "''")}' (FORMAT parquet)";
        using var result = Host.Query(sql, new (string, IArrowArrayStream)[] { (inputName, data) });
        var batch = result.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
        long rows = batch is { ColumnCount: > 0, Length: > 0 } && batch.Column(0) is Int64Array c
                    && c.GetValue(0) is long v ? v : 0;
        batch?.Dispose();
        return rows;
    }
}
