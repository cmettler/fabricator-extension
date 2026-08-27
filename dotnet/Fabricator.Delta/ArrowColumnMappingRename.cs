// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using DeltaSchema = EngineeredWood.DeltaLake.Schema;

namespace Fabricator.Bridge;

/// <summary>
/// Recursive Arrow rename between a column-mapping Delta table's LOGICAL and PHYSICAL column names — the
/// C#-side analog of duckdb-delta's <c>MultiFileColumnMapper</c> (per the 2026-07-03 decision the pure-C#
/// readers/writers are the native path, not the C++ MFR). Per the Delta protocol, data files store every level
/// under the PHYSICAL name (<c>col-&lt;guid&gt;</c>, from each field's <c>delta.columnMapping.physicalName</c>
/// metadata) in BOTH name and id mode, while the table schema speaks logical names — so:
/// <list type="bullet">
/// <item><b>ToPhysical</b> renames a logical-named write stream so DuckDB's COPY emits the physical layout
/// (top level AND nested struct fields — a flat SELECT alias can't rename struct children).</item>
/// <item><b>ToLogical</b> renames a physical-named read stream (read_parquet / engineered-wood batches) back to
/// the logical schema. Matching tolerates EITHER name at every level (a top-level column already aliased to its
/// logical name in SQL, or a legacy logical-named file, passes through unchanged).</item>
/// </list>
/// Arrays are rebuilt by re-wrapping <see cref="ArrayData"/> with the renamed types — buffers are shared, no
/// data is copied. Structs recurse to any depth; lists recurse into a struct element; maps recurse into a
/// struct value. (List/map INNER elements have structural parquet names — only struct fields carry a
/// physicalName to map.)
/// </summary>
internal static class ArrowColumnMappingRename
{
    /// <summary>True when the schema has any nested (struct-carrying) mapped field — the cheap gate for the
    /// batch transform (top-level-only tables are fully handled by SQL aliasing / flat rename maps).</summary>
    public static bool HasNestedFields(DeltaSchema.StructType schema)
    {
        foreach (var f in schema.Fields)
        {
            if (ContainsStruct(f.Type))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsStruct(DeltaSchema.DeltaDataType type) => type switch
    {
        DeltaSchema.StructType => true,
        DeltaSchema.ArrayType at => ContainsStruct(at.ElementType),
        DeltaSchema.MapType mt => ContainsStruct(mt.KeyType) || ContainsStruct(mt.ValueType),
        _ => false,
    };

    public static Schema RenameSchema(Schema schema, DeltaSchema.StructType deltaSchema, bool toPhysical)
    {
        var fields = new List<Field>(schema.FieldsList.Count);
        bool changed = false;
        foreach (var f in schema.FieldsList)
        {
            var renamed = RenameField(f, FindField(deltaSchema, f.Name), toPhysical);
            changed |= !ReferenceEquals(renamed, f);
            fields.Add(renamed);
        }
        return changed ? new Schema(fields, schema.Metadata) : schema;
    }

    public static RecordBatch RenameBatch(RecordBatch batch, DeltaSchema.StructType deltaSchema, bool toPhysical)
    {
        var fields = new List<Field>(batch.Schema.FieldsList.Count);
        var arrays = new List<IArrowArray>(batch.ColumnCount);
        bool changed = false;
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            var f = batch.Schema.FieldsList[i];
            var renamed = RenameField(f, FindField(deltaSchema, f.Name), toPhysical);
            if (ReferenceEquals(renamed, f))
            {
                fields.Add(f);
                arrays.Add(batch.Column(i));
            }
            else
            {
                changed = true;
                fields.Add(renamed);
                arrays.Add(Rebuild(batch.Column(i).Data, renamed.DataType));
            }
        }
        if (!changed)
        {
            return batch;
        }
        return new RecordBatch(new Schema(fields, batch.Schema.Metadata), arrays, batch.Length);
    }

    /// <summary>Wraps <paramref name="source"/> so its schema + every batch are renamed. Disposes the source.</summary>
    public static IArrowArrayStream Wrap(IArrowArrayStream source, DeltaSchema.StructType deltaSchema, bool toPhysical)
        => new RenamingStream(source, deltaSchema, toPhysical);

    // ---- matching ----

    // Finds the Delta field an Arrow name refers to: the logical name OR the physicalName (tolerant matching —
    // the source may be a logical-named write stream, an already-aliased top level, a physical-named spec file,
    // or a legacy logical-named file).
    private static DeltaSchema.StructField? FindField(DeltaSchema.StructType schema, string arrowName)
    {
        foreach (var f in schema.Fields)
        {
            if (string.Equals(f.Name, arrowName, System.StringComparison.Ordinal))
            {
                return f;
            }
            if (f.Metadata is { } md
                && md.TryGetValue(DeltaSchema.ColumnMapping.PhysicalNameKey, out var phys)
                && string.Equals(phys, arrowName, System.StringComparison.Ordinal))
            {
                return f;
            }
        }
        return null;
    }

    private static string TargetName(DeltaSchema.StructField field, bool toPhysical)
    {
        if (toPhysical
            && field.Metadata is { } md
            && md.TryGetValue(DeltaSchema.ColumnMapping.PhysicalNameKey, out var phys)
            && !string.IsNullOrEmpty(phys))
        {
            return phys;
        }
        return field.Name; // toLogical target, or an unmapped field
    }

    // ---- field/type rename (returns the SAME instance when nothing changes, as the no-op signal) ----

    private static Field RenameField(Field arrow, DeltaSchema.StructField? delta, bool toPhysical)
    {
        if (delta is null)
        {
            return arrow; // not a table column (e.g. the transient _metadata.row_id) — pass through
        }
        string name = TargetName(delta, toPhysical);
        var type = RenameType(arrow.DataType, delta.Type, toPhysical);
        if (string.Equals(name, arrow.Name, System.StringComparison.Ordinal) && ReferenceEquals(type, arrow.DataType))
        {
            return arrow;
        }
        return new Field(name, type, arrow.IsNullable, arrow.Metadata);
    }

    private static Apache.Arrow.Types.IArrowType RenameType(
        Apache.Arrow.Types.IArrowType arrow, DeltaSchema.DeltaDataType delta, bool toPhysical)
    {
        switch (arrow)
        {
            case Apache.Arrow.Types.StructType st when delta is DeltaSchema.StructType ds:
            {
                var children = new List<Field>(st.Fields.Count);
                bool changed = false;
                foreach (var child in st.Fields)
                {
                    var renamed = RenameField(child, FindField(ds, child.Name), toPhysical);
                    changed |= !ReferenceEquals(renamed, child);
                    children.Add(renamed);
                }
                return changed ? new Apache.Arrow.Types.StructType(children) : arrow;
            }
            case Apache.Arrow.Types.ListType lt when delta is DeltaSchema.ArrayType da:
            {
                var elemType = RenameType(lt.ValueField.DataType, da.ElementType, toPhysical);
                return ReferenceEquals(elemType, lt.ValueField.DataType)
                    ? arrow
                    : new Apache.Arrow.Types.ListType(
                        new Field(lt.ValueField.Name, elemType, lt.ValueField.IsNullable, lt.ValueField.Metadata));
            }
            case Apache.Arrow.Types.LargeListType llt when delta is DeltaSchema.ArrayType da:
            {
                var elemType = RenameType(llt.ValueField.DataType, da.ElementType, toPhysical);
                return ReferenceEquals(elemType, llt.ValueField.DataType)
                    ? arrow
                    : new Apache.Arrow.Types.LargeListType(
                        new Field(llt.ValueField.Name, elemType, llt.ValueField.IsNullable, llt.ValueField.Metadata));
            }
            case Apache.Arrow.Types.MapType mt when delta is DeltaSchema.MapType dm:
            {
                var keyType = RenameType(mt.KeyField.DataType, dm.KeyType, toPhysical);
                var valType = RenameType(mt.ValueField.DataType, dm.ValueType, toPhysical);
                if (ReferenceEquals(keyType, mt.KeyField.DataType) && ReferenceEquals(valType, mt.ValueField.DataType))
                {
                    return arrow;
                }
                return new Apache.Arrow.Types.MapType(
                    new Field(mt.KeyField.Name, keyType, mt.KeyField.IsNullable, mt.KeyField.Metadata),
                    new Field(mt.ValueField.Name, valType, mt.ValueField.IsNullable, mt.ValueField.Metadata),
                    mt.KeySorted);
            }
            default:
                return arrow; // primitive (or a shape the Delta type doesn't mirror) — unchanged
        }
    }

    // Re-wraps ArrayData with the renamed type, recursing into children. Buffers are shared (no copy);
    // only the type tree (which carries the field names) is rebuilt.
    private static IArrowArray Rebuild(ArrayData data, Apache.Arrow.Types.IArrowType newType)
    {
        var newData = RebuildData(data, newType);
        return Apache.Arrow.ArrowArrayFactory.BuildArray(newData);
    }

    private static ArrayData RebuildData(ArrayData data, Apache.Arrow.Types.IArrowType newType)
    {
        if (ReferenceEquals(data.DataType, newType))
        {
            return data;
        }
        ArrayData[]? children = data.Children;
        if (children is { Length: > 0 })
        {
            var newChildren = new ArrayData[children.Length];
            for (int i = 0; i < children.Length; i++)
            {
                newChildren[i] = RebuildData(children[i], ChildType(newType, children[i].DataType, i));
            }
            children = newChildren;
        }
        return new ArrayData(newType, data.Length, data.NullCount, data.Offset, data.Buffers, children,
                             data.Dictionary);
    }

    // The renamed type of child i of a container type (falls back to the child's own type when the container
    // shape is unexpected — a defensive no-op, never a crash).
    private static Apache.Arrow.Types.IArrowType ChildType(
        Apache.Arrow.Types.IArrowType container, Apache.Arrow.Types.IArrowType fallback, int index)
        => container switch
        {
            Apache.Arrow.Types.StructType st when index < st.Fields.Count => st.Fields[index].DataType,
            Apache.Arrow.Types.ListType lt when index == 0 => lt.ValueField.DataType,
            Apache.Arrow.Types.LargeListType llt when index == 0 => llt.ValueField.DataType,
            // Arrow MapType's single child is the entries struct<key, value>.
            Apache.Arrow.Types.MapType mt when index == 0 =>
                new Apache.Arrow.Types.StructType(new[] { mt.KeyField, mt.ValueField }),
            _ => fallback,
        };

    private sealed class RenamingStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _source;
        private readonly DeltaSchema.StructType _deltaSchema;
        private readonly bool _toPhysical;
        private Schema? _schema;

        public RenamingStream(IArrowArrayStream source, DeltaSchema.StructType deltaSchema, bool toPhysical)
        {
            _source = source;
            _deltaSchema = deltaSchema;
            _toPhysical = toPhysical;
        }

        public Schema Schema => _schema ??= RenameSchema(_source.Schema, _deltaSchema, _toPhysical);

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var batch = await _source.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            return batch is null ? null : RenameBatch(batch, _deltaSchema, _toPhysical);
        }

        public void Dispose() => _source.Dispose();
    }
}
