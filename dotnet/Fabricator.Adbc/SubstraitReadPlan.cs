// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Adbc;

/// <summary>
/// Builds the Substrait plan for a table scan: one <c>ReadRel</c> naming the table, its full schema as the base
/// schema, the projected columns as a projection mask and the host's filters as its <c>best_effort_filter</c>. A
/// driver translates it to its own dialect, so a scan needs no SQL of ours.
/// </summary>
internal static class SubstraitReadPlan
{
    /// <param name="filter">A condition over the BASE schema's columns (field i = column i), or null. It goes as the
    /// best-effort filter: the host re-applies every filter, so the driver may return more rows than it selects.</param>
    /// <param name="functions">The functions <paramref name="filter"/> calls.</param>
    public static byte[] Build(IReadOnlyList<string> tablePath, Schema schema, IReadOnlyList<int> projection,
                               ProtoWriter? filter = null, SubstraitFunctions? functions = null)
    {
        var namedStruct = new ProtoWriter();
        foreach (var field in schema.FieldsList)
            namedStruct.String(1, field.Name);
        var types = new ProtoWriter();
        foreach (var field in schema.FieldsList)
            types.Message(1, Type(field.DataType, field.IsNullable));
        types.Varint(3, Required);
        namedStruct.Message(2, types);

        var items = new ProtoWriter();
        foreach (int index in projection)
            items.Message(1, new ProtoWriter().Varint(1, (ulong)index));
        var mask = new ProtoWriter().Message(1, items).Varint(2, 1);

        var namedTable = new ProtoWriter();
        foreach (string name in tablePath)
            namedTable.String(1, name);

        var read = new ProtoWriter()
            .Message(1, new ProtoWriter().Message(1, new ProtoWriter())) // common: direct
            .Message(2, namedStruct)
            .Message(4, mask)
            .Message(7, namedTable);
        if (filter is not null)
            read.Message(11, filter); // best_effort_filter

        var root = new ProtoWriter().Message(1, new ProtoWriter().Message(1, read));
        foreach (int index in projection)
            root.String(2, schema.FieldsList[index].Name);

        var plan = new ProtoWriter();
        if (filter is not null)
            functions?.WriteTo(plan);
        return plan
            .Message(3, new ProtoWriter().Message(2, root))  // relations: PlanRel.root
            .Message(6, new ProtoWriter().Varint(2, 75))     // version 0.75
            .ToArray();
    }

    private const ulong Nullable = 1;
    private const ulong Required = 2;

    private static ProtoWriter Type(IArrowType type, bool nullable)
    {
        ulong n = nullable ? Nullable : Required;
        ProtoWriter Simple(int field) => new ProtoWriter().Message(field, new ProtoWriter().Varint(2, n));
        return type switch
        {
            BooleanType => Simple(1),
            Int8Type => Simple(2),
            Int16Type => Simple(3),
            Int32Type => Simple(5),
            Int64Type => Simple(7),
            // Substrait has no unsigned integers: declare the next wider signed type, which holds every value.
            UInt8Type => Simple(3),
            UInt16Type => Simple(5),
            UInt32Type => Simple(7),
            FloatType => Simple(10),
            DoubleType => Simple(11),
            StringType or LargeStringType or StringViewType => Simple(12),
            BinaryType or LargeBinaryType or BinaryViewType => Simple(13),
            Date32Type or Date64Type => Simple(16),
            Decimal128Type d => Decimal(d.Precision, d.Scale, n),
            Decimal256Type d => Decimal(d.Precision, d.Scale, n),
            Decimal64Type d => Decimal(d.Precision, d.Scale, n),
            Decimal32Type d => Decimal(d.Precision, d.Scale, n),
            FixedSizeBinaryType f => new ProtoWriter().Message(23,
                new ProtoWriter().Varint(1, (ulong)f.ByteWidth).Varint(3, n)),
            TimestampType t => new ProtoWriter().Message(t.Timezone is null ? 33 : 34,
                new ProtoWriter().Varint(1, Precision(t.Unit)).Varint(3, n)),
            Time32Type t => new ProtoWriter().Message(36, new ProtoWriter().Varint(1, Precision(t.Unit)).Varint(3, n)),
            Time64Type t => new ProtoWriter().Message(36, new ProtoWriter().Varint(1, Precision(t.Unit)).Varint(3, n)),
            ExtensionType e => Type(e.StorageType, nullable),
            _ => throw new NotSupportedException($"adbc: a column of type {type.Name} cannot be read through Substrait"),
        };
    }

    private static ProtoWriter Decimal(int precision, int scale, ulong n) =>
        new ProtoWriter().Message(24, new ProtoWriter()
            .Varint(1, (ulong)scale).Varint(2, (ulong)precision).Varint(4, n));

    private static ulong Precision(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 0,
        TimeUnit.Millisecond => 3,
        TimeUnit.Microsecond => 6,
        _ => 9,
    };
}
