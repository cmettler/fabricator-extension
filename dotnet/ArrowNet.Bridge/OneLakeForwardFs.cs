using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;

namespace ArrowNet.Bridge;

/// <summary>
/// The managed side of the Phase-3 <c>onelake://</c> DuckDB FileSystem subsystem: the C++ onelake FS (registered
/// in DuckDB's VFS) forwards its read ops here (via the <c>onelake_*</c> vtable entries), and this does the actual
/// IO through the Azure DataLake SDK — so DuckDB's native readers + <c>ExternalFileCache</c> use OneLake uniformly
/// while bypassing duckdb-azure's OneLake gaps. Read-only for now.
///
/// <para>Path form: <c>onelake://&lt;workspace&gt;/&lt;pathUnderFilesystem&gt;</c> (e.g.
/// <c>onelake://Test/LH.Lakehouse/Tables/t/part-….parquet</c>) → account <c>onelake.dfs.fabric.microsoft.com</c>,
/// DFS filesystem = the workspace, path = the rest. The credential comes as <c>credJson</c> — the fields of the
/// azure secret the host resolved from the calling opener (empty/<c>{}</c> ⇒ <see cref="DefaultAzureCredential"/>),
/// built via <see cref="FabricCredentialResolver"/>. All IO uses the ASYNC DataLake APIs blocked with
/// <c>GetAwaiter().GetResult()</c> — the sync APIs hang under the hostfxr-hosted CLR (the documented gotcha).</para>
/// </summary>
internal static class OneLakeForwardFs
{
    private const string OneLakeHost = "onelake.dfs.fabric.microsoft.com";

    /// <summary>An open read handle: the file client + its length (fetched once at open, cached C++-side too).</summary>
    internal sealed class Handle
    {
        public required DataLakeFileClient Client { get; init; }
        public long Length { get; init; }
    }

    // onelake://<workspace>/<pathUnderFs...>  →  (workspace, pathUnderFs)
    private static (string FileSystem, string Path) Parse(string uri)
    {
        var s = uri.Replace('\\', '/').Trim();
        const string scheme = "onelake://";
        if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(scheme.Length);
        }
        s = s.TrimStart('/');
        int slash = s.IndexOf('/');
        string fs = slash >= 0 ? s.Substring(0, slash) : s;
        string path = slash >= 0 ? s.Substring(slash + 1) : string.Empty;
        return (fs, path.TrimEnd('/'));
    }

    private static TokenCredential Cred(string? credJson)
    {
        if (string.IsNullOrWhiteSpace(credJson) || credJson == "{}")
        {
            return new DefaultAzureCredential();
        }
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(credJson) ?? new();
        return FabricCredentialResolver.Resolve(fields);
    }

    private static DataLakeFileSystemClient FsClient(string fsName, TokenCredential cred)
        => new DataLakeFileSystemClient(new Uri($"https://{OneLakeHost}/{fsName}"), cred);

    /// <summary>Open a file for reading: returns the handle + the file length.</summary>
    public static (Handle Handle, long Size) Open(string path, string? credJson)
    {
        var (fs, p) = Parse(path);
        var client = FsClient(fs, Cred(credJson)).GetFileClient(p);
        long len = client.GetPropertiesAsync().GetAwaiter().GetResult().Value.ContentLength;
        return (new Handle { Client = client, Length = len }, len);
    }

    /// <summary>Read exactly <paramref name="nrBytes"/> at absolute <paramref name="location"/> into
    /// <paramref name="dest"/> (a host-owned span).</summary>
    public static void Read(Handle h, Span<byte> dest, long location)
    {
        if (dest.Length == 0)
        {
            return;
        }
        Response<FileDownloadInfo> resp = h.Client
            .ReadAsync(new DataLakeFileReadOptions { Range = new HttpRange(location, dest.Length) })
            .GetAwaiter().GetResult();
        using Stream content = resp.Value.Content;
        int total = 0;
        while (total < dest.Length)
        {
            int n = content.Read(dest.Slice(total));
            if (n == 0)
            {
                throw new IOException(
                    $"onelake_read: short read ({total}/{dest.Length}) at offset {location}");
            }
            total += n;
        }
    }

    /// <summary>Glob an <c>onelake://</c> pattern → JSON array of <c>{path,size}</c> (paths as full onelake:// URIs).
    /// Handles a trailing <c>*</c> / a directory listing; a missing directory ⇒ empty (fresh table).</summary>
    public static string Glob(string pattern, string? credJson)
        => GlobAsync(pattern, credJson).GetAwaiter().GetResult();

    // Async so `await foreach` drives the AsyncPageable correctly (blocking the enumerator per-item with
    // GetAwaiter().GetResult() throws NotSupportedException under the hostfxr CLR); we block once, at the top.
    private static async System.Threading.Tasks.Task<string> GlobAsync(string pattern, string? credJson)
    {
        var (fs, p) = Parse(pattern);
        // Split into a directory to list + a before/after-star filter. A single '*' matches within one path
        // segment (it does NOT cross '/') — so `Tables/t/*.parquet` matches the data files at the table root
        // but NOT `Tables/t/_delta_log/0000.json` (that has an extra '/'), and the .parquet suffix is enforced.
        int star = p.IndexOf('*');
        string beforeStar = star >= 0 ? p.Substring(0, star) : p;
        string afterStar = star >= 0 ? p.Substring(star + 1) : string.Empty;
        int slash = beforeStar.LastIndexOf('/');
        string dir = slash >= 0 ? beforeStar.Substring(0, slash) : string.Empty;

        var client = FsClient(fs, Cred(credJson));
        var sb = new StringBuilder("[");
        bool firstItem = true;
        try
        {
            await foreach (var item in client.GetPathsAsync(dir.Length == 0 ? null : dir, recursive: true)
                               .ConfigureAwait(false))
            {
                if (item.IsDirectory == true)
                {
                    continue;
                }
                var name = (item.Name ?? string.Empty).Replace('\\', '/');
                bool match;
                if (star < 0)
                {
                    match = name == p; // exact path, no wildcard
                }
                else
                {
                    // startsWith(before) && endsWith(after) && the '*'-matched middle has no '/' (single segment).
                    match = name.StartsWith(beforeStar, StringComparison.Ordinal)
                            && name.EndsWith(afterStar, StringComparison.Ordinal)
                            && name.Length >= beforeStar.Length + afterStar.Length
                            && name.Substring(beforeStar.Length, name.Length - beforeStar.Length - afterStar.Length)
                                   .IndexOf('/') < 0;
                }
                if (!match)
                {
                    continue;
                }
                if (!firstItem)
                {
                    sb.Append(',');
                }
                firstItem = false;
                sb.Append("{\"path\":\"onelake://").Append(fs).Append('/').Append(name.Replace("\"", "\\\""))
                  .Append("\",\"size\":").Append(item.ContentLength ?? 0).Append('}');
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // directory doesn't exist yet → empty result
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static bool Exists(string path, string? credJson)
    {
        var (fs, p) = Parse(path);
        var client = FsClient(fs, Cred(credJson)).GetFileClient(p);
        return client.ExistsAsync().GetAwaiter().GetResult().Value;
    }

    // ---- WRITE (slice 2): a plain OneLake file write (COPY … TO 'onelake://…'), sequential append. ----

    /// <summary>An open write handle: the file client + the running append position.</summary>
    internal sealed class WriteHandle
    {
        public required DataLakeFileClient Client { get; init; }
        public long Position { get; set; }
    }

    /// <summary>Create (overwrite) the target file and return a write handle.</summary>
    public static WriteHandle OpenWrite(string path, string? credJson)
    {
        var (fs, p) = Parse(path);
        var client = FsClient(fs, Cred(credJson)).GetFileClient(p);
        client.CreateAsync().GetAwaiter().GetResult(); // create/overwrite (0-length); appends follow
        return new WriteHandle { Client = client, Position = 0 };
    }

    /// <summary>Append `data` at the current position.</summary>
    public static void Write(WriteHandle h, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }
        // AppendAsync needs a Stream; copy the span into a MemoryStream (the host buffer is not retained).
        var bytes = data.ToArray();
        using var ms = new MemoryStream(bytes, writable: false);
        h.Client.AppendAsync(ms, h.Position).GetAwaiter().GetResult();
        h.Position += bytes.Length;
    }

    /// <summary>Flush + commit at the final length.</summary>
    public static void CloseWrite(WriteHandle h)
    {
        h.Client.FlushAsync(h.Position).GetAwaiter().GetResult();
    }
}
