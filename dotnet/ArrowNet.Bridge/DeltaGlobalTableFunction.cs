using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

        // The Delta read returns the full set of columns; DuckDB re-applies projection (by name) + filters
        // above the scan. (engineered-wood file/row-group skipping via the scan's filter spec is a future
        // streaming refinement — see docs/filesystem-bridge.md "Next".)
        public bool SupportsPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Materialize the whole table NOW, while the opener is valid (this Execute body runs synchronously
            // — the actual host IO happens here, not lazily). The returned iterator only walks the in-memory
            // result, so it's safe to drain after the opener is gone.
            var stream = DeltaReader.Scan(AmbientOpener.Current, _path);
            return Drain(stream, ct);
        }

        private static async IAsyncEnumerable<RecordBatch> Drain(
            IArrowArrayStream stream, [EnumeratorCancellation] CancellationToken ct)
        {
            using (stream)
            {
                RecordBatch? batch;
                while ((batch = await stream.ReadNextRecordBatchAsync(ct).ConfigureAwait(false)) is not null)
                {
                    yield return batch;
                }
            }
        }

        public void Dispose() { }
    }
}
