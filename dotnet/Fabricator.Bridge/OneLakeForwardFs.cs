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
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

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

    // Per-IO Debug lines (open + ranged read). They exist to make remote-scan attribution measurable — the
    // "why does read_parquet([N files]) pay ~all footers before its first row" puzzle needs the open/read
    // TIMELINE (sequential vs burst, files touched before first data read), and nothing logged per IO before.
    // Chatty by nature, so Debug-gated like the per-file scan lines; ⚠ never compute log arguments before the
    // IsEnabled check on the Read path — it runs once per HTTP GET.
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.OneLake.Fs");

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
            return FabricCredentialResolver.AmbientChain();
        }
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(credJson) ?? new();
        return FabricCredentialResolver.Resolve(fields);
    }

    private static DataLakeFileSystemClient FsClient(string fsName, TokenCredential cred)
        => new DataLakeFileSystemClient(new Uri($"https://{OneLakeHost}/{fsName}"), cred);

    /// <summary>Open a file for reading: returns the handle + the file length (+ the cache-validation
    /// identity when a properties fetch happened). <paramref name="knownSize"/> &gt;= 0 (from a listing's
    /// extended info) skips the per-file GetProperties round trip — constructing the client itself does no
    /// IO, so a known-size open costs NOTHING until the first read (the host then takes etag/mtime from the
    /// listing's extended info instead).</summary>
    public static (Handle Handle, long Size, string? ETag, long ModifiedMs) Open(
        string path, string? credJson, long knownSize = -1)
    {
        var (fs, p) = Parse(path);
        var client = FsClient(fs, Cred(credJson)).GetFileClient(p);
        if (knownSize >= 0)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("onelake open {Path} size={Size} (known, no IO)", p, knownSize);
            }
            return (new Handle { Client = client, Length = knownSize }, knownSize, null, -1);
        }
        var props = client.GetPropertiesAsync().GetAwaiter().GetResult().Value;
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("onelake open {Path} size={Size} (props fetched)", p, props.ContentLength);
        }
        return (new Handle { Client = client, Length = props.ContentLength }, props.ContentLength,
                props.ETag.ToString(), props.LastModified.ToUnixTimeMilliseconds());
    }

    /// <summary>Read exactly <paramref name="nrBytes"/> at absolute <paramref name="location"/> into
    /// <paramref name="dest"/> (a host-owned span).</summary>
    public static void Read(Handle h, Span<byte> dest, long location)
    {
        if (dest.Length == 0)
        {
            return;
        }
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("onelake read {Path} off={Off} len={Len}", h.Client.Path, location, dest.Length);
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

    /// <summary>Path → size side table consulted by the literal-glob ECHO, so an echoed entry can carry the
    /// size the echo's zero-IO contract otherwise forfeits — the C++ side turns it into
    /// <c>extended_info["file_size"]</c> and <c>OpenFileExtended</c> then SKIPS the per-file properties
    /// round trip entirely (the measured ~2–5 props-opens per scanned file). Seeded by
    /// <c>DeltaNativeReader</c> from the snapshot's AddFiles — the size is part of the Delta COMMIT, and a
    /// Delta data file is immutable (UUID-named, never overwritten in place), so a seeded size cannot go
    /// stale; the same immutability argument duckdb-iceberg's multi-file reader makes when it stamps
    /// <c>validate_external_file_cache=false</c> + dummy etag/mtime on its OpenFileInfos. ⚠ Fed ONLY by the
    /// Delta reader for its own data files — a generic caller must not seed paths whose content can change,
    /// since a wrong size here reads past the end or truncates silently. Keys are the URI text EXACTLY as
    /// it appears in the generated SQL (DuckDB passes list entries to Glob verbatim — the filename-echo
    /// finding), so lookup is byte-identical by construction. Bounded: the table clears wholesale at the
    /// cap, degrading to the props fetch, never to a wrong answer.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> KnownSizes =
        new(System.StringComparer.Ordinal);

    private const int KnownSizesCap = 65536;

    /// <summary>Records a known immutable-file size for the literal-glob echo (see <see cref="KnownSizes"/>).</summary>
    public static void SeedKnownSize(string uri, long size)
    {
        if (KnownSizes.Count >= KnownSizesCap)
        {
            KnownSizes.Clear();
        }
        KnownSizes[uri] = size;
    }

    // Async so `await foreach` drives the AsyncPageable correctly (blocking the enumerator per-item with
    // GetAwaiter().GetResult() throws NotSupportedException under the hostfxr CLR); we block once, at the top.
    private static async System.Threading.Tasks.Task<string> GlobAsync(string pattern, string? credJson)
    {
        var (fs, p) = Parse(pattern);
        // Split into a directory to list + a before/after-star filter. A single '*' matches within one path
        // segment (it does NOT cross '/') — so `Tables/t/*.parquet` matches the data files at the table root
        // but NOT `Tables/t/_delta_log/0000.json` (that has an extra '/'), and the .parquet suffix is enforced.
        int star = p.IndexOf('*');
        if (star < 0)
        {
            // ⚠ A LITERAL path is ECHOED with ZERO IO — matching httpfs's own literal-glob behaviour, and it
            // is a MEASURED fix, not tidiness. This used to fall through to the recursive directory LIST
            // below and filter `name == p`: one remote LIST per literal path. DuckDB's multi-file scan globs
            // EVERY input path when its lazy file list expands at scan init, so the Delta native reader's
            // batched read_parquet([89 files]) paid 89 sequential LISTs ≈ 21.4 s on live OneLake before its
            // FIRST byte of parquet IO — the span misattributed as an "execution-phase footer sweep" until
            // the per-IO log lines showed 2 opens / 2 reads total under LIMIT 1 and a 21 s IO-silent gap.
            // (It also explains the 2026-08-14 probe pair: LIMIT 0 = 0.49 s never expands the list; LIMIT 1
            // = 15.15 s does.) The costs this trades: a missing file now errors at OPEN (a 404) instead of
            // globbing empty — the honest outcome for a path the Delta snapshot listed; and the echo carries
            // no extended_info, so a file that is actually OPENED pays one properties fetch there — UNLESS
            // the Delta reader seeded its snapshot size into KnownSizes, in which case the echo carries it
            // and OpenFileExtended skips the fetch too (still zero IO here either way).
            // A seeded entry also declares itself IMMUTABLE — the C++ Glob turns that into the per-file
            // `validate_external_file_cache = false` open option (duckdb-iceberg's own pattern for files
            // that are never modified). Without it, ExternalFileCache::IsValid compares VERSION TAGS
            // whenever EITHER side has one (external_file_cache.cpp:116), and our opens have MIXED
            // identity — listing-fed/bare opens carry a real etag, seeded echo opens an empty one — so a
            // range cached under the real etag would be judged INVALID by the next seeded open and
            // silently dropped + re-read. NO_VALIDATION removes the comparison and states the truth: a
            // Delta data file cannot change under its cached ranges.
            bool known = KnownSizes.TryGetValue(pattern, out long knownSize);
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("onelake glob {Path} (literal, echoed — no IO{Sized})", p,
                             known ? ", size seeded" : "");
            }
            return known
                ? $"[{{\"path\":\"onelake://{fs}/{p.Replace("\"", "\\\"")}\",\"size\":{knownSize.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"immutable\":true}}]"
                : $"[{{\"path\":\"onelake://{fs}/{p.Replace("\"", "\\\"")}\"}}]";
        }
        string beforeStar = star >= 0 ? p.Substring(0, star) : p;
        string afterStar = star >= 0 ? p.Substring(star + 1) : string.Empty;
        int slash = beforeStar.LastIndexOf('/');
        string dir = slash >= 0 ? beforeStar.Substring(0, slash) : string.Empty;

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("onelake glob {Pattern} (listing {Dir})", p, dir);
        }
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
                // Everything the listing gives us for FREE rides along: size + last_modified + etag (the
                // same keys httpfs surfaces) — the C++ side turns them into OpenFileInfo.extended_info so
                // subsequent opens skip the per-file properties round trip.
                sb.Append("{\"path\":\"onelake://").Append(fs).Append('/').Append(name.Replace("\"", "\\\""))
                  .Append("\",\"size\":").Append(item.ContentLength ?? 0);
                if (item.LastModified != default)
                {
                    sb.Append(",\"last_modified\":").Append(item.LastModified.ToUnixTimeMilliseconds());
                }
                var etag = item.ETag.ToString();
                if (!string.IsNullOrEmpty(etag))
                {
                    sb.Append(",\"etag\":\"").Append(etag.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                }
                sb.Append('}');
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // directory doesn't exist yet → empty result
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Does a FILE exist at exactly <paramref name="path"/>? Returns FALSE for a directory — DuckDB's
    /// partitioned-COPY setup calls FileExists on the target and errors ("exists and is a file") if a directory
    /// reports as a file; a directory must report absent-as-a-file so the write proceeds via CreateDirectory.
    /// (ADLS Gen2 / OneLake is a hierarchical namespace: a directory carries the <c>hdi_isfolder=true</c>
    /// metadata marker; a file does not.)</summary>
    public static bool Exists(string path, string? credJson)
    {
        var (fs, p) = Parse(path);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("onelake exists {Path}", p);
        }
        var client = FsClient(fs, Cred(credJson)).GetFileClient(p);
        try
        {
            var props = client.GetPropertiesAsync().GetAwaiter().GetResult().Value;
            if (props.Metadata is { } m && m.TryGetValue("hdi_isfolder", out var folder)
                && string.Equals(folder, "true", StringComparison.OrdinalIgnoreCase))
            {
                return false; // it's a directory, not a file
            }
            return true; // exists and is a file
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    // ---- WRITE (slice 2): a plain OneLake file write (COPY … TO 'onelake://…'), sequential append. ----

    /// <summary>An open write handle: the file client + the running append position.</summary>
    internal sealed class WriteHandle
    {
        public required DataLakeFileClient Client { get; init; }
        public long Position { get; set; }
    }

    /// <summary>
    /// Create the target file and return a write handle. <paramref name="exclusive"/> = put-if-absent
    /// (ADLS conditional create, If-None-Match:* — the atomic-commit primitive EXCLUSIVE_CREATE maps to;
    /// an existing target fails the create); otherwise create/overwrite.
    /// </summary>
    public static WriteHandle OpenWrite(string path, string? credJson, bool exclusive = false)
    {
        var (fs, p) = Parse(path);
        var client = FsClient(fs, Cred(credJson)).GetFileClient(p);
        var options = exclusive
            ? new DataLakePathCreateOptions
            {
                Conditions = new DataLakeRequestConditions { IfNoneMatch = ETag.All },
            }
            : null;
        client.CreateAsync(options).GetAwaiter().GetResult(); // 0-length; appends follow
        return new WriteHandle { Client = client, Position = 0 };
    }

    /// <summary>Atomic single-file rename via the DFS endpoint's native rename (a metadata op, not a copy;
    /// overwrites an existing destination — MoveFile semantics). Src and dest must be in the same workspace
    /// filesystem. The destination path is filesystem-relative (no workspace prefix) with the
    /// <c>&lt;item&gt;.Lakehouse</c> as its leading segment — exactly what <see cref="Parse"/> yields (the
    /// same OneLake quirk <c>FabricLakehouse.RenameDirectory</c> documents).</summary>
    public static void Move(string src, string dest, string? credJson)
    {
        var (srcFs, srcPath) = Parse(src);
        var (destFs, destPath) = Parse(dest);
        if (!string.Equals(srcFs, destFs, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"onelake_move: cross-workspace rename is not supported ('{srcFs}' -> '{destFs}')");
        }
        var client = FsClient(srcFs, Cred(credJson)).GetFileClient(srcPath);
        client.RenameAsync(destPath).GetAwaiter().GetResult();
    }

    /// <summary>Delete a single file (idempotent — no error if absent).</summary>
    public static void Remove(string path, string? credJson)
    {
        var (fs, p) = Parse(path);
        var client = FsClient(fs, Cred(credJson)).GetFileClient(p);
        client.DeleteIfExistsAsync().GetAwaiter().GetResult();
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
