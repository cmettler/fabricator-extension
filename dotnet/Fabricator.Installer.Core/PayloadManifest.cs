using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fabricator.Installer;

/// <summary>
/// Describes an artifact's payload. Stored UNCOMPRESSED between the payload archive and the
/// polyglot index (see <see cref="PolyglotIndex"/>), so both the compatibility gate and the
/// idempotence check read it with one small tail read and no decompression — that is what keeps
/// the steady-state load path cheap.
/// </summary>
/// <remarks>
/// It carries <see cref="PayloadSha256"/> of the archive that PRECEDES it, which is why the
/// manifest lives outside the archive: a sha inside the thing it hashes would be circular.
/// </remarks>
public sealed class PayloadManifest
{
    /// <summary>Manifest schema version; bumped only on a breaking field change.</summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>The fabricator extension version this payload was built from (e.g. <c>0.0.1</c>).</summary>
    public string FabricatorVersion { get; init; } = "";

    /// <summary>
    /// The DuckDB version the inner core was compiled against, normalized with a leading <c>v</c>.
    /// The core uses the CPP ABI, so this is an EXACT requirement (extension.cpp:51-58) — hence the
    /// gate in <see cref="CompatibilityGate"/>.
    /// </summary>
    public string TargetDuckDbVersion { get; init; } = "";

    /// <summary>DuckDB's platform string (<c>PRAGMA platform</c>), e.g. <c>windows_amd64</c>.</summary>
    public string Platform { get; init; } = "";

    /// <summary>Which payload flavour this is: <c>standard</c> (framework-dependent) or <c>standalone</c>.</summary>
    public string Sku { get; init; } = "";

    /// <summary>Name of the core loadable inside the archive and on disk after extraction.</summary>
    public string CoreFileName { get; init; } = FabricatorPayloadNames.CoreFile;

    /// <summary>Name of the managed directory inside the archive and on disk after extraction.</summary>
    public string ManagedDirectoryName { get; init; } = FabricatorPayloadNames.ManagedDirectory;

    /// <summary>Lowercase hex SHA-256 of the payload archive bytes. Doubles as the marker value.</summary>
    public string PayloadSha256 { get; init; } = "";

    /// <summary>Length of the payload archive in bytes; cross-checked against the index.</summary>
    public long PayloadLength { get; init; }

    /// <summary>Number of files in the archive; diagnostic only.</summary>
    public int EntryCount { get; init; }

    /// <summary>Serializes compactly and deterministically (source-generated; no reflection).</summary>
    public byte[] ToJsonUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, PayloadManifestJson.Default.PayloadManifest);

    /// <summary>Parses a manifest, throwing <see cref="InstallerException"/> on malformed input.</summary>
    public static PayloadManifest FromJsonUtf8(ReadOnlySpan<byte> utf8)
    {
        PayloadManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(utf8, PayloadManifestJson.Default.PayloadManifest);
        }
        catch (JsonException ex)
        {
            throw new InstallerException("The fabricator payload manifest is not valid JSON: " + ex.Message, ex);
        }

        return manifest ?? throw new InstallerException("The fabricator payload manifest is empty.");
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PayloadManifest))]
internal sealed partial class PayloadManifestJson : JsonSerializerContext;
