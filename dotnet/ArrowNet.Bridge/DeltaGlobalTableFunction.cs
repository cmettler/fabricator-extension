using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;
using EngineeredWood.Parquet;

namespace ArrowNet.Bridge;

/// <summary>
/// <c>arrownet_delta_scan(path)</c> — a connection-free GLOBAL <b>host-FS</b> table function: reads a Delta
/// Lake table (Curt Hagenlocher's engineered-wood, pure C#) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via <see cref="DuckDbTableFileSystem"/>, so local / az:// / s3:// / https:// paths and
/// DuckDB secrets all work (one auth config shared with native reads).
///
/// This is the reference <b>host-FS reader</b>: it needs the calling operator's <c>ClientContext</c> (for
/// secret resolution) at both bind (read the Delta log → schema) and execute (read data). That opener isn't an
/// argument of the generic table-function path, so it's read from <see cref="AmbientOpener.Current"/>, which
/// the host sets via <c>set_active_opener</c> immediately before each bind/execute. A new lakehouse format
/// (Iceberg/Lance/…) is added the same way — a pure-C# <see cref="ITableFunction"/> declared as a global, no
/// bespoke C++/ABI. See docs/global-functions.md §host-FS + docs/filesystem-bridge.md.
/// </summary>
public sealed class DeltaGlobalTableFunction : ITableFunction
{
    public string Name => "arrownet_delta_scan";

    public Schema Parameters { get; } =
        new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, metadata: null);

    // Delta/Parquet statistics are byte-ordered (UTF-8 binary), matching DuckDB's default binary string
    // comparison — so string ordering comparisons + BETWEEN are superset-safe to push into file/row-group
    // skipping (the C++ FilterSerializer honors this; DuckDB re-applies regardless).
    public bool StringOrderPushable => true;

    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        var path = ((StringArray)args.Column(0)).GetString(0)
                   ?? throw new System.ArgumentException("arrownet_delta_scan: path must not be NULL");
        // The opener (this operator's ClientContext) is valid for the duration of this synchronous bind —
        // read the Delta table's schema now (no data read).
        var schema = DeltaReader.GetSchema(AmbientOpener.Current, path);
        return new DeltaBinding(path, schema);
    }

    private sealed class DeltaBinding : IArrowTableFunctionBinding
    {
        private readonly string _path;
        private readonly Schema _schema;

        public DeltaBinding(string path, Schema schema)
        {
            _path = path;
            _schema = schema;
        }

        public Schema OutputSchema => _schema;

        // engineered-wood honors the projection (reads only the requested columns) and pushes the filter into
        // file + row-group skipping; it does NOT re-apply the predicate per row, so the result is a SUPERSET —
        // DuckDB re-applies the projection (by name) + every filter above the scan. (The host already maps the
        // result columns by name regardless of this flag for a global table function.)
        public bool SupportsPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Capture the opener (this operator's ClientContext) NOW — it stays valid for the whole execution,
            // so the lazy stream below can read files through it as the host pulls batches (no materialization).
            var opener = AmbientOpener.Current;
            var spec = scan.Spec;
            // Push the FILTER for file + row-group skipping (doesn't change the result schema). Read the filter
            // constants + map the predicate eagerly (the constants batch is in-memory; this consumes + disposes
            // scan.FilterValues). A node we can't safely push is dropped (superset-safe); DuckDB re-applies.
            var filter = spec?.Filter is { } node
                ? new DeltaFilterBuilder(ReadValues(scan.FilterValues)).Build(node)
                : null;
            // Column PROJECTION is intentionally NOT pushed into engineered-wood here: the shared
            // BindingBoundTable wraps this stream with the binding's FULL OutputSchema, so returning a
            // projected column subset would mismatch the declared schema (arrow_ingest SIGSEGV). DuckDB still
            // projects columns above the scan (by name). True column-pruning into the Parquet read would need
            // a pushdown-native bound table that declares the projected schema — see docs/filesystem-bridge.md.
            return DeltaReader.Stream(opener, _path, columns: null, filter, ct);
        }

        private static IReadOnlyList<object?> ReadValues(IArrowArrayStream? filterValues)
        {
            if (filterValues is null)
            {
                return System.Array.Empty<object?>();
            }
            using (filterValues)
            {
                var batch = filterValues.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                if (batch is null)
                {
                    return System.Array.Empty<object?>();
                }
                var values = new object?[batch.ColumnCount];
                for (int i = 0; i < batch.ColumnCount; i++)
                {
                    try
                    {
                        values[i] = ArrowValueReader.ReadScalar(batch.Column(i), 0);
                    }
                    catch (System.NotSupportedException)
                    {
                        values[i] = null; // unmappable Arrow type → that predicate node won't push (DuckDB re-applies)
                    }
                }
                return values;
            }
        }

        public void Dispose() { }
    }
}

/// <summary>
/// <c>arrownet_delta_write_demo(path)</c> — a connection-free GLOBAL host-FS table function that WRITES a small
/// fixed Delta table (5 rows: <c>id BIGINT</c>, <c>name VARCHAR</c>) at <paramref name="path"/> via
/// engineered-wood through the host FileSystem write callbacks, and returns one row <c>(version, rows_written)</c>.
/// A spike that proves the write bridge end-to-end (round-trips with <c>arrownet_delta_scan</c>); the write goes
/// through <see cref="DuckDbTableFileSystem"/>, whose commit uses the put-if-absent EXCLUSIVE_CREATE primitive
/// (validated on OneLake). The opener (this operator's ClientContext) is read from <see cref="AmbientOpener"/>.
/// </summary>
public sealed class DeltaWriteDemoFunction : ITableFunction
{
    public string Name => "arrownet_delta_write_demo";

    public Schema Parameters { get; } =
        new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, metadata: null);

    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        var path = ((StringArray)args.Column(0)).GetString(0)
                   ?? throw new System.ArgumentException("arrownet_delta_write_demo: path must not be NULL");
        var outSchema = new Schema(new[]
        {
            new Field("version", Int64Type.Default, nullable: false),
            new Field("rows_written", Int64Type.Default, nullable: false),
        }, metadata: null);
        return new WriteBinding(path, outSchema);
    }

    private sealed class WriteBinding : IArrowTableFunctionBinding
    {
        private readonly string _path;
        private readonly Schema _schema;

        public WriteBinding(string path, Schema schema)
        {
            _path = path;
            _schema = schema;
        }

        public Schema OutputSchema => _schema;
        public bool SupportsPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Write synchronously while the opener (captured now) is valid, then yield the result row.
            var (version, rows) = WriteDemoTable(AmbientOpener.Current, _path, ct);
            var batch = new RecordBatch(_schema, new IArrowArray[]
            {
                new Int64Array.Builder().Append(version).Build(),
                new Int64Array.Builder().Append(rows).Build(),
            }, length: 1);
            return Single(batch);
        }

        private static async IAsyncEnumerable<RecordBatch> Single(RecordBatch batch)
        {
            yield return batch;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose() { }
    }

    private static (long Version, long Rows) WriteDemoTable(nint opener, string path, CancellationToken ct)
    {
        var schema = new Schema(new[]
        {
            new Field("id", Int64Type.Default, nullable: false),
            new Field("name", StringType.Default, nullable: false),
        }, metadata: null);

        const int rows = 5;
        var ids = new Int64Array.Builder();
        var names = new StringArray.Builder();
        for (long i = 1; i <= rows; i++)
        {
            ids.Append(i);
            names.Append($"row_{i}");
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { ids.Build(), names.Build() }, rows);
        long version = DeltaWriter.WriteOverwrite(opener, path, schema, new[] { batch }, ct);
        return (version, rows);
    }
}

/// <summary>
/// Writes a Delta Lake table via engineered-wood through the host FileSystem (the <see cref="DuckDbTableFileSystem"/>
/// write side + the put-if-absent EXCLUSIVE_CREATE commit). Forces <c>OmitPathInSchema=false</c> so the parquet
/// footer carries the REQUIRED <c>path_in_schema</c> field — otherwise standard readers (DuckDB/arrow-rs/Fabric)
/// reject the file. The opener is the calling operator's ClientContext (valid for the write call).
/// </summary>
internal static class DeltaWriter
{
    /// <summary>Delta table options for ALL engineered-wood writes (initial write AND the copy-on-write DELETE
    /// rewrite): the parquet writer MUST emit <c>path_in_schema</c> (OmitPathInSchema=false) or standard readers
    /// (delta-kernel / Spark / Fabric) reject the footer with <c>TProtocolException: Invalid data</c>.</summary>
    internal static DeltaTableOptions Options() => DeltaTableOptions.Default with
    {
        ParquetWriteOptions = new ParquetWriteOptions
        {
            OmitPathInSchema = false, // REQUIRED field — standard readers (DuckDB/arrow-rs/Fabric) reject without it
            RowGroupMaxRows = 122880, // DuckDB's default row-group size
        },
    };

    // Catalog tables are written as PLAIN Delta — NO table features (no row tracking, no deletion vectors).
    // The DuckDB rowid for UPDATE/DELETE is a TRANSIENT (file, position) rowid computed at scan time
    // (engineered-wood ReadAllWithRowIdsAsync), and DELETE is copy-on-write (rewrite the file, plain add/remove).
    // Plain Delta (minReader 1 / minWriter 2) is maximally reader-compatible — Fabric OneLake conversion + Spark
    // can't read our row-tracking/deletion-vector commits (engineered-wood's DV format isn't Spark-compatible).

    // Opt-in table features for the deletion-vector fast-delete mode (DELETE marks rows in a DV instead of
    // rewriting the file). Row tracking is enabled alongside (per the chosen design); both are declared in the
    // protocol by CreateAsync (reader v3 + deletionVectors/rowTracking/domainMetadata). A table created with
    // this config is recognized by DeltaReader.IsDeletionVectorsEnabled so DELETE picks the DV path.
    private static readonly Dictionary<string, string> DeletionVectorConfig = new()
    {
        ["delta.enableDeletionVectors"] = "true",
        ["delta.enableRowTracking"] = "true",
    };

    // Optimistic-concurrency retry bound for commits. A concurrent writer that commits our target version
    // first makes engineered-wood throw DeltaConflictException; we reopen (picking up the new latest version)
    // and retry. Safe for append/overwrite/create — the data doesn't depend on the conflicting commit. Rowid
    // DELETE/UPDATE do NOT retry (their absolute positions are tied to the scanned snapshot — a concurrent
    // change invalidates them; DeltaReader surfaces a clear conflict error instead).
    internal const int MaxCommitAttempts = 16;

    /// <summary>Opens-or-creates the Delta table at <paramref name="path"/> and writes <paramref name="batches"/>
    /// in <paramref name="mode"/> (Overwrite for CTAS/REPLACE, Append for INSERT). Returns the committed version.
    /// <paramref name="deletionVectors"/> enables the DV+rowTracking features on a NEW table (opt-in fast-delete).
    /// Retries on a commit conflict (concurrent writer) by reopening at the new latest version (OCC).</summary>
    public static long Write(nint opener, string path, Schema schema, IReadOnlyList<RecordBatch> batches,
                             DeltaWriteMode mode, CancellationToken ct, bool deletionVectors = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            var fs = new DuckDbTableFileSystem(opener, path);
            var table = DeltaTable.OpenOrCreateAsync(fs, schema, Options(),
                                                     configuration: deletionVectors ? DeletionVectorConfig : null,
                                                     cancellationToken: ct).AsTask().GetAwaiter().GetResult();
            try
            {
                return table.WriteAsync(batches, mode, ct).AsTask().GetAwaiter().GetResult();
            }
            catch (EngineeredWood.DeltaLake.DeltaConflictException) when (attempt < MaxCommitAttempts)
            {
                // Concurrent writer took our version — reopen + retry (append/overwrite is snapshot-independent).
            }
            finally
            {
                table.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    public static long WriteOverwrite(nint opener, string path, Schema schema, IReadOnlyList<RecordBatch> batches,
                                      CancellationToken ct) =>
        Write(opener, path, schema, batches, DeltaWriteMode.Overwrite, ct);

    /// <summary>Creates an empty Delta table (commit 0 with the schema, no data) at <paramref name="path"/>.
    /// <paramref name="deletionVectors"/> enables the DV+rowTracking features (opt-in fast-delete).</summary>
    public static void Create(nint opener, string path, Schema schema, CancellationToken ct,
                              bool deletionVectors = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            var fs = new DuckDbTableFileSystem(opener, path);
            try
            {
                // OpenOrCreate writes commit-0 for a new table (or opens an existing one — no commit, no conflict).
                var table = DeltaTable.OpenOrCreateAsync(fs, schema, Options(),
                                                         configuration: deletionVectors ? DeletionVectorConfig : null,
                                                         cancellationToken: ct).AsTask().GetAwaiter().GetResult();
                table.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return;
            }
            catch (EngineeredWood.DeltaLake.DeltaConflictException) when (attempt < MaxCommitAttempts)
            {
                // A concurrent creator won commit-0 — retry (the next OpenOrCreate will just open it).
            }
        }
    }

    /// <summary>Materializes a (possibly streamed) Arrow stream into independent in-memory batches via an Arrow
    /// IPC round-trip — the source batches may be freed after consumption, and engineered-wood's WriteAsync
    /// needs them retained for one commit. Returns the schema, batches, and total row count.</summary>
    public static (Schema Schema, List<RecordBatch> Batches, long Rows) Materialize(
        IArrowArrayStream stream, CancellationToken ct)
    {
        var schema = stream.Schema;
        var ms = new MemoryStream();
        long rows = 0;
        using (var w = new ArrowStreamWriter(ms, schema, leaveOpen: true))
        {
            RecordBatch? b;
            while ((b = stream.ReadNextRecordBatchAsync(ct).AsTask().GetAwaiter().GetResult()) is not null)
            {
                if (b.Length == 0)
                {
                    continue;
                }
                w.WriteRecordBatchAsync(b, ct).GetAwaiter().GetResult();
                rows += b.Length;
            }
            w.WriteEndAsync(ct).GetAwaiter().GetResult();
        }
        var batches = new List<RecordBatch>();
        ms.Position = 0;
        using (var r = new ArrowStreamReader(ms))
        {
            RecordBatch? b;
            while ((b = r.ReadNextRecordBatchAsync(ct).AsTask().GetAwaiter().GetResult()) is not null)
            {
                batches.Add(b);
            }
        }
        return (schema, batches, rows);
    }
}

/// <summary>
/// <c>arrownet_delta_write(&lt;input&gt;, path := '…')</c> — a connection-free GLOBAL host-FS <b>collector</b>
/// that writes ANY input table (a DuckDB query result) to a Delta Lake table at <c>path</c> on OneLake/ADLS/
/// local (Overwrite), returning one row <c>(version, rows_written)</c>. The collector buffers all input, copies
/// it (via an Arrow IPC round-trip — the input batches are freed after consumption), and commits one Delta
/// version through <see cref="DeltaWriter"/>. The opener is threaded via <see cref="AmbientOpener"/> (set by the
/// host before the collector runs). Single-writer; the commit is put-if-absent (EXCLUSIVE_CREATE). The written
/// table is standard-/Fabric-readable.
/// </summary>
public sealed class DeltaWriteCollectorFunction : ICollectorTableFunction
{
    public string Name => "arrownet_delta_write";

    // The actual input schema is supplied to Bind; this declared schema is only discovery metadata (the operator
    // registers a {TABLE} param that accepts any input).
    public Schema InputSchema { get; } = new Schema(System.Array.Empty<Field>(), metadata: null);

    public Schema Parameters { get; } =
        new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, metadata: null);

    public IArrowCollectorBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        var path = ReadPath(args);
        return new WriteCollectorBinding(path, inputSchema);
    }

    private static string ReadPath(RecordBatch? args)
    {
        if (args is not null)
        {
            for (int i = 0; i < args.ColumnCount; i++)
            {
                if (string.Equals(args.Schema.FieldsList[i].Name, "path", System.StringComparison.OrdinalIgnoreCase)
                    && args.Column(i) is StringArray s && s.Length > 0 && s.GetString(0) is { } p)
                {
                    return p;
                }
            }
        }
        throw new System.ArgumentException("arrownet_delta_write: the 'path' argument is required");
    }

    private sealed class WriteCollectorBinding : IArrowCollectorBinding
    {
        private readonly string _path;
        private readonly Schema _inputSchema;
        private readonly Schema _outputSchema;

        public WriteCollectorBinding(string path, Schema inputSchema)
        {
            _path = path;
            _inputSchema = inputSchema;
            _outputSchema = new Schema(new[]
            {
                new Field("version", Int64Type.Default, nullable: false),
                new Field("rows_written", Int64Type.Default, nullable: false),
            }, metadata: null);
        }

        public Schema OutputSchema => _outputSchema;

        public async IAsyncEnumerable<RecordBatch> Collect(
            IAsyncEnumerable<RecordBatch> allInput, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Capture the opener at entry (the operator set it before pulling); valid for the whole write.
            var opener = AmbientOpener.Current;

            // Copy the input out of its (transient) Arrow buffers via an IPC round-trip, since the operator frees
            // each batch after it's consumed and we must retain ALL rows to write one Delta commit.
            var ms = new MemoryStream();
            long rows = 0;
            using (var w = new ArrowStreamWriter(ms, _inputSchema, leaveOpen: true))
            {
                await foreach (var b in allInput.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (b.Length == 0)
                    {
                        continue;
                    }
                    await w.WriteRecordBatchAsync(b, ct).ConfigureAwait(false);
                    rows += b.Length;
                }
                await w.WriteEndAsync(ct).ConfigureAwait(false);
            }

            var batches = new List<RecordBatch>();
            ms.Position = 0;
            using (var r = new ArrowStreamReader(ms))
            {
                RecordBatch? b;
                while ((b = await r.ReadNextRecordBatchAsync(ct).ConfigureAwait(false)) is not null)
                {
                    batches.Add(b);
                }
            }

            long version = DeltaWriter.WriteOverwrite(opener, _path, _inputSchema, batches, ct);
            yield return new RecordBatch(_outputSchema, new IArrowArray[]
            {
                new Int64Array.Builder().Append(version).Build(),
                new Int64Array.Builder().Append(rows).Build(),
            }, length: 1);
        }

        public void Dispose() { }
    }
}
