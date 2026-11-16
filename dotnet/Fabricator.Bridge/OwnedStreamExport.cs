// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.C;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Exports a stream whose batches the caller hands over, and disposes each batch once it has been exported.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> Up to Arrow 23 the C stream exporter does not dispose the batches it reads: the exported array
/// takes its own reference to each buffer (<c>ArrowBuffer.TryExport</c> retains the shared handle) and gives that
/// back when the consumer releases, but the batch's own reference stays with the batch, which nobody holds any more.
/// The buffers are then freed by the finalizer. For a batch imported from DuckDB and passed straight back (a bulk
/// write's input to a host query), that kept DuckDB's memory until a GC: measured 8% of every batch DuckDB handed us
/// across the hermetic suites.</para>
/// <para>Disposing the batch right after its export is safe because the export already holds its own reference: the
/// memory lives until the consumer releases the array. It is done when the NEXT batch is read (and at the end), since
/// the exporter exports a batch inside the same <c>get_next</c> call that read it. arrow-dotnet #483 (after 23.0.0)
/// makes the exporter dispose the batch itself on release; disposing the same buffers again then is a no-op, so this
/// stays correct on both.</para>
/// <para>⚠ Only for streams that hand over their batches. A stream that returns a batch it still uses (a cached or
/// borrowed one) must return a view instead — <see cref="BorrowedBatch.View(RecordBatch)"/>.</para>
/// </remarks>
internal static class OwnedStreamExport
{
    public static unsafe void Export(IArrowArrayStream stream, CArrowArrayStream* target) =>
        CArrowArrayStreamExporter.ExportArrayStream(new ReleaseAfterExport(stream), target);

    private sealed class ReleaseAfterExport : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private RecordBatch? _exported;

        public ReleaseAfterExport(IArrowArrayStream inner) => _inner = inner;

        public Schema Schema => _inner.Schema;

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            ReleaseExported();
            _exported = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            return _exported;
        }

        public void Dispose()
        {
            ReleaseExported();
            _inner.Dispose();
        }

        private void ReleaseExported()
        {
            var batch = _exported;
            _exported = null;
            batch?.Dispose();
        }
    }
}
