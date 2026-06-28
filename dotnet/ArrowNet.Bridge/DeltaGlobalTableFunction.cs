using System.Collections.Generic;
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
        var fs = new DuckDbTableFileSystem(opener, path);
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

        // engineered-wood defaults OmitPathInSchema=true, which drops the `path_in_schema` field from each
        // column chunk's parquet footer. That field is REQUIRED in the shipping Parquet Thrift definitions used
        // by DuckDB (Apache Thrift C++), arrow-rs/delta-kernel, and Fabric — omitting it makes them reject the
        // file ("TProtocolException: Invalid data" = required-field-missing). Force it on for portable output.
        var options = DeltaTableOptions.Default with
        {
            ParquetWriteOptions = new ParquetWriteOptions { OmitPathInSchema = false },
        };
        var table = DeltaTable.OpenOrCreateAsync(fs, schema, options, cancellationToken: ct)
            .AsTask().GetAwaiter().GetResult();
        try
        {
            // Overwrite so the demo is idempotent (the table is always exactly these 5 rows, re-run-safe).
            long version = table.WriteAsync(new[] { batch }, DeltaWriteMode.Overwrite, ct)
                .AsTask().GetAwaiter().GetResult();
            return (version, rows);
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
