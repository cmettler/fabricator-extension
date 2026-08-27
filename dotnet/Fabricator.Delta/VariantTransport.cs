// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Scalars.Variant;
using Apache.Arrow.Types;
using EngineeredWood.Parquet;

namespace Fabricator.Bridge;

/// <summary>
/// The variant BATCH conversion at our boundary with engineered-wood: canonical
/// <see cref="VariantArray"/> ⇄ the <c>ew.variant_transport</c> leaf-binary form
/// (<see cref="VariantMarker"/> owns the marker and the SCHEMA direction).
///
/// <para><b>Why this lives here and not in engineered-wood.</b> It used to be a 322-line patch on EW that
/// REPLACED <c>VariantColumnCoercion.Coerce</c>, and therefore had to normalise four physical layouts
/// (canonical, shredded, a bare struct-of-binary from an unannotated Spark 4.0.x file, and a seam-delivered
/// blob) keyed off the DELTA schema — because an unannotated variant is indistinguishable from an ordinary
/// struct at the Arrow level. Upstream's `Coerce` already performs exactly that normalisation, to canonical.
/// So we let it run and convert AFTER it: one layout in, one out, and the detection is by Arrow TYPE rather
/// than by consulting the Delta schema. That is the whole reason this is ~1/3 of the size it was.</para>
///
/// <para><b>The C-interface crash is not relevant at this seam.</b> EW hands us <see cref="RecordBatch"/>es
/// as in-process .NET objects; only the export to DuckDB crosses the C data interface, which is where the
/// leaf-blob form is required (a nested extension type crashes DuckDB's
/// <c>ArrowAppender::FinalizeChild</c> — see <c>src/fabricator/fabricator_variant.cpp</c>). So we may hold a
/// canonical <see cref="VariantArray"/> right up to the boundary and flatten it there.</para>
///
/// <para>TOP-LEVEL columns only, matching both `Coerce` and the parquet reader (neither wraps a variant
/// nested inside list/map, and DuckDB's parquet writer rejects a non-root VARIANT anyway). The SCHEMA
/// direction in <see cref="VariantMarker.ToTransportSchema"/> does recurse, which is deliberate: a nested
/// variant must still be DESCRIBED correctly even where no batch carries one.</para>
/// </summary>
internal static class VariantTransport
{
    /// <summary>
    /// READ direction (EW → DuckDB): every canonical <see cref="VariantArray"/> column becomes a BINARY
    /// column of one self-delimiting <c>metadata ++ value</c> blob per row, tagged with the transport
    /// marker. Idempotent — a column that is already the transport form is left alone, so this is safe to
    /// apply on a path where a seam delivered blobs directly.
    /// </summary>
    internal static RecordBatch ToTransport(RecordBatch batch)
    {
        List<Field>? fields = null;
        List<IArrowArray>? arrays = null;

        for (int c = 0; c < batch.ColumnCount; c++)
        {
            var column = batch.Column(c);
            var f = batch.Schema.FieldsList[c];

            // Already the transport form (a seam delivered it, or we ran twice): nothing to do. Checked
            // before the VariantArray test so idempotence does not depend on the field metadata surviving.
            if (column is BinaryArray && VariantMarker.IsVariantArrowField(f))
            {
                continue;
            }
            if (column is not VariantArray variant)
            {
                continue;
            }

            var storage = Canonicalise(variant, f.Name);
            var structType = (StructType)storage.Data.DataType;
            int off = storage.Data.Offset; // struct children do NOT incorporate the parent's offset

            BinaryArray? meta = null, val = null;
            for (int i = 0; i < structType.Fields.Count; i++)
            {
                // Apache.Arrow's factory, QUALIFIED on purpose: EngineeredWood.Parquet.Data declares its own
                // internal ArrowArrayFactory that throws on 'struct'. It is invisible from this assembly, so
                // the unqualified name would bind correctly today — but a future using-directive would
                // silently rebind it and fail only at run time.
                var child = Apache.Arrow.ArrowArrayFactory.BuildArray(storage.Data.Children[i]) as BinaryArray;
                // BY NAME, never by position: writers disagree on child order (engineered-wood emits
                // (metadata, value), Spark emits (value, metadata)), and swapping the halves would corrupt
                // every value while remaining structurally valid.
                if (string.Equals(structType.Fields[i].Name, "metadata", StringComparison.Ordinal))
                {
                    meta = child;
                }
                else if (string.Equals(structType.Fields[i].Name, "value", StringComparison.Ordinal))
                {
                    val = child;
                }
            }
            if (meta is null || val is null)
            {
                throw new InvalidOperationException(
                    $"column '{f.Name}' is a VariantArray but its storage lacks binary metadata/value "
                    + "children; the transport form cannot be built.");
            }

            var blobs = new BinaryArray.Builder();
            for (int r = 0; r < storage.Length; r++)
            {
                if (storage.IsNull(r) || meta.IsNull(off + r) || val.IsNull(off + r))
                {
                    blobs.AppendNull();
                    continue;
                }
                var m = meta.GetBytes(off + r);
                var v = val.GetBytes(off + r);
                var combined = new byte[m.Length + v.Length];
                m.CopyTo(combined);
                v.CopyTo(combined.AsSpan(m.Length));
                blobs.Append(combined.AsSpan());
            }

            EnsureCopies(batch, ref fields, ref arrays);
            fields![c] = TagTransport(f);
            arrays![c] = blobs.Build();
        }

        return Rebuild(batch, fields, arrays);
    }

    /// <summary>
    /// WRITE direction (DuckDB → EW), and the SEAM direction (our native reader → EW): every
    /// transport-marked BINARY column becomes a canonical <see cref="VariantArray"/>, so engineered-wood
    /// sees the layout its own pipeline produces and needs no knowledge of the transport.
    ///
    /// <para>Marker-keyed, so it works on logical- and physical-named batches alike, and is a no-op for a
    /// batch that carries no transport column. This is what lets EW's <c>VariantColumnCoercion.Coerce</c>
    /// run UNPATCHED: it throws on a BinaryArray whose schema says variant, and by converting first we
    /// never hand it one.</para>
    /// </summary>
    internal static RecordBatch ToCanonical(RecordBatch batch)
    {
        List<Field>? fields = null;
        List<IArrowArray>? arrays = null;

        for (int c = 0; c < batch.ColumnCount; c++)
        {
            var f = batch.Schema.FieldsList[c];
            if (!VariantMarker.IsVariantArrowField(f) || batch.Column(c) is not BinaryArray blob)
            {
                continue;
            }

            var variant = BuildVariantColumn(blob);
            EnsureCopies(batch, ref fields, ref arrays);
            // Drop the transport marker: the field is a real variant now, and leaving the marker on would
            // make ToTransport's idempotence check misread a canonical column as already-converted.
            fields![c] = new Field(f.Name, variant.Data.DataType, f.IsNullable, StripMarker(f.Metadata));
            arrays![c] = variant;
        }

        return Rebuild(batch, fields, arrays);
    }

    /// <summary>
    /// <see cref="ToCanonical(RecordBatch)"/> over a batch list, returning the SAME instance when nothing
    /// carried a transport column — so a non-variant write allocates nothing and the identity is preserved
    /// for callers that compare (e.g. a pending buffer's batches).
    /// </summary>
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

    /// <summary>
    /// Reduces a possibly-SHREDDED variant to the canonical <c>(metadata, value)</c> storage struct. The
    /// parquet reader reassembles shredding when it wraps an annotated file, but a variant that arrived
    /// shredded — ours or a foreign writer's — must be merged before its halves can be concatenated.
    /// </summary>
    private static StructArray Canonicalise(VariantArray variant, string columnName)
    {
        if (variant.Storage is not StructArray storage)
        {
            throw new InvalidOperationException(
                $"column '{columnName}': VariantArray storage is "
                + $"{variant.Storage?.GetType().Name ?? "null"}, not a struct.");
        }

        var st = (StructType)storage.Data.DataType;
        if (st.GetFieldIndex("typed_value") < 0 && st.GetFieldIndex("value") >= 0)
        {
            return storage; // already canonical
        }

        if (storage.Data.Offset != 0)
        {
            throw new InvalidOperationException(
                $"column '{columnName}': shredded variant reassembly over an offset struct slice is not "
                + "supported (fresh reader batches are never sliced).");
        }

        var canonical = VariantShredding.Reassemble(variant).Storage as StructArray;
        // Post-condition CHECKED, not assumed: if a typed_value survived, the concat above would read the
        // RAW value child — EMPTY for every shredded row — and silently produce empty variants.
        if (canonical is null
            || ((StructType)canonical.Data.DataType).GetFieldIndex("typed_value") >= 0)
        {
            throw new InvalidOperationException(
                $"column '{columnName}': shredded variant reassembly did not yield a canonical "
                + "metadata/value struct.");
        }
        return canonical;
    }

    /// <summary>
    /// Builds the canonical column from a transport-blob column. Each blob is parsed ONCE and the decoded
    /// values are offered to <see cref="VariantShredding"/>, which owns the layout decision: where a
    /// shredding schema applies the rows are shredded into typed columns plus residuals (the data-skipping
    /// that spec readers exploit); where it declines we build the unshredded array from the ORIGINAL bytes,
    /// so a mixed-shape column costs no re-encode. A SQL NULL row becomes a null STORAGE row, which is
    /// distinct from a variant JSON null riding in the value bytes.
    /// </summary>
    private static VariantArray BuildVariantColumn(BinaryArray blob)
    {
        int n = blob.Length;
        var values = new VariantValue[n];
        var isNull = new bool[n];
        bool anyNull = false;

        for (int r = 0; r < n; r++)
        {
            if (blob.IsNull(r))
            {
                isNull[r] = true;
                anyNull = true;
                values[r] = VariantValue.Null; // placeholder; masked by validity in the shredder
                continue;
            }
            var bytes = blob.GetBytes(r);
            int metaLen = MetadataLength(bytes);
            values[r] = new VariantReader(bytes.Slice(0, metaLen), bytes.Slice(metaLen)).ToVariantValue();
        }

        if (VariantShredding.TryShred(values, anyNull ? isNull : default, out var shredded))
        {
            return shredded;
        }

        var builder = new VariantArray.Builder();
        for (int r = 0; r < n; r++)
        {
            if (isNull[r])
            {
                builder.AppendNull();
                continue;
            }
            var bytes = blob.GetBytes(r);
            int metaLen = MetadataLength(bytes);
            builder.Append(bytes.Slice(0, metaLen), bytes.Slice(metaLen));
        }
        return builder.Build();
    }

    /// <summary>
    /// The metadata prefix length of a concatenated variant blob (header byte ++ dictionary_size ++ offsets
    /// ++ dictionary bytes — every piece sized by the header's offset_size, so the prefix is self-delimiting
    /// per the Variant binary spec v1). This is what makes the single-blob transport possible without a
    /// length prefix.
    /// </summary>
    internal static int MetadataLength(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 3)
        {
            throw new InvalidOperationException($"variant transport blob too short ({blob.Length} bytes).");
        }
        byte header = blob[0];
        int version = header & 0x0F;
        if (version != 1)
        {
            throw new InvalidOperationException($"unsupported variant metadata version {version}.");
        }
        int offsetSize = ((header >> 6) & 0x3) + 1;
        long dictSize = ReadLittleEndian(blob, 1, offsetSize);
        int offsetsStart = 1 + offsetSize;
        long lastOffset = ReadLittleEndian(blob, offsetsStart + (int)dictSize * offsetSize, offsetSize);
        long total = offsetsStart + (dictSize + 1) * offsetSize + lastOffset;
        if (total <= 0 || total >= blob.Length)
        {
            throw new InvalidOperationException("variant transport blob has a malformed metadata prefix.");
        }
        return (int)total;
    }

    private static long ReadLittleEndian(ReadOnlySpan<byte> blob, int offset, int size)
    {
        if (offset + size > blob.Length)
        {
            throw new InvalidOperationException("variant transport blob truncated inside the metadata prefix.");
        }
        long v = 0;
        for (int i = 0; i < size; i++)
        {
            v |= (long)blob[offset + i] << (8 * i);
        }
        return v;
    }

    private static Field TagTransport(Field f)
    {
        var tagged = new Dictionary<string, string>
        {
            [VariantMarker.ExtensionNameKey] = VariantMarker.ExtensionName,
        };
        if (f.Metadata is { } src)
        {
            foreach (var kv in src)
            {
                tagged[kv.Key] = kv.Value;
            }
        }
        // Our key is written FIRST and the source copied over it, so an incoming ARROW:extension:name would
        // win — deliberate: a field already tagged something else is not ours to relabel. A canonical
        // VariantArray field carries no such tag, which is the case that reaches here.
        tagged[VariantMarker.ExtensionNameKey] = VariantMarker.ExtensionName;
        return new Field(f.Name, BinaryType.Default, f.IsNullable, tagged);
    }

    private static IReadOnlyDictionary<string, string>? StripMarker(IReadOnlyDictionary<string, string>? md)
    {
        if (md is null || !md.ContainsKey(VariantMarker.ExtensionNameKey))
        {
            return md;
        }
        var copy = new Dictionary<string, string>(md.Count);
        foreach (var kv in md)
        {
            if (!string.Equals(kv.Key, VariantMarker.ExtensionNameKey, StringComparison.Ordinal))
            {
                copy[kv.Key] = kv.Value;
            }
        }
        return copy.Count == 0 ? null : copy;
    }

    private static void EnsureCopies(
        RecordBatch batch, ref List<Field>? fields, ref List<IArrowArray>? arrays)
    {
        if (fields is not null)
        {
            return;
        }
        fields = new List<Field>(batch.Schema.FieldsList);
        arrays = new List<IArrowArray>(batch.ColumnCount);
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            arrays.Add(batch.Column(i));
        }
    }

    private static RecordBatch Rebuild(
        RecordBatch batch, List<Field>? fields, List<IArrowArray>? arrays)
    {
        if (fields is null)
        {
            return batch; // nothing converted — the common case, and it allocates nothing
        }
        var sb = new Schema.Builder();
        foreach (var f in fields)
        {
            sb.Field(f);
        }
        return new RecordBatch(sb.Build(), arrays!, batch.Length);
    }
}
