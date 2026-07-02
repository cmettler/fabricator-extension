using Azure.Core;

namespace ArrowNet.Bridge;

/// <summary>
/// The Fabric credential (resolved by <see cref="FabricCredentialResolver"/> from the ATTACH'd azure secret, or
/// the ambient managed/workspace identity) in effect on this thread — read by <see cref="TableFileSystems.Create"/>
/// to build a direct-SDK <see cref="OneLakeDataLakeFileSystem"/> for OneLake roots instead of routing IO through
/// DuckDB's azure extension. Mirrors <see cref="AmbientOpener"/>: <c>[ThreadStatic]</c> (concurrent scans carry
/// independent credentials), set by <c>DeltaCatalog</c> immediately before it calls into the reader/writer, and
/// re-established on the bulk consumer thread (<c>BulkSession</c>) across the thread hop. <c>null</c> => no Fabric
/// credential in scope, so IO falls back to the host-FS path (local / S3 / plain ADLS, or OneLake via duckdb-azure).
/// </summary>
public static class AmbientOneLakeCredential
{
    [System.ThreadStatic] private static TokenCredential? _current;

    /// <summary>The active Fabric credential on this thread (null = none).</summary>
    public static TokenCredential? Current
    {
        get => _current;
        set => _current = value;
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
        return new DuckDbTableFileSystem(opener, path);
    }
}
