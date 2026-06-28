using System.Collections.Generic;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

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
