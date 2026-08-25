using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.SqlServer;

/// <summary>
/// Appends <c>_changed_columns</c> — the NAMES of the columns an update recorded — to every batch of a
/// <c>cdc.changes(… images := 'both')</c> read, decoded from <c>_update_mask</c>.
/// </summary>
/// <remarks>
/// <para><b>⚠⚠ IT EXISTS BECAUSE THE RAW MASK IS A TRAP, and the trap is measured.</b> §1.4 documents
/// "bit index = <c>column_ordinal − 1</c>, little-endian within each byte", which is true and says NOTHING
/// about byte order. MEASURED on a ten-column table: ordinal 2 sets <c>0x0002</c> and ordinal 9 sets
/// <c>0x0100</c>, so ordinals 1-8 live in the LAST byte — the mask is a big-endian bit string over the WHOLE
/// <c>varbinary</c>, and the natural <c>floor((ord-1)/8)</c> byte index picks the wrong end. A consumer
/// decoding that way silently mis-reads every table with more than eight captured columns. The mask still
/// ships (it is the raw truth); this is the answer most callers actually want.</para>
/// <para><b>⚠⚠ A MASK IS ONLY DECODABLE AGAINST THE INSTANCE THAT PRODUCED IT</b>, which is why the map is
/// keyed by capture instance rather than being one list. A two-instance read (§19) unions two change tables
/// whose ordinals need not agree, and the aligned output order is neither instance's. <c>_capture_instance</c>
/// is what says which map a row belongs to — the column §18.2 shipped early, doing a third job.</para>
/// <para><b>⚠ THE COLUMN GOES AT THE END OF THE SCHEMA, NOT INTO THE METADATA BLOCK, and that is a safety
/// choice rather than a taste one.</b> <c>meta</c> indexes BOTH the SQL statement's own columns and the
/// declared schema, and the two agree only while every declared metadata column is also a SELECT-list
/// column. This one is computed HERE and has no SQL counterpart, so putting it in the block would split
/// <c>meta</c> into two counts across six call sites — and getting that wrong shifts every captured column
/// by one, silently, which is the failure <c>CdcDeclare</c>'s own name check exists to prevent.</para>
/// <para>⚠ NULL for a row with no mask — a <c>include := 'snapshot'</c> baseline row, which is state rather
/// than an update. NOT an empty list: "this update changed nothing" and "this row is not an update" are
/// different claims, and an empty list would assert the first.</para>
/// <para>⚠ An INSERT and a DELETE carry an all-bits-set mask (MEASURED: <c>0x0F</c> on four columns,
/// <c>0x03FF</c> on ten), so they list EVERY captured column. That is SQL Server's answer, reported rather
/// than reinterpreted — an insert does set every column.</para>
/// </remarks>
internal sealed class CdcChangedColumnsStream : IArrowArrayStream
{
    /// <summary>The output column this stream appends.</summary>
    internal const string ColumnName = "_changed_columns";

    private readonly IArrowArrayStream _inner;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _byInstance;
    private readonly IReadOnlyList<string>? _single;
    private readonly int _maskIndex;
    private readonly int _instanceIndex;

    /// <summary>
    /// The field a declaring plan must append to its output schema, so the DECLARED and ARRIVED schemas
    /// agree at the arrival check.
    /// </summary>
    /// <remarks>
    /// ⚠ The item field is NOT nullable: a captured column always has a name, so a NULL element would be
    /// meaningless. The LIST itself is nullable — that is where "no mask" is expressed.
    /// </remarks>
    internal static Field DeclaredField() =>
        new(ColumnName, new ListType(new Field("item", StringType.Default, nullable: false)), nullable: true);

    /// <summary>
    /// Wraps <paramref name="inner"/>, appending <see cref="ColumnName"/> to its schema and to every batch.
    /// </summary>
    /// <remarks>
    /// <b>⚠⚠ THE SCHEMA IS DERIVED FROM THE INNER ONE, NEVER TAKEN FROM THE PLAN'S DECLARATION — taking the
    /// declaration would make the ARRIVAL CHECK VACUOUS.</b> That check compares what the plan DECLARED at
    /// bind against what the statement actually returned, and it is the only thing standing between a type
    /// that moved under us and a silently mis-read column. Reporting the declaration here would have it
    /// compare the declaration to itself and pass unconditionally. The index lookups are by NAME on the
    /// inner schema for the same reason: they must reflect what arrived.
    /// </remarks>
    internal CdcChangedColumnsStream(IArrowArrayStream inner,
                                     IReadOnlyDictionary<string, IReadOnlyList<string>> byInstance)
    {
        _inner = inner;
        _byInstance = byInstance;
        var fields = new List<Field>(inner.Schema.FieldsList.Count + 1);
        fields.AddRange(inner.Schema.FieldsList);
        fields.Add(DeclaredField());
        Schema = new Schema(fields, inner.Schema.Metadata);
        _maskIndex = IndexOf(inner.Schema, "_update_mask");
        _instanceIndex = IndexOf(inner.Schema, "_capture_instance");
        // ⚠ A one-instance read is the common case and its rows all carry the same map, so it is resolved
        // ONCE here rather than by a dictionary lookup per row.
        _single = null;
        foreach (var pair in byInstance)
        {
            if (_single is null)
            {
                _single = pair.Value;
            }
            else
            {
                _single = null;
                break;
            }
        }
    }

    public Schema Schema { get; }

    private static int IndexOf(Schema schema, string name)
    {
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            if (string.Equals(schema.FieldsList[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(
        CancellationToken cancellationToken = default)
    {
        var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }
        // ⚠ OWNERSHIP TRANSFERS: the new batch is built over the SAME arrays, so the inner batch must NOT
        // be disposed here — disposing a RecordBatch disposes its arrays, and the consumer owns them now.
        var columns = new List<IArrowArray>(batch.ColumnCount + 1);
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            columns.Add(batch.Column(i));
        }
        columns.Add(BuildChangedColumns(batch));
        return new RecordBatch(Schema, columns, batch.Length);
    }

    private IArrowArray BuildChangedColumns(RecordBatch batch)
    {
        var mask = _maskIndex >= 0 ? batch.Column(_maskIndex) as BinaryArray : null;
        var instances = _instanceIndex >= 0 ? batch.Column(_instanceIndex) as StringArray : null;
        var builder = new ListArray.Builder(new Field("item", StringType.Default, nullable: false));
        var values = (StringArray.Builder)builder.ValueBuilder;
        for (int row = 0; row < batch.Length; row++)
        {
            if (mask is null || mask.IsNull(row))
            {
                builder.AppendNull();
                continue;
            }
            var names = _single;
            if (names is null)
            {
                string? instance = instances is null || instances.IsNull(row) ? null : instances.GetString(row);
                if (instance is null || !_byInstance.TryGetValue(instance, out names))
                {
                    // ⚠ UNKNOWN INSTANCE ⇒ NULL, never a guess. Decoding against the wrong instance's
                    // ordinals would name the WRONG columns, which is worse than saying nothing.
                    builder.AppendNull();
                    continue;
                }
            }
            builder.Append();
            AppendNames(values, mask.GetBytes(row), names);
        }
        return builder.Build();
    }

    /// <summary>
    /// Appends the names whose bit is set, reading bit <c>ordinal − 1</c> from the RIGHT END of the mask.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>bytes[bytes.Length - 1 - (bit / 8)]</c> is the whole point: the mask is big-endian over the
    /// array, so ordinal 1 lives in the LAST byte. Indexing from the front is the documented-looking rule
    /// that is wrong past eight columns.
    /// </remarks>
    private static void AppendNames(StringArray.Builder values, ReadOnlySpan<byte> bytes,
                                    IReadOnlyList<string> names)
    {
        for (int i = 0; i < names.Count; i++)
        {
            int bit = i; // ordinal (i + 1) - 1
            int fromEnd = bit / 8;
            if (fromEnd >= bytes.Length)
            {
                continue;
            }
            byte b = bytes[bytes.Length - 1 - fromEnd];
            if ((b & (1 << (bit % 8))) != 0)
            {
                values.Append(names[i]);
            }
        }
    }

    public void Dispose() => _inner.Dispose();
}
