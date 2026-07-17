using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EngineeredWood.IO;

namespace Fabricator.Bridge;

/// <summary>
/// The credential fields of a DuckDB <c>s3</c> secret carried to the Delta catalog (via the ATTACH
/// <c>SECRET</c> v39 marker) so the COMMIT rename can run a REAL conditional PUT through the AWS SDK.
/// DuckDB's httpfs never passes <c>If-None-Match</c>, so its "exclusive create" is unguarded on S3 —
/// two racing committers can silently clobber the same version. With these fields present, S3 catalogs
/// get multi-process/multi-engine commit safety.
/// </summary>
public sealed record S3CommitCredential(
    string? KeyId, string? Secret, string? SessionToken, string? Endpoint, string? Region,
    bool PathStyle, bool UseSsl)
{
    private const string Marker = ";FabricatorS3Cred=";

    /// <summary>Appends the s3-secret fields as a credential marker to the Delta root connection string
    /// (mirrors <c>FabricLakehouse.AppendCredMarker</c>).</summary>
    public static string AppendMarker(string root, IReadOnlyDictionary<string, string> fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return root;
        }
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)));
        return root + Marker + b64;
    }

    /// <summary>Splits a connection string into the bare root and an optional S3 commit credential.</summary>
    public static (string Root, S3CommitCredential? Credential) Extract(string connectionString)
    {
        int idx = connectionString.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return (connectionString, null);
        }
        var root = connectionString.Substring(0, idx);
        var b64 = connectionString.Substring(idx + Marker.Length);
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(
                         Encoding.UTF8.GetString(Convert.FromBase64String(b64)))
                     ?? new Dictionary<string, string>();
        string? Get(params string[] names)
        {
            foreach (var n in names)
            {
                foreach (var kv in fields)
                {
                    if (string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        return kv.Value;
                    }
                }
            }
            return null;
        }
        return (root, new S3CommitCredential(
            Get("key_id"), Get("secret"), Get("session_token"), Get("endpoint"), Get("region"),
            PathStyle: string.Equals(Get("url_style"), "path", StringComparison.OrdinalIgnoreCase),
            UseSsl: !string.Equals(Get("use_ssl"), "false", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Get("use_ssl"), "0", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>The S3 commit credential in effect on this flow (null = none) — read by
/// <see cref="TableFileSystems.Create"/> to wrap <c>s3://</c> roots with
/// <see cref="S3CommitFileSystem"/>. Mirrors <see cref="AmbientOneLakeCredential"/>: published by
/// <c>DeltaCatalog.Opener()</c> immediately before catalog work (unconditionally, so a reused execution
/// thread never carries another catalog's stale credential).</summary>
public static class AmbientS3Credential
{
    private static readonly AsyncLocal<S3CommitCredential?> _current = new();

    public static S3CommitCredential? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>
/// The hybrid S3 filesystem for Delta commits: ALL IO delegates to the host-FS
/// <see cref="DuckDbTableFileSystem"/> (opener-resolved secrets, DuckDB's transport + caching) EXCEPT
/// <see cref="RenameAsync"/> — engineered-wood's commit primitive — which runs a REAL put-if-absent
/// through the AWS SDK: read the temp object, <c>PutObject</c> the target with <c>If-None-Match:"*"</c>
/// (412 on an existing target → false → <c>DeltaConflictException</c> upstream), delete the temp.
/// PutObject is the conditional-write primitive S3 (Nov 2024) and MinIO actually enforce — probed:
/// MinIO 412s a conditional PUT but SILENTLY IGNORES a conditional CopyObject, so a copy-based
/// "atomic rename" is NOT guarded. Commit files are small JSON, so read+put costs nothing.
/// TLS: a CUSTOM endpoint (MinIO/on-prem) accepts self-signed certificates — the same posture as the
/// test rig's <c>enable_curl_server_cert_verification=false</c>; the AWS default endpoint keeps full
/// certificate validation.
/// </summary>
internal sealed class S3CommitFileSystem : ITableFileSystem
{
    private readonly ITableFileSystem _inner;
    private readonly S3CommitCredential _cred;
    private readonly string _bucket;
    private readonly string _prefix; // "" or "a/b/" (trailing slash)
    private readonly Lazy<IAmazonS3> _client;

    public S3CommitFileSystem(ITableFileSystem inner, string rootS3Uri, S3CommitCredential cred)
    {
        _inner = inner;
        _cred = cred;
        (_bucket, _prefix) = ParseS3Url(rootS3Uri);
        _client = new Lazy<IAmazonS3>(() => BuildClient(_cred));
    }

    /// <summary>True when <paramref name="path"/> is an <c>s3://</c> URL.</summary>
    public static bool IsS3(string path) =>
        path.TrimStart().StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits an s3 URL into (bucket, prefix) — prefix "" or with a trailing slash.</summary>
    private static (string Bucket, string Prefix) ParseS3Url(string url)
    {
        var rest = url.Replace('\\', '/');
        const string scheme = "s3://";
        if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest.Substring(scheme.Length);
        }
        rest = rest.Trim('/');
        int slash = rest.IndexOf('/');
        var bucket = slash < 0 ? rest : rest.Substring(0, slash);
        var prefix = slash < 0 ? string.Empty : rest.Substring(slash + 1).TrimEnd('/') + "/";
        return (bucket, prefix);
    }

    private static IAmazonS3 BuildClient(S3CommitCredential cred)
    {
        var cfg = new AmazonS3Config { ForcePathStyle = cred.PathStyle };
        if (!string.IsNullOrEmpty(cred.Endpoint))
        {
            cfg.ServiceURL = (cred.UseSsl ? "https://" : "http://") + cred.Endpoint;
            // custom endpoint (MinIO/on-prem): tolerate self-signed certs, like the DuckDB-side rig
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };
            cfg.HttpClientFactory = new PermissiveHttpClientFactory(handler);
        }
        else if (!string.IsNullOrEmpty(cred.Region))
        {
            cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(cred.Region);
        }
        AWSCredentials creds = string.IsNullOrEmpty(cred.KeyId)
            ? FallbackCredentialsFactory.GetCredentials() // SDK default chain (env/profile/role)
            : string.IsNullOrEmpty(cred.SessionToken)
                ? new BasicAWSCredentials(cred.KeyId, cred.Secret)
                : new SessionAWSCredentials(cred.KeyId, cred.Secret, cred.SessionToken);
        return new AmazonS3Client(creds, cfg);
    }

    /// <summary>
    /// Renames a whole table folder SERVER-SIDE: list every object under <paramref name="srcUrl"/>, copy
    /// each to the same suffix under <paramref name="dstUrl"/> via <c>CopyObject</c> (in-cluster — no data
    /// crosses the client, unlike a host-FS read/write fallback), then batch-delete the sources. Copy-ALL-
    /// then-delete: a mid-copy failure leaves the SOURCE table fully intact (the partial destination is
    /// inert garbage without its complete <c>_delta_log</c>). Backs committed-table RENAME TABLE on S3 —
    /// httpfs has no MoveFile — and requires the SECRET-routed attach (the SDK credential). NOTE a single
    /// CopyObject call caps at 5 GB/object (larger would need multipart copy; our data files are far
    /// below — a violation surfaces as the SDK error with the source intact).
    /// </summary>
    public static void RenameDirectory(string srcUrl, string dstUrl, S3CommitCredential cred)
        => RenameDirectoryAsync(srcUrl, dstUrl, cred).GetAwaiter().GetResult();

    private static async Task RenameDirectoryAsync(string srcUrl, string dstUrl, S3CommitCredential cred)
    {
        // Cancellable on query interrupt (the opener is set fresh by the ALTER operator).
        using var interrupt = new InterruptScope(AmbientOpener.Current);
        var token = interrupt.Token;
        var (srcBucket, srcPrefix) = ParseS3Url(srcUrl);
        var (dstBucket, dstPrefix) = ParseS3Url(dstUrl);
        if (!string.Equals(srcBucket, dstBucket, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"delta RENAME TABLE on S3: source and destination must share a bucket ('{srcBucket}' vs '{dstBucket}').");
        }
        using var c = BuildClient(cred);
        var keys = new List<string>();
        string? continuation = null;
        do
        {
            var resp = await c.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = srcBucket,
                Prefix = srcPrefix,
                ContinuationToken = continuation,
            }, token).ConfigureAwait(false);
            if (resp.S3Objects is not null)
            {
                foreach (var o in resp.S3Objects)
                {
                    keys.Add(o.Key);
                }
            }
            continuation = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (continuation is not null);
        if (keys.Count == 0)
        {
            throw new FileNotFoundException($"delta RENAME TABLE: no objects under '{srcUrl}'.");
        }
        foreach (var key in keys)
        {
            await c.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = srcBucket,
                SourceKey = key,
                DestinationBucket = dstBucket,
                DestinationKey = dstPrefix + key.Substring(srcPrefix.Length),
            }, token).ConfigureAwait(false);
        }
        for (int i = 0; i < keys.Count; i += 1000) // DeleteObjects caps at 1000 keys per call
        {
            var batch = new List<KeyVersion>();
            for (int j = i; j < keys.Count && j < i + 1000; j++)
            {
                batch.Add(new KeyVersion { Key = keys[j] });
            }
            await c.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = srcBucket, Objects = batch }, token)
                .ConfigureAwait(false);
        }
    }

    private sealed class PermissiveHttpClientFactory : Amazon.Runtime.HttpClientFactory
    {
        private readonly System.Net.Http.HttpClientHandler _handler;
        public PermissiveHttpClientFactory(System.Net.Http.HttpClientHandler handler) => _handler = handler;
        public override System.Net.Http.HttpClient CreateHttpClient(IClientConfig _) =>
            new System.Net.Http.HttpClient(_handler);
    }

    private string Key(string path) => _prefix + path.Replace('\\', '/').TrimStart('/');

    /// <summary>The put-if-absent commit rename (see the class doc). False on 412 = a concurrent writer
    /// claimed the target version — engineered-wood surfaces it as <c>DeltaConflictException</c> and the
    /// OCC/rebase machinery takes over.</summary>
    public async ValueTask<bool> RenameAsync(string sourcePath, string targetPath,
                                             CancellationToken cancellationToken = default)
    {
        var c = _client.Value;
        string source = Key(sourcePath);
        string target = Key(targetPath);
        using var got = await c.GetObjectAsync(_bucket, source, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await got.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        try
        {
            await c.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = target,
                InputStream = buffer,
                IfNoneMatch = "*",
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
        // Target committed; the temp is best-effort cleanup (a leftover temp is an invisible orphan).
        try
        {
            await c.DeleteObjectAsync(_bucket, source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
        return true;
    }

    // ---- everything else: the host-FS path (opener secrets, DuckDB transport + caching) ----
    public IAsyncEnumerable<TableFileInfo> ListAsync(string prefix, CancellationToken ct = default)
        => _inner.ListAsync(prefix, ct);
    public ValueTask<IRandomAccessFile> OpenReadAsync(string path, CancellationToken ct = default)
        => _inner.OpenReadAsync(path, ct);
    public ValueTask<ISequentialFile> CreateAsync(string path, bool overwrite = false, CancellationToken ct = default)
        => _inner.CreateAsync(path, overwrite, ct);
    public ValueTask DeleteAsync(string path, CancellationToken ct = default)
        => _inner.DeleteAsync(path, ct);
    public ValueTask<bool> ExistsAsync(string path, CancellationToken ct = default)
        => _inner.ExistsAsync(path, ct);
    public async ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        // Small-file reads (commit JSONs + the MUTABLE _last_checkpoint pointer) go through the SDK:
        // DuckDB's httpfs pins the etag recorded at open and re-serves it from its handle/metadata
        // caches, so a CONCURRENT writer's in-place checkpoint overwrite fails the host read with
        // "ETag ... has changed" — and retries keep failing against the cached etag. A plain GetObject
        // has no pinned etag and always returns a consistent copy. Data files stay on the host path
        // (OpenReadAsync) — they are immutable, cache-friendly, and large.
        var c = _client.Value;
        using var got = await c.GetObjectAsync(_bucket, Key(path), ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await got.ResponseStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
    public ValueTask WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => _inner.WriteAllBytesAsync(path, data, ct);
}
