using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge;

/// <summary>
/// Widens the newer narrow Arrow decimal types (<see cref="Decimal32Type"/> / <see cref="Decimal64Type"/>) to the
/// classic <see cref="Decimal128Type"/> on the READ boundary. engineered-wood's parquet reader represents a decimal
/// by its parquet physical width (INT32 =&gt; Decimal32, INT64 =&gt; Decimal64), whose VALUES are correct in C#, but
/// the Arrow C-data-interface handoff of the narrow decimal types is mishandled crossing to DuckDB (the exported
/// format string is read as 128-bit over the 4/8-byte buffer =&gt; corruption). Decimal128 is the universally-handled
/// width, so the Delta read path widens decimal columns (schema + batches) before export. Non-decimal and
/// already-128/256 columns pass through untouched. (WRITE is unaffected: DuckDB exports Decimal128 at the standard
/// boundary and engineered-wood writes it correctly — verified via DuckDB's native read_parquet.)
/// </summary>
internal static class DecimalWidening
{
    private static bool IsNarrow(IArrowType t) => t is Decimal32Type or Decimal64Type;

    /// <summary>Rewrites Decimal32/64 fields to Decimal128 (same precision/scale); returns the same schema when none.</summary>
    public static Schema WidenSchema(Schema schema)
    {
        if (!schema.FieldsList.Any(f => IsNarrow(f.DataType)))
        {
            return schema;
        }
        var fields = new List<Field>(schema.FieldsList.Count);
        foreach (var f in schema.FieldsList)
        {
            fields.Add(IsNarrow(f.DataType) ? new Field(f.Name, To128(f.DataType), f.IsNullable, f.Metadata) : f);
        }
        return new Schema(fields, schema.Metadata);
    }

    /// <summary>Rebuilds any Decimal32/64 column of the batch as a Decimal128Array (values preserved); returns the
    /// same batch when it has no narrow decimals.</summary>
    public static RecordBatch WidenBatch(RecordBatch batch)
    {
        if (!batch.Schema.FieldsList.Any(f => IsNarrow(f.DataType)))
        {
            return batch;
        }
        var fields = new List<Field>(batch.ColumnCount);
        var columns = new IArrowArray[batch.ColumnCount];
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            var f = batch.Schema.FieldsList[i];
            var col = batch.Column(i);
            if (IsNarrow(f.DataType))
            {
                var to = (Decimal128Type)To128(f.DataType);
                var builder = new Decimal128Array.Builder(to);
                for (int r = 0; r < col.Length; r++)
                {
                    decimal? v = col switch
                    {
                        Decimal32Array d32 => d32.GetValue(r),
                        Decimal64Array d64 => d64.GetValue(r),
                        _ => null,
                    };
                    if (v.HasValue) { builder.Append(v.Value); } else { builder.AppendNull(); }
                }
                columns[i] = builder.Build();
                fields.Add(new Field(f.Name, to, f.IsNullable, f.Metadata));
            }
            else
            {
                columns[i] = col;
                fields.Add(f);
            }
        }
        return new RecordBatch(new Schema(fields, batch.Schema.Metadata), columns, batch.Length);
    }

    /// <summary>Widens every batch of a stream (for the scan / CDF / rowid read paths).</summary>
    public static async IAsyncEnumerable<RecordBatch> WidenBatches(
        IAsyncEnumerable<RecordBatch> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var batch in source.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return WidenBatch(batch);
        }
    }

    private static IArrowType To128(IArrowType t) => t switch
    {
        Decimal32Type d => new Decimal128Type(d.Precision, d.Scale),
        Decimal64Type d => new Decimal128Type(d.Precision, d.Scale),
        _ => t,
    };
}
