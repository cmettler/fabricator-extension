using ArrowNet.Bridge;

namespace ArrowNet.DeltaRs;

/// <summary>
/// Delta Lake provider backed by delta-rs (via the delta-dotnet binding). Provider name <c>"deltars"</c>
/// (alias <c>"delta-rs"</c>). Coexists with the pure-C# engineered-wood provider (<c>"engineeredwooddelta"</c>,
/// the default). delta-rs performs its own object_store IO, so this provider does NOT use the host-FS bridge;
/// cloud credentials are passed as <c>storage_options</c> derived from the ATTACH'd secret. See
/// docs/delta-rs-provider.md.
/// </summary>
public sealed class DeltaRsBackend : IBackend
{
    public string Name => "deltars";

    public IEnumerable<string> Aliases => new[] { "delta-rs" };

    /// <summary>
    /// Maps an ATTACH'd foreign secret to delta-rs <c>object_store</c> options, encoded onto the connection
    /// string for <see cref="OpenCatalog"/> to parse. v1 supports azure (account/SP) + s3 keys; without a
    /// secret the ATTACH target (a path/URI) is used verbatim.
    /// </summary>
    public string BuildConnectionString(
        string secretType, IReadOnlyDictionary<string, string> fields, string baseConnString)
        => StorageOptionsCodec.Encode(secretType, fields, baseConnString);

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson)
        => new DeltaRsCatalog(connectionString, optionsJson);
}
