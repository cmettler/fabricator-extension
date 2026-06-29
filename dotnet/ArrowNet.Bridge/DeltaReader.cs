using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Apache.Arrow;
using EngineeredWood.DeltaLake;
using EngineeredWood.DeltaLake.Table;
using EngineeredWood.Expressions;
using EngineeredWood.Parquet;

namespace ArrowNet.Bridge;

/// <summary>
/// Reads a Delta Lake table (Curt Hagenlocher's engineered-wood, pure C#) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via <see cref="DuckDbTableFileSystem"/> — so Delta tables on local/az://-s3://-https://
/// paths read with DuckDB's secrets + backends. Surfaced to DuckDB as the <c>arrownet_delta_scan(path)</c>
/// connection-free GLOBAL host-FS table function (see <see cref="DeltaGlobalTableFunction"/>); the opener is
/// the calling operator's ClientContext, threaded via <see cref="AmbientOpener"/>.
/// </summary>
internal static class DeltaReader
{
    /// <summary>Opens the Delta table at <paramref name="path"/> and returns its Arrow schema only (no data
    /// read). Used at table-function bind. <paramref name="opener"/> = the calling operator's ClientContext.</summary>
    public static Schema GetSchema(nint opener, string path)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            return table.ArrowSchema;
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>
    /// Streams the Delta table at <paramref name="path"/> lazily (one <see cref="RecordBatch"/> at a time, no
    /// materialization). <paramref name="columns"/> (null =&gt; all) is the projection — engineered-wood reads
    /// only those columns. <paramref name="filter"/> (null =&gt; none) is pushed for <b>file + row-group
    /// skipping</b>: it drives both the Delta file pruner (<c>ReadAllAsync(columns, filter)</c>) and the
    /// per-file Parquet row-group/stats pruner (via <c>ParquetReadOptions.Filter</c>). engineered-wood does not
    /// re-apply the predicate per row, so the result is a superset — DuckDB re-applies above the scan.
    /// <paramref name="opener"/> (the operator's ClientContext) must stay valid for the whole scan; it does —
    /// the ClientContext lives for the whole table-function execution.
    /// </summary>
    public static IAsyncEnumerable<RecordBatch> Stream(
        nint opener, string path, IReadOnlyList<string>? columns, Predicate? filter, CancellationToken ct)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var parquet = filter is null ? ParquetReadOptions.Default : new ParquetReadOptions { Filter = filter };
        var options = DeltaTableOptions.Default with { ParquetReadOptions = parquet };
        return StreamImpl(fs, options, columns, filter, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamImpl(
        DuckDbTableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns,
        Predicate? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var table = await DeltaTable.OpenAsync(fs, options, ct).ConfigureAwait(false);
        try
        {
            await foreach (var batch in table.ReadAllAsync(columns, filter, ct).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Like <see cref="Stream"/> but each batch carries a trailing non-null Int64
    /// <c>_metadata.row_id</c> column (the stable row-tracking id) — used to surface the DuckDB rowid for
    /// UPDATE/DELETE. Requires the table to have row tracking enabled (see <see cref="IsRowTrackingEnabled"/>).</summary>
    public static IAsyncEnumerable<RecordBatch> StreamWithRowIds(
        nint opener, string path, IReadOnlyList<string>? columns, Predicate? filter, CancellationToken ct)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var parquet = filter is null ? ParquetReadOptions.Default : new ParquetReadOptions { Filter = filter };
        var options = DeltaTableOptions.Default with { ParquetReadOptions = parquet };
        return StreamWithRowIdsImpl(fs, options, columns, filter, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamWithRowIdsImpl(
        DuckDbTableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns,
        Predicate? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var table = await DeltaTable.OpenAsync(fs, options, ct).ConfigureAwait(false);
        try
        {
            await foreach (var batch in table.ReadAllWithRowIdsAsync(columns, filter, ct).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the rows whose transient <c>_metadata.row_id</c> is in <paramref name="rowIds"/>
    /// (deletion vectors). Returns the number of rows deleted.</summary>
    public static long DeleteByRowIds(nint opener, string path, IReadOnlyCollection<long> rowIds, CancellationToken ct)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        // Open with the standard WRITE options (OmitPathInSchema=false) so the copy-on-write rewrite emits
        // standard-readable parquet — DeltaTableOptions.Default would drop path_in_schema (TProtocolException).
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            return table.DeleteByRowIdsAsync(rowIds, ct).AsTask().GetAwaiter().GetResult().RowsDeleted;
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DELETE");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // A rowid DELETE/UPDATE cannot be safely retried on a commit conflict: its absolute positions were computed
    // against the scanned snapshot, which a concurrent writer has changed. Surface a clear, retryable-by-the-user
    // error instead (re-running re-scans and recomputes the rowids).
    private static System.InvalidOperationException ConcurrentModification(string op) =>
        new($"delta: concurrent modification during {op} — another writer committed; the row positions are no "
            + "longer valid. Retry the statement.");

    /// <summary>True if the Delta table at <paramref name="path"/> has <c>delta.enableDeletionVectors=true</c>
    /// — DELETE then uses deletion vectors (no file rewrite) instead of copy-on-write.</summary>
    public static bool IsDeletionVectorsEnabled(nint opener, string path)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            return cfg is not null
                && cfg.TryGetValue("delta.enableDeletionVectors", out var v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>DELETE via deletion vectors (no file rewrite) — for tables with deletion vectors enabled.
    /// <paramref name="rowIds"/> are ABSOLUTE transient rowids. Returns rows deleted.</summary>
    public static long DeleteByRowIdsViaVectors(nint opener, string path, IReadOnlyCollection<long> rowIds,
                                                CancellationToken ct)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            return table.DeleteByRowIdsViaVectorsAsync(rowIds, ct).AsTask().GetAwaiter().GetResult().RowsDeleted;
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DELETE");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Per-file copy-on-write UPDATE: only files containing a target <paramref name="rowIds"/> are
    /// rewritten. <paramref name="rewriteFile"/> (ordinal, the file's batches) returns the same rows with the SET
    /// columns modified on matched positions (the caller owns the typed substitution); engineered-wood re-writes
    /// them as plain remove+add with a clean schema. Opens with the standard write options (path_in_schema).</summary>
    public static void UpdateByRowIds(nint opener, string path, IReadOnlyCollection<long> rowIds,
        System.Func<long, IReadOnlyList<RecordBatch>, IReadOnlyList<RecordBatch>> rewriteFile, CancellationToken ct)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.UpdateByRowIdsAsync(rowIds, rewriteFile, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("UPDATE");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
