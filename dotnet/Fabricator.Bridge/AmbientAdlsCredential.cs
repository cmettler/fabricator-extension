using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The ADLS Gen2 storage credential (resolved by <see cref="AdlsCredential.FromFields"/> from the ATTACH'd
/// azure secret, or the ambient managed/workspace identity) in effect on this thread — read by
/// <see cref="TableFileSystems.Create"/> to build a direct-SDK <see cref="AdlsGen2TableFileSystem"/> for
/// <c>abfss://</c> roots instead of routing IO through DuckDB's azure extension. Mirrors
/// <see cref="AmbientOpener"/>: an <see cref="System.Threading.AsyncLocal{T}"/> (concurrent scans carry
/// independent credentials, and the value flows across <c>await</c>/pool-thread hops), set by
/// <c>DeltaCatalog</c> immediately before it calls into the reader/writer, and re-established on the bulk
/// consumer thread (<c>BulkSession</c>) across the thread hop. <c>null</c> => no storage credential in scope,
/// so IO falls back to the host-FS path (local / S3, or ADLS via duckdb-azure).
/// </summary>
public static class AmbientAdlsCredential
{
    private static readonly System.Threading.AsyncLocal<AdlsCredential?> _current = new();

    /// <summary>The active ADLS credential on this flow (null = none).</summary>
    public static AdlsCredential? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>
/// Selects the <see cref="EngineeredWood.IO.ITableFileSystem"/> for a (opener, path): a direct Azure-SDK
/// <see cref="AdlsGen2TableFileSystem"/> when the path is an <c>abfss://</c> root AND a storage credential is in
/// scope (<see cref="AmbientAdlsCredential"/>), else the host-FS <see cref="DuckDbTableFileSystem"/> (DuckDB's
/// FileSystem + secrets — local / S3, or a global/connection-free reader with no credential).
///
/// <para><b>Why the direct SDK is the right path for ALL of ADLS, not just OneLake.</b> The selector used to
/// ask <c>IsOneLake</c>, so a plain storage account fell through to duckdb-azure — which cannot commit safely:
/// its <c>ExclusiveCreate</c> is a client-side existence CHECK, not a conditional PUT, so two writers at the
/// same Delta version both pass it and one silently wins. MEASURED on a live account: 6 writers × 8 commits
/// landed 41 of 48, and six of the seven losses raised no error at all
/// (docs/delta-transactions.md §8.4). The direct-SDK filesystem's <c>RenameAsync</c> is a true
/// <c>If-None-Match:*</c> create, which is the primitive the commit actually needs. OneLake was never special
/// here — it was simply the one account shape we happened to route correctly.</para>
/// </summary>
internal static class TableFileSystems
{
    private static readonly Microsoft.Extensions.Logging.ILogger _log =
        FabricatorLog.CreateLogger("Fabricator.Delta.Fs");

    public static EngineeredWood.IO.ITableFileSystem Create(nint opener, string path)
        => Create(opener, path, outlivesThisCall: false);

    /// <summary>
    /// <paramref name="outlivesThisCall"/>: the filesystem will be held by something cached across ABI calls
    /// (<see cref="DeltaTableCache"/>), so it must NOT capture the host opener.
    /// </summary>
    /// <remarks>
    /// ⚠ The opener is a <c>ClientContext*</c> valid only for the call that handed it to us.
    /// <c>DuckDbTableFileSystem.Opener</c> prefers the <c>AmbientOpener</c> and keeps the constructed value
    /// as a fallback — safe, as its own comment says, "because no object outlives its call today", and
    /// "load-bearing the moment something is cached". Passing 0 makes the ambient the ONLY source, so a
    /// context the AsyncLocal did not flow into fails loudly instead of driving IO through a dangling
    /// pointer — a use-after-free that neither Windows nor glibc would necessarily fault on (the asymmetry
    /// that hid the late <c>ArrowProducer</c> release everywhere except macOS).
    /// </remarks>
    public static EngineeredWood.IO.ITableFileSystem Create(nint opener, string path, bool outlivesThisCall)
    {
        if (outlivesThisCall)
        {
            opener = 0;
        }
        var adls = AmbientAdlsCredential.Current;
        if (adls is not null && AdlsPath.IsAdlsGen2(path))
        {
            // WHICH filesystem a catalog picked is not otherwise observable from SQL, and it decides whether
            // the commit is atomic — so a remote root that quietly took the host-FS fallback looks identical
            // to one that did not until concurrent writers start losing commits. Debug-level, one line per
            // table open.
            _log.LogDebug("delta fs {Path}: AdlsGen2TableFileSystem (direct Azure DataLake SDK)", path);
            return new AdlsGen2TableFileSystem(path, adls);
        }
        if (AdlsPath.IsAdlsGen2(path))
        {
            _log.LogDebug("delta fs {Path}: DuckDbTableFileSystem (host FS) — no ADLS credential in scope, "
                          + "so the commit guard is NOT atomic here", path);
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
