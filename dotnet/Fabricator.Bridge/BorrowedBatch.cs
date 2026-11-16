// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// A view over a batch the caller does NOT own, for handing it to the C stream exporter.
/// </summary>
/// <remarks>
/// <para><b>Why a view and not the batch itself.</b> Since arrow-dotnet #483 (on main after 23.0.0) the C stream
/// exporter takes ownership of every batch a stream returns and DISPOSES it when the consumer releases the exported
/// array. A stream that hands out a borrowed batch therefore frees the owner's buffers: the owner (engineered-wood's
/// row-mask reshaping, a registered rows source scanned twice) then reads released memory — an
/// <see cref="System.ObjectDisposedException"/> on a <c>SharedMemoryHandle</c>, or a segfault inside DuckDB when the
/// freed buffer is native. Measured: verify_delta_catalog_dynamic_filter, verify_decision_eval/_coverage and
/// verify_plugin_fluid all fail on Arrow main without this, and all pass on 23.0.0.</para>
/// <para>Each buffer is <see cref="ArrowBuffer.Retain"/>ed, not copied: a reference-counted buffer gains one
/// reference that the exporter's dispose gives back, and plain memory is shared as it was. Children and dictionaries
/// are viewed the same way. <c>Retain</c> is public since 23.0.0 (#297), so this works on every Arrow we build
/// against.</para>
/// </remarks>
internal static class BorrowedBatch
{
    public static RecordBatch View(RecordBatch batch)
    {
        var columns = new IArrowArray[batch.ColumnCount];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = View(batch.Column(i));
        }
        return new RecordBatch(batch.Schema, columns, batch.Length);
    }

    public static IArrowArray View(IArrowArray array) => ArrowArrayFactory.BuildArray(View(array.Data));

    /// <summary>
    /// A deep copy of <paramref name="batch"/> in MANAGED memory, for handing a small imported batch (the 1-row bind
    /// arguments) to code that may keep it: the copy is collected like any object, so the imported original can be
    /// released at once instead of holding DuckDB's memory until a finalizer runs.
    /// </summary>
    public static RecordBatch Copy(RecordBatch batch)
    {
        var columns = new IArrowArray[batch.ColumnCount];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = ArrowArrayFactory.BuildArray(Copy(batch.Column(i).Data));
        }
        return new RecordBatch(batch.Schema, columns, batch.Length);
    }

    private static ArrayData Copy(ArrayData data)
    {
        ArrowBuffer[]? buffers = null;
        if (data.Buffers is { } source)
        {
            buffers = new ArrowBuffer[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                buffers[i] = source[i].IsEmpty ? ArrowBuffer.Empty : new ArrowBuffer(source[i].Span.ToArray());
            }
        }
        ArrayData[]? children = null;
        if (data.Children is { } kids)
        {
            children = new ArrayData[kids.Length];
            for (int i = 0; i < kids.Length; i++)
            {
                children[i] = Copy(kids[i]);
            }
        }
        return new ArrayData(data.DataType, data.Length, data.NullCount, data.Offset, buffers, children,
                             data.Dictionary is null ? null : Copy(data.Dictionary));
    }

    private static ArrayData View(ArrayData data)
    {
        ArrowBuffer[]? buffers = null;
        if (data.Buffers is { } source)
        {
            buffers = new ArrowBuffer[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                buffers[i] = source[i].Retain();
            }
        }
        ArrayData[]? children = null;
        if (data.Children is { } kids)
        {
            children = new ArrayData[kids.Length];
            for (int i = 0; i < kids.Length; i++)
            {
                children[i] = View(kids[i]);
            }
        }
        return new ArrayData(data.DataType, data.Length, data.NullCount, data.Offset, buffers, children,
                             data.Dictionary is null ? null : View(data.Dictionary));
    }
}
