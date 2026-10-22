// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The <c>ew.variant_transport</c> Arrow boundary discriminator — a PRIVATE transport marker (field metadata
/// <c>ARROW:extension:name = ew.variant_transport</c> on a BINARY column carrying one self-delimiting blob per
/// row: metadata bytes ++ value bytes). This is the C++↔C# crossing form the extension's ArrowTypeExtension
/// registry (<c>fabricator_variant.cpp</c>) keys on; it is deliberately NOT the canonical
/// <c>arrow.parquet.variant</c> struct extension (a nested internal type crashes DuckDB's
/// <c>ArrowAppender::FinalizeChild</c>, and a canonical name would collide with built-in handlers).
///
/// <para>The fork's engineered-wood recognized this marker in its SchemaConverter; clast master instead
/// models variant as Apache.Arrow's <see cref="Apache.Arrow.Types.VariantType"/> — so the detector (and the
/// blob⇄struct conversion at the EW boundary) is now the Bridge's own concern. This helper is the detector;
/// see the migration notes for the transport adaptation.</para>
/// </summary>
public static class VariantMarker
{
    /// <summary>The Arrow field-metadata key carrying an extension type's name.</summary>
    public const string ExtensionNameKey = "ARROW:extension:name";

    /// <summary>The CANONICAL parquet-variant extension name. ⚠ This was <c>ew.variant_transport</c>
    /// until DuckDB 2.0: the private leaf-blob transport existed because a nested extension type crashed
    /// DuckDB's <c>ArrowAppender::FinalizeChild</c>, and 2.0 registers
    /// <c>arrow.parquet.variant</c> itself with the canonical <c>struct&lt;metadata, value&gt;</c>
    /// storage — which is the shape the transport was invented to avoid.</summary>
    public const string ExtensionName = "arrow.parquet.variant";

    /// <summary>
    /// True when <paramref name="field"/> is a VARIANT — either as Apache.Arrow's
    /// <see cref="VariantType"/> (what engineered-wood models it as) or as a struct carrying the canonical
    /// extension NAME in its field metadata (what crosses the C data interface, because Apache.Arrow's C#
    /// importer does not materialise the extension type from the metadata — MEASURED: a CREATE committed
    /// a Delta <c>struct&lt;metadata:binary,value:binary&gt;</c> instead of <c>variant</c> when this
    /// detector only knew the type).
    /// </summary>
    public static bool IsVariantArrowField(Field field) =>
        field.DataType is VariantType
        || (field.Metadata is { } md
            && md.TryGetValue(ExtensionNameKey, out var ext)
            && string.Equals(ext, ExtensionName, System.StringComparison.Ordinal));

    /// <summary>
    /// Gives engineered-wood the type it needs: every canonical-MARKED field becomes a
    /// canonical <see cref="VariantType"/> field with the marker stripped, at any nesting depth. This is
    /// what lets engineered-wood's <c>SchemaConverter</c> run UNPATCHED — upstream already maps
    /// <see cref="VariantType"/> to Delta <c>variant</c> and <c>variant</c> back to
    /// <see cref="VariantType"/>, so a canonical schema needs no marker awareness there. A schema that
    /// carries no marker returns unchanged (reference-identical), so this is free on the common path.
    /// </summary>
    public static Schema ToCanonicalSchema(Schema schema)
    {
        List<Field>? converted = null;
        var fields = schema.FieldsList;
        for (int i = 0; i < fields.Count; i++)
        {
            var f = ToCanonicalField(fields[i]);
            if (!ReferenceEquals(f, fields[i]) && converted is null)
            {
                converted = new List<Field>(fields.Count);
                for (int j = 0; j < i; j++)
                {
                    converted.Add(fields[j]);
                }
            }
            converted?.Add(f);
        }
        return converted is null ? schema : new Schema(converted, schema.Metadata);
    }

    /// <summary>Field-level <see cref="ToCanonicalSchema"/> — the ALTER ADD COLUMN path crosses a single
    /// field, and the marker is the ONLY thing distinguishing a variant from an ordinary blob there.</summary>
    public static Field ToCanonicalField(Field field)
    {
        if (field.DataType is VariantType)
        {
            return field; // already canonical — nothing to do
        }
        if (IsVariantArrowField(field))
        {
            // A canonical-marked STRUCT arriving over the C data interface. Strip the marker and give EW
            // the real type: its SchemaConverter maps `VariantType => PrimitiveType { TypeName = "variant" }`
            // and will otherwise write a Delta STRUCT — into a metaData commit that cannot be revised.
            var meta = StripMarker(field.Metadata);
            return new Field(field.Name, VariantType.Default, field.IsNullable, meta);
        }
        var t = ToCanonicalType(field.DataType);
        return ReferenceEquals(t, field.DataType)
            ? field
            : new Field(field.Name, t, field.IsNullable, field.Metadata);
    }

    private static IReadOnlyDictionary<string, string>? StripMarker(
        IReadOnlyDictionary<string, string>? md)
    {
        if (md is null)
        {
            return null;
        }
        Dictionary<string, string>? copy = null;
        foreach (var kv in md)
        {
            if (string.Equals(kv.Key, ExtensionNameKey, System.StringComparison.Ordinal))
            {
                continue;
            }
            (copy ??= new Dictionary<string, string>(md.Count))[kv.Key] = kv.Value;
        }
        return copy;
    }

    private static IArrowType ToCanonicalType(IArrowType type)
    {
        switch (type)
        {
            case StructType st:
            {
                List<Field>? converted = null;
                for (int i = 0; i < st.Fields.Count; i++)
                {
                    var f = ToCanonicalField(st.Fields[i]);
                    if (!ReferenceEquals(f, st.Fields[i]) && converted is null)
                    {
                        converted = new List<Field>(st.Fields.Count);
                        for (int j = 0; j < i; j++)
                        {
                            converted.Add(st.Fields[j]);
                        }
                    }
                    converted?.Add(f);
                }
                return converted is null ? type : new StructType(converted);
            }
            case ListType lt:
            {
                var elem = ToCanonicalField(lt.ValueField);
                return ReferenceEquals(elem, lt.ValueField) ? type : new ListType(elem);
            }
            case MapType mt:
            {
                var key = ToCanonicalField(mt.KeyField);
                var val = ToCanonicalField(mt.ValueField);
                return ReferenceEquals(key, mt.KeyField) && ReferenceEquals(val, mt.ValueField)
                    ? type
                    : new MapType(key, val, mt.KeySorted);
            }
            default:
                return type;
        }
    }

}
