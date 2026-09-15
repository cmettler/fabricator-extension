// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// STRUCTURAL comparison of two Arrow types.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ IT EXISTS BECAUSE <c>IArrowType.Equals</c> IS REFERENCE EQUALITY IN Apache.Arrow. Two types that
/// describe the same thing compare UNEQUAL unless they are literally the same object, so every caller that
/// needs real equality has to hand-write it — and this repo had grown two answers to that
/// (<c>SqlServerCdcReader.SameType</c>, private to another assembly, and <c>Host.Verify</c>, which
/// deliberately compares only <c>TypeId</c>). <c>Host.cs</c> already recorded consolidating them here as the
/// right move; this is it, and the third caller is what forced it.
/// </para>
/// <para>
/// <b>⚠⚠ <paramref name="namesMatter"/> IS NOT A CONVENIENCE — THE TWO CALLERS WANT OPPOSITE ANSWERS, AND
/// GETTING IT BACKWARDS IS A WRONG ANSWER IN BOTH DIRECTIONS.</b>
/// </para>
/// <list type="bullet">
/// <item><b>TRUE — "is this the same schema?"</b> CDC drift detection: a renamed nested field IS the drift
/// it is looking for, so ignoring names would wave through the change it exists to catch.</item>
/// <item><b>FALSE — "may I hand this array to a consumer expecting that type?"</b> A nested field's name is
/// a LABEL, not layout: Arrow conventionally names a list's child <c>item</c> while the parquet spec names
/// it <c>element</c>, so two faithful converters describing the SAME `VARCHAR[]` legitimately disagree on
/// it. Comparing names there would reject an array that is byte-for-byte compatible.</item>
/// </list>
/// <para>
/// ⚠ There is no default. A caller must state which question it is asking, because the failure mode of
/// guessing is silent in both directions — a missed schema change, or a needless fallback that looks like
/// an unsupported type.
/// </para>
/// <para>
/// ⚠ THE <c>default:</c> ARM RETURNS TRUE, i.e. a type id with no parameters compares equal on the id
/// alone. That is correct for every parameterless type and is why the parameterised ones are enumerated
/// above it — a new parameterised Arrow type would be waved through until it is added here, which is the
/// one way this can be wrong.
/// </para>
/// </remarks>
public static class ArrowTypeCompare
{
    /// <summary>Structural equality of two Arrow types. See the remarks for <paramref name="namesMatter"/>.</summary>
    /// <param name="namesMatter">
    /// TRUE compares nested field NAMES (schema identity — a rename is a difference); FALSE compares only
    /// layout and semantics (assignment compatibility — a child's label is irrelevant).
    /// </param>
    public static bool SameType(IArrowType a, IArrowType b, bool namesMatter)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a.TypeId != b.TypeId)
        {
            return false;
        }
        switch (a, b)
        {
            case (Decimal128Type x, Decimal128Type y):
                return x.Precision == y.Precision && x.Scale == y.Scale;
            case (Decimal256Type x, Decimal256Type y):
                return x.Precision == y.Precision && x.Scale == y.Scale;
            case (TimestampType x, TimestampType y):
                return x.Unit == y.Unit && string.Equals(x.Timezone, y.Timezone, StringComparison.Ordinal);
            case (Time32Type x, Time32Type y):
                return x.Unit == y.Unit;
            case (Time64Type x, Time64Type y):
                return x.Unit == y.Unit;
            case (FixedSizeBinaryType x, FixedSizeBinaryType y):
                return x.ByteWidth == y.ByteWidth;
            case (NestedType x, NestedType y):
                if (x.Fields.Count != y.Fields.Count)
                {
                    return false;
                }
                for (int i = 0; i < x.Fields.Count; i++)
                {
                    if (namesMatter
                        && !string.Equals(x.Fields[i].Name, y.Fields[i].Name, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    // ⚠ NULLABILITY IS DELIBERATELY NOT COMPARED. A nullable child accepts everything a
                    // non-nullable one holds, and the two converters that meet here disagree about it
                    // routinely (DuckDB marks almost everything nullable). Declared nullability is enforced
                    // where it belongs — DeltaNullability, against the TARGET field — not by refusing an
                    // array whose values already satisfy it.
                    if (!SameType(x.Fields[i].DataType, y.Fields[i].DataType, namesMatter))
                    {
                        return false;
                    }
                }
                return true;
            default:
                return true;
        }
    }
}
