// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

// ⚠⚠ MOVED HERE FROM Fabricator.Bridge (2026-09-11) SO A PLUGIN CAN REACH IT, and it was already a gap
// rather than a new need: StaticInOutFunction lives in THIS assembly and its own documentation tells an
// author to yield `InOutExchange.EmptyBatch` as the per-input-chunk sentinel — while the helper sat in
// Bridge, which a plugin deliberately does not reference. So a plugin deriving from that base could not
// write the one thing the base requires of it.
//
// ⚠ It belongs here by the membership rule the split was built on: it needs NO host state — it is a pure
// function over a Schema — so it is a LIBRARY, not a service. The NAMESPACE is unchanged
// (Fabricator.Bridge, the Abstractions/Common convention), so the fully-qualified name is identical and
// not one call site moved.

/// <summary>Helpers shared by the in-out exchange path.</summary>
public static class InOutExchange
{
    /// <summary>A length-0 <see cref="RecordBatch"/> matching <paramref name="schema"/> — the exchange sentinel
    /// the host reads as NEED_MORE_INPUT.</summary>
    public static RecordBatch EmptyBatch(Schema schema)
    {
        var arrays = new IArrowArray[schema.FieldsList.Count];
        for (int i = 0; i < arrays.Length; i++)
        {
            arrays[i] = BuildEmptyArray(schema.FieldsList[i].DataType);
        }
        return new RecordBatch(schema, arrays, 0);
    }

    private static IArrowArray BuildEmptyArray(IArrowType type) => type.TypeId switch
    {
        ArrowTypeId.Boolean => new BooleanArray.Builder().Build(),
        ArrowTypeId.Int8 => new Int8Array.Builder().Build(),
        ArrowTypeId.Int16 => new Int16Array.Builder().Build(),
        ArrowTypeId.Int32 => new Int32Array.Builder().Build(),
        ArrowTypeId.Int64 => new Int64Array.Builder().Build(),
        ArrowTypeId.UInt8 => new UInt8Array.Builder().Build(),
        ArrowTypeId.UInt16 => new UInt16Array.Builder().Build(),
        ArrowTypeId.UInt32 => new UInt32Array.Builder().Build(),
        ArrowTypeId.UInt64 => new UInt64Array.Builder().Build(),
        ArrowTypeId.Float => new FloatArray.Builder().Build(),
        ArrowTypeId.Double => new DoubleArray.Builder().Build(),
        ArrowTypeId.String => new StringArray.Builder().Build(),
        ArrowTypeId.Binary => new BinaryArray.Builder().Build(),
        ArrowTypeId.Decimal128 => new Decimal128Array.Builder((Decimal128Type)type).Build(),
        ArrowTypeId.Date32 => new Date32Array.Builder().Build(),
        ArrowTypeId.Date64 => new Date64Array.Builder().Build(),
        ArrowTypeId.Timestamp => new TimestampArray.Builder((TimestampType)type).Build(),
        ArrowTypeId.Time32 => new Time32Array.Builder((Time32Type)type).Build(),
        ArrowTypeId.Time64 => new Time64Array.Builder((Time64Type)type).Build(),
        _ => throw new NotSupportedException(
            $"fabricator: in-out exchange sentinel does not support output column type {type.Name}"),
    };
}
