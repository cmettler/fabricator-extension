using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.Expressions;
using Microsoft.Extensions.Logging;

namespace ArrowNet.Bridge;

/// <summary>
/// The native Delta reader: C# lists the table's exact active data files (via <see cref="DeltaReader"/>) and
/// DuckDB's own <c>read_parquet</c> does the decode — one query per file on a fresh host connection
/// (<see cref="Host.Query"/>), tuned + <c>ExternalFileCache</c>-backed, over <c>onelake://</c> for OneLake. Per
/// file it pushes the <b>static filter</b> into the <c>read_parquet WHERE</c> (row-group pruning), excludes the
/// file's <b>deletion vector</b>, projects the requested columns, and computes the transient
/// <c>_metadata.row_id = (fileOrdinal &lt;&lt; 40) | file_row_number</c> when requested — so DELETE/UPDATE work
/// natively (no fallback to the engineered-wood reader). Files are read with a bounded prefetch
/// (<c>ARROWNET_DELTA_PREFETCH</c>, default 1 = sequential; &gt;1 = concurrent file fetch — the cloud-I/O win).
///
/// <para>The per-file loop is the decision point that the single <c>read_parquet([list])</c> lacks: Delta-log
/// FILE pruning (skip a file whose stats can't match) + early-stop. It keeps <c>filter_pushdown = false</c>
/// (superset-safe; DuckDB re-applies every predicate above the scan), so a partial WHERE only forfeits pruning.
/// Dynamic (join) filter pushdown at this decision point is a later slice (a live-filter host callback).</para>
/// </summary>
internal static class DeltaNativeReader
{
    private const string RowIdColumn = "_metadata.row_id";
    private const int RowIdPositionBits = 40;
    private static readonly ILogger Log = ArrowNetLog.CreateLogger("ArrowNet.Delta.Native");

    /// <summary>Builds the Arrow stream for a native Delta scan. <paramref name="unit"/>/<paramref name="value"/>
    /// = the resolved time-travel/pinned snapshot ("version"/"timestamp"), or null for latest.</summary>
    public static IArrowArrayStream Read(
        nint opener, string path, Schema userSchema, ScanSpec? spec,
        IReadOnlyList<object?> filterValues, string? unit, string? value)
    {
        bool wantRowId = spec?.Columns is { } c0 && c0.Contains(RowIdColumn);
        var dataCols = spec?.Columns is { Count: > 0 } cols
            ? cols.Where(c => c != RowIdColumn).ToList()
            : userSchema.FieldsList.Select(f => f.Name).ToList();

        // Static filter → engineered-wood predicate (Delta-log FILE pruning) + SQL WHERE (read_parquet row-group pruning).
        Predicate? prune = spec?.Filter is { } node ? new DeltaFilterBuilder(filterValues).Build(node) : null;
        // Prefer the host's 1:1 native SQL rendering (literals inlined, DuckDB self-render → exact). It carries the
        // SAME superset-safe predicates as spec.Filter, so it's correctness-neutral (DuckDB re-applies above the
        // scan). Fall back to translating the FilterNode ourselves when the host didn't emit one.
        string? where = !string.IsNullOrEmpty(spec?.NativeFilter)
            ? spec!.NativeFilter
            : spec?.Filter is { } node2 ? DeltaSqlFilter.ToWhere(node2, filterValues) : null;

        var listing = DeltaReader.ListNativeScanFiles(opener, path, unit, value, prune, Log);
        var schema = ProbeSchema(listing, userSchema, dataCols, wantRowId);
        int prefetch = Prefetch();

        Log.LogInformation(
            "delta native scan {Path}: v{Version} files={Files} cols=[{Cols}] rowid={RowId} where=[{Where}] prefetch={Prefetch} colmap={Map}",
            path, listing.Version, listing.Files.Count, string.Join(",", dataCols), wantRowId, where ?? "", prefetch,
            listing.LogicalToPhysical is not null ? "name" : listing.LogicalToFieldId is not null ? "id" : "none");

        return new AsyncEnumerableArrowStream(schema, StreamFiles(listing, dataCols, wantRowId, where, prefetch));
    }

    // The per-file SELECT (ordinal folded into the rowid expression); file_row_number is read but only surfaces
    // as _metadata.row_id (and drives the DV exclusion) — never as an output column.
    private static string FileSql(IReadOnlyList<string> dataCols, bool wantRowId, string? where,
                                  DeltaReader.NativeScanFile f, IReadOnlyDictionary<string, string>? logToPhys)
    {
        // The scan source. No column mapping: read_parquet exposes the data columns by their logical name +
        // file_row_number. Column mapping (name/id): the file stores PHYSICAL names, so alias physical→logical in
        // an inner query — then the OUTER projection, user filter, rowid, and DV condition all reference logical
        // names + file_row_number unchanged (no filter rewrite; DuckDB pushes the outer filter into read_parquet).
        string source;
        if (logToPhys is null)
        {
            source = $"read_parquet('{f.Uri.Replace("'", "''")}', file_row_number => true)";
        }
        else
        {
            var inner = new List<string>(dataCols.Count + 1);
            foreach (var c in dataCols)
            {
                inner.Add(logToPhys.TryGetValue(c, out var phys) && phys != c
                    ? $"{Quote(phys)} AS {Quote(c)}"
                    : Quote(c));
            }
            inner.Add("file_row_number");
            source = $"(SELECT {string.Join(", ", inner)} FROM read_parquet('{f.Uri.Replace("'", "''")}', file_row_number => true))";
        }

        var sb = new StringBuilder("SELECT ");
        sb.Append(dataCols.Count == 0 ? "" : string.Join(", ", dataCols.Select(Quote)));
        if (wantRowId)
        {
            if (dataCols.Count > 0)
            {
                sb.Append(", ");
            }
            sb.Append($"((CAST({f.Ordinal.ToString(CultureInfo.InvariantCulture)} AS BIGINT) << {RowIdPositionBits}) | file_row_number) AS {Quote(RowIdColumn)}");
        }
        if (dataCols.Count == 0 && !wantRowId)
        {
            sb.Append("1"); // degenerate projection (e.g. COUNT(*) with no columns) — a constant keeps SQL valid
        }
        sb.Append($" FROM {source}");
        var conds = new List<string>(2);
        if (!string.IsNullOrEmpty(where))
        {
            conds.Add(where!);
        }
        if (f.Dv.Length > 0)
        {
            conds.Add($"file_row_number NOT IN ({string.Join(",", f.Dv.Select(p => p.ToString(CultureInfo.InvariantCulture)))})");
        }
        if (conds.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", conds));
        }
        return sb.ToString();
    }

    // Advertises the EXACT read_parquet output schema (probed via LIMIT 0 over any active file), so the streamed
    // batches match by type. With no files, derives it from the user schema (+ the rowid field).
    private static Schema ProbeSchema(DeltaReader.NativeScanList listing, Schema userSchema,
                                      IReadOnlyList<string> dataCols, bool wantRowId)
    {
        if (listing.AnyUri is { } probe)
        {
            var probeFile = new DeltaReader.NativeScanFile(0, probe, System.Array.Empty<long>());
            var sql = FileSql(dataCols, wantRowId, where: null, probeFile, LogToPhys(listing, probe)) + " LIMIT 0";
            using var s = Host.Query(sql);
            // Nested mapped fields: the probed schema carries physical struct-child names — rename to logical
            // (top level is already logical via the SELECT alias; the transform passes it through).
            return listing.MappedSchema is { } ms
                ? ArrowColumnMappingRename.RenameSchema(s.Schema, ms, toPhysical: false)
                : s.Schema;
        }
        var fields = new List<Field>();
        foreach (var c in dataCols)
        {
            var f = userSchema.GetFieldByName(c);
            if (f is not null)
            {
                fields.Add(f);
            }
        }
        if (wantRowId)
        {
            fields.Add(new Field(RowIdColumn, Int64Type.Default, nullable: false));
        }
        return new Schema(fields, userSchema.Metadata);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamFiles(
        DeltaReader.NativeScanList listing, IReadOnlyList<string> dataCols, bool wantRowId, string? where,
        int prefetch, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (listing.Files.Count == 0)
        {
            yield break;
        }
        // Bounded channel + a semaphore-gated pump: up to `prefetch` files fetched concurrently (default 1 =
        // sequential). Order across files is not preserved (DuckDB re-applies ORDER BY above the scan); the rowid
        // is per-file-correct regardless of read order.
        var channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(Math.Max(2, prefetch * 2))
        {
            SingleReader = true,
            SingleWriter = prefetch == 1,
        });
        var writer = channel.Writer;
        var pump = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(prefetch);
            var tasks = new List<Task>(listing.Files.Count);
            try
            {
                foreach (var f in listing.Files)
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    var file = f;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var sql = FileSql(dataCols, wantRowId, where, file, LogToPhys(listing, file.Uri));
                            Log.LogDebug("delta native file: {Sql}", sql);
                            using var s = Host.Query(sql);
                            while (true)
                            {
                                var b = await s.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
                                if (b is null)
                                {
                                    break;
                                }
                                // Nested mapped fields: rename physical struct-child names back to logical
                                // (zero-copy type-tree rewrap; top level already logical via the SELECT alias).
                                if (listing.MappedSchema is { } ms)
                                {
                                    b = ArrowColumnMappingRename.RenameBatch(b, ms, toPhysical: false);
                                }
                                await writer.WriteAsync(b, ct).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex);
            }
        }, ct);

        await foreach (var b in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return b;
        }
        await pump.ConfigureAwait(false); // observe pump faults
    }

    // Resolves the effective logical→physical column-name map for ONE file of a column-mapping table:
    //   • name mode → the file-independent physicalName map from the listing (probe-free);
    //   • id mode   → probe THIS file's parquet `field_id → physical name` (footer read only) and compose
    //                 logical → field_id → physical name (per-file, so it stays correct across a column RENAME
    //                 where old + new files store the same field_id under different physical names);
    //   • no mapping → null (FileSql reads by logical name directly).
    private static IReadOnlyDictionary<string, string>? LogToPhys(DeltaReader.NativeScanList listing, string uri)
    {
        if (listing.LogicalToPhysical is { } phys)
        {
            return phys;
        }
        if (listing.LogicalToFieldId is not { } logToFid)
        {
            return null;
        }
        var fieldIdToName = ProbeFieldIds(uri);
        var map = new Dictionary<string, string>();
        foreach (var kv in logToFid)
        {
            if (fieldIdToName.TryGetValue(kv.Value, out var physName) && physName != kv.Key)
            {
                map[kv.Key] = physName;
            }
        }
        return map.Count > 0 ? map : null;
    }

    // Reads a parquet file's `field_id → physical column name` (top-level + nested nodes) via parquet_schema —
    // a footer-only read. Rows with no field_id (e.g. the schema root) are skipped.
    private static Dictionary<int, string> ProbeFieldIds(string uri)
    {
        var result = new Dictionary<int, string>();
        var sql = $"SELECT name, CAST(field_id AS BIGINT) AS fid FROM parquet_schema('{uri.Replace("'", "''")}') WHERE field_id IS NOT NULL";
        using var s = Host.Query(sql);
        while (true)
        {
            var batch = s.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            using (batch)
            {
                var names = (StringArray)batch.Column(0);
                var fids = (Int64Array)batch.Column(1);
                for (int i = 0; i < batch.Length; i++)
                {
                    if (fids.IsValid(i) && !names.IsNull(i))
                    {
                        result[(int)fids.GetValue(i)!.Value] = names.GetString(i);
                    }
                }
            }
        }
        return result;
    }

    private static int Prefetch()
    {
        var text = Environment.GetEnvironmentVariable("ARROWNET_DELTA_PREFETCH");
        if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text, out var n) && n >= 1)
        {
            return Math.Min(n, 64);
        }
        return 1; // sequential by default; >1 opts into concurrent file fetch (the cloud-I/O win)
    }

    private static string Quote(string col) => "\"" + col.Replace("\"", "\"\"") + "\"";
}
