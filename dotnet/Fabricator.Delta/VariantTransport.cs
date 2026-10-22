// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Gives engineered-wood a batch whose VARIANT columns are typed as Apache.Arrow's
/// <see cref="VariantType"/>, which is the only thing EW's schema converter recognises
/// (<c>SchemaConverter</c>: <c>VariantType =&gt; PrimitiveType { TypeName = "variant" }</c>).
///
/// <para><b>⚠⚠ THIS USED TO BE A 379-LINE BYTE TRANSPORT AND IS NOT ONE ANY MORE (DuckDB 2.0).</b> The
/// <c>ew.variant_transport</c> leaf-binary form — one self-delimiting <c>metadata ++ value</c> blob per row
/// — existed for exactly one reason: a NESTED extension type crashed DuckDB's
/// <c>ArrowAppender::FinalizeChild</c>, so the canonical <c>struct&lt;metadata, value&gt;</c> could not
/// cross the C data interface. DuckDB 2.0 registers <c>arrow.parquet.variant</c> itself, with that very
/// storage, and both directions now cross canonically. The blobs, the shredding round trip and the
/// metadata-length parsing are gone, and so is the READ direction — nothing has to be flattened.</para>
///
/// <para><b>What is left, and why it is NOT the old workaround.</b> Apache.Arrow's C# importer does not
/// materialise an extension TYPE from the <c>ARROW:extension:name</c> field metadata: a VARIANT arriving
/// over the C data interface is a <see cref="StructArray"/> whose FIELD merely carries the name. EW needs
/// the type. ⚠ MEASURED what happens without this: a <c>CREATE TABLE … (v VARIANT)</c> committed a Delta
/// <c>struct&lt;metadata:binary,value:binary&gt;</c> and declared no <c>variantType</c> feature — and a
/// <c>metaData</c> commit is NOT revisable, so the wrong type would be recorded FOREVER.</para>
///
/// <para>TOP-LEVEL columns only, matching EW's own <c>VariantColumnCoercion</c> and the parquet reader
/// (neither wraps a variant nested inside list/map, and DuckDB's parquet writer rejects a non-root
/// VARIANT anyway). <see cref="VariantMarker.ToCanonicalSchema"/> does recurse, deliberately: a nested
/// variant must still be DESCRIBED correctly even where no batch carries one.</para>
/// </summary>
internal static class VariantTransport
{
    /// <summary>
    /// WRITE direction (DuckDB → EW): every VARIANT column is retyped to <see cref="VariantType"/> and its
    /// array wrapped as a <see cref="VariantArray"/>. Idempotent, and reference-identical when the batch
    /// carries no variant — so this costs nothing on the common path.
    /// <para>⚠ Both halves are needed and a mismatch is a CRASH rather than a wrong type: EW's parquet
    /// writer unwraps an extension ARRAY only when the FIELD is an <c>ExtensionType</c>
    /// (<c>NestedLevelWriter.DecomposeRecursive</c>), so a <see cref="VariantArray"/> sitting under a
    /// struct field reaches <c>DecomposeStruct</c> and dies on its <c>(StructArray)array</c> — measured on
    /// the codec rewrite path (<c>UPDATE</c> on a <c>PROVIDER 'engineeredwooddelta'</c> catalog).</para>
    /// </summary>
    internal static RecordBatch ToCanonical(RecordBatch batch)
    {
        List<Field>? fields = null;
        List<IArrowArray>? arrays = null;

        for (int c = 0; c < batch.ColumnCount; c++)
        {
            var f = batch.Schema.FieldsList[c];
            var column = batch.Column(c);
            var canonical = VariantMarker.ToCanonicalField(f);
            bool isVariant = canonical.DataType is VariantType;
            bool unchanged = ReferenceEquals(canonical, f) && (!isVariant || column is VariantArray);

            if (unchanged)
            {
                fields?.Add(f);
                arrays?.Add(column);
                continue;
            }

            if (fields is null)
            {
                fields = new List<Field>(batch.ColumnCount);
                arrays = new List<IArrowArray>(batch.ColumnCount);
                for (int j = 0; j < c; j++)
                {
                    fields.Add(batch.Schema.FieldsList[j]);
                    arrays.Add(batch.Column(j));
                }
            }

            fields.Add(canonical);
            arrays!.Add(isVariant ? column as VariantArray ?? new VariantArray(column) : column);
        }

        return fields is null
            ? batch
            : new RecordBatch(new Schema(fields, batch.Schema.Metadata), arrays!, batch.Length);
    }

    /// <summary>
    /// The DuckDB-facing guarantee: every VARIANT column is reduced to the canonical TWO-child storage
    /// (<c>metadata</c>, <c>value</c>).
    /// <para>⚠⚠ A SHREDDED variant CANNOT cross into DuckDB — MEASURED as
    /// <c>INTERNAL Error: Attempted to access index 2 within vector of size 2</c> on a native OPTIMIZE,
    /// which is the SAME upstream assertion the old leaf-blob transport was invented to dodge: DuckDB's
    /// VARIANT has exactly two children, so a third (<c>typed_value</c>) walks off the end. EW's own reader
    /// already reassembles on the way out (<c>VariantNestedWrapper.Wrap</c>), so this is needed only where
    /// we hand EW-side batches BACK to DuckDB's COPY.</para>
    /// </summary>
    internal static RecordBatch ToDuckDb(RecordBatch batch)
    {
        List<IArrowArray>? arrays = null;
        for (int c = 0; c < batch.ColumnCount; c++)
        {
            var column = batch.Column(c);
            var reduced = column is VariantArray va && va.Storage is StructArray st && st.Data.Children.Length > 2
                ? EngineeredWood.Parquet.VariantShredding.Reassemble(va)
                : column;
            if (!ReferenceEquals(reduced, column) && arrays is null)
            {
                arrays = new List<IArrowArray>(batch.ColumnCount);
                for (int j = 0; j < c; j++)
                {
                    arrays.Add(batch.Column(j));
                }
            }
            arrays?.Add(reduced);
        }
        return arrays is null ? batch : new RecordBatch(batch.Schema, arrays, batch.Length);
    }

    /// <summary>Batch-list form of <see cref="ToCanonical(RecordBatch)"/>; returns the same instance when
    /// nothing needed converting.</summary>
    internal static IReadOnlyList<RecordBatch> ToCanonical(IReadOnlyList<RecordBatch> batches)
    {
        List<RecordBatch>? converted = null;
        for (int i = 0; i < batches.Count; i++)
        {
            var b = ToCanonical(batches[i]);
            if (!ReferenceEquals(b, batches[i]) && converted is null)
            {
                converted = new List<RecordBatch>(batches.Count);
                for (int j = 0; j < i; j++)
                {
                    converted.Add(batches[j]);
                }
            }
            converted?.Add(b);
        }
        return converted ?? batches;
    }
}
