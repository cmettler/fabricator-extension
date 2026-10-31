// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Fabricator.Bridge;

/// <summary>
/// DELETE / UPDATE / INSERT … RETURNING on a Delta table (MERGE … RETURNING runs its actions through these). The rows
/// come from the READ-BACK, not from the change feed: a DELETE returns the rows its row ids name, read at the version
/// the statement scanned; an UPDATE their post-image (the same read, SET values substituted — the merge the buffered
/// UPDATE writes); an INSERT its input in table order. All of it is computed BEFORE anything is written, so a
/// statement that cannot return its rows changes nothing — and it works inside BEGIN … COMMIT, where a change feed
/// would have no version to read until COMMIT.
/// </summary>
public sealed partial class DeltaCatalog
{
    public IArrowArrayStream ExecuteDeleteReturning(IReadOnlyList<string> schemaPath, string tableName,
                                                    IArrowArrayStream keys)
    {
        var ids = CollectRowIds(keys);
        var path = TablePath(schemaPath, tableName);
        var opener = Opener();
        var schema = ReturningSchema(opener, path);
        var rows = new List<RecordBatch>();
        if (ids.Count > 0)
        {
            long? pinned = TryTxn(AmbientTransaction.Current)?.Peek(path)?.PinnedVersion;
            foreach (var batch in DeltaReader.ReadRowsByRowIds(opener, path, ids, default, atVersion: pinned,
                                                               nativeRead: _nativeRead))
            {
                rows.Add(batch);
            }
            EnsureEveryRowRead(rows, ids.Distinct().Count(), schema, "DELETE");
            ExecuteDelete(schemaPath, tableName, RowIdStream(ids));
        }
        return Returned(schema, rows);
    }

    public IArrowArrayStream ExecuteUpdateReturning(IReadOnlyList<string> schemaPath, string tableName,
                                                    int setColumnCount, IArrowArrayStream data)
    {
        var path = TablePath(schemaPath, tableName);
        var schema = ReturningSchema(Opener(), path);
        var rows = new List<RecordBatch>();
        ExecuteUpdateCore(schemaPath, tableName, setColumnCount, data, rows);
        return Returned(schema, rows);
    }

    public IArrowArrayStream InsertReturning(IReadOnlyList<string> schemaPath, string tableName,
                                             IArrowArrayStream rows)
    {
        var path = TablePath(schemaPath, tableName);
        var schema = ReturningSchema(Opener(), path);
        if (schema.FieldsList.Any(f => f.Metadata?.Keys.Any(k => k.StartsWith("delta.identity.",
                StringComparison.Ordinal)) == true))
        {
            rows.Dispose();
            throw Unsupported("INSERT ... RETURNING into a table with an IDENTITY column (the values are assigned "
                              + "at commit)");
        }
        var input = new List<RecordBatch>();
        Schema inputSchema;
        using (rows)
        {
            inputSchema = rows.Schema;
            RecordBatch? b;
            while ((b = rows.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
            {
                input.Add(b);
            }
        }
        // the table's columns in table order: a column the INSERT names comes from the input (converted to the
        // table's type where DuckDB's differs), any other is NULL — DuckDB has already applied the defaults it knows
        var returned = input.Select(b => InTableOrder(b, schema)).ToList();
        BulkInsert(schemaPath, tableName, new ListArrowStream(inputSchema, input), createTable: false,
                   replace: false, checkConstraints: true, txnId: AmbientTransaction.Current,
                   partitionColumns: null, sortColumns: null, schemaMode: null, partitionOverwrite: false,
                   optionsJson: null);
        return Returned(schema, returned);
    }

    /// <summary>The post-images of an UPDATE's rows, read back at the scanned version (see ExecuteUpdateCore).</summary>
    private void ReturningPostImages(nint opener, string path, UpdateInput updates, IReadOnlyList<Field> fields,
                                     int[] setSlotByColumn, Schema userSchema, List<RecordBatch> returning)
    {
        long? pinned = TryTxn(AmbientTransaction.Current)?.Peek(path)?.PinnedVersion;
        var ridsPerBatch = new List<long[]>();
        foreach (var batch in DeltaReader.ReadRowsByRowIds(opener, path, updates.RowByRid.Keys, default,
                                                           atVersion: pinned, rowIdsOut: ridsPerBatch,
                                                           nativeRead: _nativeRead))
        {
            var rids = ridsPerBatch[ridsPerBatch.Count - 1];
            ridsPerBatch.Clear();
            if (batch.ColumnCount != fields.Count)
            {
                throw Unsupported("UPDATE ... RETURNING after an ALTER TABLE in the same transaction");
            }
            returning.Add(MergeSetValues(batch, rids, fields, setSlotByColumn, updates, userSchema));
        }
        EnsureEveryRowRead(returning, updates.Count, userSchema, "UPDATE");
    }

    /// <summary>
    /// A row the read-back cannot find — one written earlier in the same transaction, which lives in a file the
    /// scanned version does not hold — would be missing from RETURNING; refused before anything is written.
    /// </summary>
    private void EnsureEveryRowRead(List<RecordBatch> rows, int expected, Schema schema, string op)
    {
        long read = rows.Sum(b => (long)b.Length);
        if (read != expected || rows.Any(b => b.ColumnCount != schema.FieldsList.Count))
        {
            foreach (var b in rows)
            {
                b.Dispose();
            }
            rows.Clear();
            throw Unsupported($"{op} ... RETURNING of rows written earlier in the same transaction");
        }
    }

    private Schema ReturningSchema(nint opener, string path)
        => TryTxn(AmbientTransaction.Current)?.Get(path)?.PendingArrowSchema
           ?? DeltaReader.GetSchema(opener, path, ReadBound(path));

    private static IArrowArrayStream Returned(Schema schema, List<RecordBatch> rows)
        => new ListArrowStream(schema, rows);

    private static IArrowArrayStream RowIdStream(List<long> ids)
    {
        var b = new Int64Array.Builder();
        foreach (var id in ids)
        {
            b.Append(id);
        }
        var schema = new Schema(new[] { new Field(RowIdColumn, Int64Type.Default, nullable: false) }, null);
        return new ListArrowStream(schema, new List<RecordBatch> { new(schema, new IArrowArray[] { b.Build() }, ids.Count) });
    }

    /// <summary>An input batch rearranged into the table's columns and types.</summary>
    private static RecordBatch InTableOrder(RecordBatch input, Schema table)
    {
        var columns = new IArrowArray[table.FieldsList.Count];
        for (int c = 0; c < columns.Length; c++)
        {
            var field = table.FieldsList[c];
            int k = -1;
            for (int i = 0; i < input.Schema.FieldsList.Count && k < 0; i++)
            {
                if (string.Equals(input.Schema.FieldsList[i].Name, field.Name, StringComparison.OrdinalIgnoreCase))
                {
                    k = i;
                }
            }
            if (k < 0)
            {
                columns[c] = BuildArray(field.DataType, Enumerable.Repeat<object?>(null, input.Length).ToList());
                continue;
            }
            var col = input.Column(k);
            if (field.DataType is VariantType)
            {
                var copy = EngineeredWood.Arrow.ArrowCompute.Take(col, Enumerable.Range(0, input.Length).ToArray());
                columns[c] = copy as VariantArray ?? new VariantArray(copy);
                continue;
            }
            // ⚠ A COPY (Take allocates new buffers): the input batches go to BulkInsert, whose writer disposes them
            columns[c] = ArrowTypeCompare.SameType(col.Data.DataType, field.DataType, namesMatter: false)
                ? EngineeredWood.Arrow.ArrowCompute.Take(col, Enumerable.Range(0, input.Length).ToArray())
                : BuildArray(field.DataType, Enumerable.Range(0, input.Length)
                    .Select(i => ArrowValueReader.ReadScalarDeep(col, i)).ToList());
        }
        return new RecordBatch(table, columns, input.Length);
    }

    /// <summary>A stream over batches held in memory; disposes the ones it never handed out.</summary>
    private sealed class ListArrowStream : IArrowArrayStream
    {
        private readonly List<RecordBatch> _batches;
        private int _next;

        public ListArrowStream(Schema schema, List<RecordBatch> batches)
        {
            Schema = schema;
            _batches = batches;
        }

        public Schema Schema { get; }

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
            => new(_next < _batches.Count ? _batches[_next++] : null);

        public void Dispose()
        {
            for (; _next < _batches.Count; _next++)
            {
                _batches[_next].Dispose();
            }
        }
    }
}
