using System.Collections.Generic;
using Apache.Arrow;
using EngineeredWood.DeltaLake.Table;

namespace ArrowNet.Bridge;

/// <summary>
/// Reads a Delta Lake table (Curt Hagenlocher's engineered-wood, pure C#) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via <see cref="DuckDbTableFileSystem"/> — so Delta tables on local/az://-s3://-https://
/// paths read with DuckDB's secrets + backends. Surfaced to DuckDB as the <c>arrownet_delta_scan(path)</c>
/// table function (see the <c>delta_schema</c>/<c>delta_scan</c> ABI handlers in <c>Bootstrap</c>).
/// </summary>
internal static class DeltaReader
{
    /// <summary>Opens the Delta table at <paramref name="path"/> and returns its Arrow schema only (no data
    /// read). Used at table-function bind. <paramref name="opener"/> = the calling operator's ClientContext.</summary>
    public static Schema GetSchema(nint opener, string path)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        return table.ArrowSchema;
    }

    /// <summary>Opens + reads the whole Delta table at <paramref name="path"/>, materializing every
    /// <see cref="RecordBatch"/> in managed memory (so the result is independent of the opener's lifetime —
    /// all host IO happens before this returns).</summary>
    public static InMemoryArrayStream Scan(nint opener, string path)
    {
        var fs = new DuckDbTableFileSystem(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        var schema = table.ArrowSchema;

        var batches = new List<RecordBatch>();
        var e = table.ReadAllAsync().GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().GetAwaiter().GetResult())
            {
                batches.Add(e.Current);
            }
        }
        finally
        {
            e.DisposeAsync().GetAwaiter().GetResult();
        }
        return new InMemoryArrayStream(schema, batches);
    }
}
