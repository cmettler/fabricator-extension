// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using DeltaSchema = EngineeredWood.DeltaLake.Schema;

namespace Fabricator.Bridge;

/// <summary>
/// Write-side NOT NULL enforcement for the Delta provider. A Delta writer MUST enforce the table's
/// declared nullability (readers trust it — Spark reads a non-nullable column without null checks), so an
/// append that would introduce a NULL into a non-nullable field has to fail BEFORE anything is committed.
/// Covers nested constraints too: struct fields (a null child under a VALID parent row), list elements
/// (<c>containsNull=false</c>) and map values (<c>valueContainsNull=false</c>) — external (Spark-created)
/// tables declare these even though DuckDB DDL cannot.
///
/// <para>Validation is driven by the table's authoritative Delta schema and is only active when the schema
/// actually carries a constraint (<see cref="HasConstraints"/>); the streaming path wraps the input stream
/// (per-batch, lazy — a caller that falls back without consuming the stream is unaffected), the collect
/// path validates the materialized batches. Error message matches DuckDB's constraint wording.</para>
/// </summary>
internal static class DeltaNullability
{
    /// <summary>True when any field (at any nesting level) declares a NOT NULL-style constraint.</summary>
    internal static bool HasConstraints(DeltaSchema.StructType schema)
    {
        foreach (var f in schema.Fields)
        {
            if (!f.Nullable || TypeHasConstraints(f.Type))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TypeHasConstraints(DeltaSchema.DeltaDataType type) => type switch
    {
        DeltaSchema.StructType st => HasConstraints(st),
        DeltaSchema.ArrayType at => !at.ContainsNull || TypeHasConstraints(at.ElementType),
        DeltaSchema.MapType mt => !mt.ValueContainsNull || TypeHasConstraints(mt.ValueType),
        _ => false,
    };

    /// <summary>Wraps <paramref name="data"/> with per-batch validation when the schema carries constraints
    /// (lazy: nothing is pulled until the consumer reads).</summary>
    internal static IArrowArrayStream Wrap(IArrowArrayStream data, DeltaSchema.StructType schema, string table)
        => HasConstraints(schema) ? new ValidatingStream(data, schema, table) : data;

    internal static void ValidateBatches(
        IReadOnlyList<RecordBatch> batches, DeltaSchema.StructType schema, string table)
    {
        if (!HasConstraints(schema))
        {
            return;
        }
        foreach (var b in batches)
        {
            ValidateBatch(b, schema, table);
        }
    }

    internal static void ValidateBatch(RecordBatch batch, DeltaSchema.StructType schema, string table)
    {
        foreach (var field in schema.Fields)
        {
            var column = FindColumn(batch, field.Name);
            if (column is null)
            {
                // A partial-column INSERT omits the column entirely = an implicit NULL for every row.
                if (!field.Nullable && batch.Length > 0)
                {
                    throw Violation(table, field.Name);
                }
                continue;
            }
            ValidateColumn(column, field.Type, field.Nullable, field.Name, table, live: null);
        }
    }

    /// <summary>Validates one UPDATE SET value (scalar or the ReadScalarDeep struct dictionary) against the
    /// target field's declared nullability.</summary>
    internal static void ValidateSetValue(object? value, Field field, string table)
        => ValidateSetValue(value, field.DataType, field.IsNullable, field.Name, table);

    private static void ValidateSetValue(
        object? value, Apache.Arrow.Types.IArrowType type, bool nullable, string path, string table)
    {
        if (value is null)
        {
            if (!nullable)
            {
                throw Violation(table, path);
            }
            return;
        }
        if (type is Apache.Arrow.Types.StructType st && value is IReadOnlyDictionary<string, object?> dict)
        {
            foreach (var child in st.Fields)
            {
                object? childValue = null;
                foreach (var kv in dict)
                {
                    if (string.Equals(kv.Key, child.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        childValue = kv.Value;
                        break;
                    }
                }
                ValidateSetValue(childValue, child.DataType, child.IsNullable, path + "." + child.Name, table);
            }
        }
    }

    // ---- per-array validation ------------------------------------------------------------------------

    // `live[i]` = logical row i of `arr` belongs to a live (non-null-ancestor) row; null = all rows live.
    // A null under a NULL parent is unconstrained (Arrow leaves child slots under null parents unspecified).
    private static void ValidateColumn(
        IArrowArray arr, DeltaSchema.DeltaDataType type, bool nullable, string path, string table, bool[]? live)
    {
        if (!nullable)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if ((live is null || live[i]) && arr.IsNull(i))
                {
                    throw Violation(table, path);
                }
            }
        }

        switch (type)
        {
            case DeltaSchema.StructType st when arr is StructArray sa:
            {
                int off = sa.Data.Offset;
                var arrowStruct = (Apache.Arrow.Types.StructType)sa.Data.DataType;
                foreach (var field in st.Fields)
                {
                    if (!field.Nullable || TypeHasConstraints(field.Type))
                    {
                        int childIndex = FindStructChild(arrowStruct, field.Name);
                        if (childIndex < 0)
                        {
                            continue; // absent child: nothing was written for it (reader backfills NULL)
                        }
                        var child = sa.Fields[childIndex];
                        // Struct children are NOT sliced with the parent — logical parent row i lives at
                        // child index off + i (the TakeRows convention).
                        var childLive = new bool[child.Length];
                        for (int i = 0; i < sa.Length; i++)
                        {
                            if (off + i < childLive.Length)
                            {
                                childLive[off + i] = (live is null || live[i]) && !sa.IsNull(i);
                            }
                        }
                        ValidateColumn(child, field.Type, field.Nullable, path + "." + field.Name, table, childLive);
                    }
                }
                break;
            }
            case DeltaSchema.ArrayType at when arr is ListArray la && la is not MapArray:
            {
                if (!at.ContainsNull || TypeHasConstraints(at.ElementType))
                {
                    var values = la.Values;
                    var elemLive = new bool[values.Length];
                    for (int i = 0; i < la.Length; i++)
                    {
                        if ((live is null || live[i]) && !la.IsNull(i))
                        {
                            int start = la.ValueOffsets[i];
                            int end = la.ValueOffsets[i + 1];
                            for (int k = start; k < end && k < elemLive.Length; k++)
                            {
                                elemLive[k] = true;
                            }
                        }
                    }
                    ValidateColumn(values, at.ElementType, at.ContainsNull, path + ".element", table, elemLive);
                }
                break;
            }
            case DeltaSchema.MapType mt when arr is MapArray ma:
            {
                if (!mt.ValueContainsNull || TypeHasConstraints(mt.ValueType))
                {
                    var entries = (StructArray)ma.Values; // (key, value) entry struct; entries are never null
                    var entryLive = new bool[entries.Length];
                    for (int i = 0; i < ma.Length; i++)
                    {
                        if ((live is null || live[i]) && !ma.IsNull(i))
                        {
                            int start = ma.ValueOffsets[i];
                            int end = ma.ValueOffsets[i + 1];
                            for (int k = start; k < end && k < entryLive.Length; k++)
                            {
                                entryLive[k] = true;
                            }
                        }
                    }
                    int entryOff = entries.Data.Offset;
                    var value = entries.Fields[1];
                    var valueLive = new bool[value.Length];
                    for (int k = 0; k < entries.Length; k++)
                    {
                        if (entryOff + k < valueLive.Length)
                        {
                            valueLive[entryOff + k] = entryLive[k];
                        }
                    }
                    ValidateColumn(value, mt.ValueType, mt.ValueContainsNull, path + ".value", table, valueLive);
                }
                break;
            }
        }
    }

    private static IArrowArray? FindColumn(RecordBatch batch, string name)
    {
        for (int i = 0; i < batch.Schema.FieldsList.Count; i++)
        {
            if (string.Equals(batch.Schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return batch.Column(i);
            }
        }
        return null;
    }

    private static int FindStructChild(Apache.Arrow.Types.StructType arrowStruct, string name)
    {
        for (int i = 0; i < arrowStruct.Fields.Count; i++)
        {
            if (string.Equals(arrowStruct.Fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static InvalidOperationException Violation(string table, string path)
        => new($"NOT NULL constraint failed: {table}.{path}");

    // Per-batch validating pass-through: lazy (validation happens as the consumer pulls), so a caller that
    // decides to fall back WITHOUT consuming the stream is unaffected.
    private sealed class ValidatingStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private readonly DeltaSchema.StructType _schema;
        private readonly string _table;

        internal ValidatingStream(IArrowArrayStream inner, DeltaSchema.StructType schema, string table)
        {
            _inner = inner;
            _schema = schema;
            _table = table;
        }

        public Schema Schema => _inner.Schema;

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch is not null)
            {
                ValidateBatch(batch, _schema, _table);
            }
            return batch;
        }

        public void Dispose() => _inner.Dispose();
    }
}
