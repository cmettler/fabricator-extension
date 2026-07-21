using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// The <c>fabricator.variant</c> Arrow boundary discriminator — a PRIVATE transport marker (field metadata
/// <c>ARROW:extension:name = fabricator.variant</c> on a BINARY column carrying one self-delimiting blob per
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

    /// <summary>The fabricator variant transport's extension name.</summary>
    public const string ExtensionName = "fabricator.variant";

    /// <summary>True when <paramref name="field"/> carries the fabricator variant transport marker.</summary>
    public static bool IsVariantArrowField(Field field) =>
        field.Metadata is { } md
        && md.TryGetValue(ExtensionNameKey, out var ext)
        && string.Equals(ext, ExtensionName, System.StringComparison.Ordinal);
}
