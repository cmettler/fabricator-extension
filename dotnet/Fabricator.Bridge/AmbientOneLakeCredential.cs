using Azure.Core;

namespace Fabricator.Bridge;

/// <summary>
/// The Fabric credential (resolved by <see cref="FabricCredentialResolver"/> from the ATTACH'd azure secret, or
/// the ambient managed/workspace identity) in effect on this thread — read by <see cref="TableFileSystems.Create"/>
/// to build a direct-SDK <see cref="OneLakeDataLakeFileSystem"/> for OneLake roots instead of routing IO through
/// DuckDB's azure extension. Mirrors <see cref="AmbientOpener"/>: an <see cref="System.Threading.AsyncLocal{T}"/>
/// (concurrent scans carry independent credentials, and the value flows across <c>await</c>/pool-thread hops),
/// set by <c>DeltaCatalog</c> immediately before it calls into the reader/writer, and re-established on the bulk
/// consumer thread (<c>BulkSession</c>) across the thread hop. <c>null</c> => no Fabric credential in scope, so IO
/// falls back to the host-FS path (local / S3 / plain ADLS, or OneLake via duckdb-azure).
/// </summary>
public static class AmbientOneLakeCredential
{
    private static readonly System.Threading.AsyncLocal<TokenCredential?> _current = new();

    /// <summary>The active Fabric credential on this flow (null = none).</summary>
    public static TokenCredential? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>
/// Selects the <see cref="EngineeredWood.IO.ITableFileSystem"/> for a (opener, path): a direct Azure-SDK
/// <see cref="OneLakeDataLakeFileSystem"/> when the path is a OneLake root AND a Fabric credential is in scope
/// (<see cref="AmbientOneLakeCredential"/>), else the host-FS <see cref="DuckDbTableFileSystem"/> (DuckDB's
/// FileSystem + secrets — local / S3 / plain ADLS, or a global/connection-free reader with no credential).
/// </summary>
internal static class TableFileSystems
{
    public static EngineeredWood.IO.ITableFileSystem Create(nint opener, string path)
    {
        var cred = AmbientOneLakeCredential.Current;
        if (cred is not null && FabricLakehouse.IsOneLake(path))
        {
            return new OneLakeDataLakeFileSystem(path, cred);
        }
        // s3:// with a commit credential in scope: the hybrid FS — host-FS data IO, AWS-SDK
        // conditional-PUT commit rename (real put-if-absent; httpfs's is unguarded on S3).
        var s3 = AmbientS3Credential.Current;
        if (s3 is not null && path.TrimStart().StartsWith("s3://", System.StringComparison.OrdinalIgnoreCase))
        {
            return new S3CommitFileSystem(new DuckDbTableFileSystem(opener, path), path, s3);
        }
        return new DuckDbTableFileSystem(opener, path);
    }
}
