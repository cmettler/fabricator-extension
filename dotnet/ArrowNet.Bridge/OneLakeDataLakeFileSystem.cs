using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using EngineeredWood.IO;

namespace ArrowNet.Bridge;

/// <summary>
/// An <see cref="ITableFileSystem"/> (engineered-wood) for <b>OneLake</b> (Microsoft Fabric) that does all IO
/// through the <b>Azure DataLake SDK directly</b> (<see cref="DataLakeFileSystemClient"/>), bypassing DuckDB's
/// azure extension entirely. This escapes the duckdb-azure OneLake gaps (the mid-path-wildcard glob bug PR #174,
/// and <c>MoveFile</c>/<c>RemoveDirectory</c> being unimplemented on the DFS endpoint) — most importantly
/// <see cref="RenameAsync"/> here is a <b>true atomic ADLS rename</b> (put-if-absent via <c>IfNoneMatch=*</c>),
/// the Delta-commit primitive, instead of the copy+exclusive-create emulation the host-FS path needs.
///
/// <para>Selected for OneLake roots by <see cref="TableFileSystems.Create"/> when a Fabric credential is present
/// (<see cref="AmbientOneLakeCredential"/>); local / S3 / plain-ADLS roots keep <see cref="DuckDbTableFileSystem"/>
/// (DuckDB's FileSystem + secrets). The credential is the one resolved by <see cref="FabricCredentialResolver"/>
/// from the ATTACH'd azure secret (or the Fabric managed/workspace identity).</para>
///
/// <para>Paths are root-relative (matching <see cref="DuckDbTableFileSystem"/>): <see cref="ListAsync"/> returns
/// paths relative to the table root, and they are re-resolved against it. All operations use the <b>async</b>
/// DataLake APIs — the sync ones use <c>HttpClient.Send</c>, which hangs under the hostfxr-hosted CLR (the same
/// gotcha documented across the Bridge's OneLake IO).</para>
/// </summary>
internal sealed class OneLakeDataLakeFileSystem : ITableFileSystem
{
    private readonly DataLakeFileSystemClient _fs;
    private readonly string _rootUnderFs; // path of the table root within the filesystem (e.g. "lh.Lakehouse/Tables/t")

    public OneLakeDataLakeFileSystem(string rootAbfss, TokenCredential credential)
    {
        var (host, fileSystem, pathUnderFs) = ParseAbfss(rootAbfss);
        _fs = new DataLakeFileSystemClient(new Uri($"https://{host}/{fileSystem}"), credential);
        _rootUnderFs = pathUnderFs.TrimEnd('/');
    }

    /// <summary>Parses <c>abfss://&lt;container&gt;@&lt;host&gt;/&lt;path&gt;</c> into (host, container, pathUnderFs).</summary>
    internal static (string Host, string FileSystem, string PathUnderFs) ParseAbfss(string abfss)
    {
        var s = abfss.Replace('\\', '/').Trim();
        const string scheme = "abfss://";
        if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(scheme.Length);
        }
        int at = s.IndexOf('@');
        string container = at >= 0 ? s.Substring(0, at) : string.Empty;
        string rest = at >= 0 ? s.Substring(at + 1) : s;
        int slash = rest.IndexOf('/');
        string host = slash >= 0 ? rest.Substring(0, slash) : rest;
        string path = slash >= 0 ? rest.Substring(slash + 1) : string.Empty;
        return (host, container, path.TrimEnd('/'));
    }

    /// <summary>A caller path (relative to the root, or an absolute abfss) → path within the filesystem.</summary>
    private string Resolve(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.StartsWith("abfss://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAbfss(p).PathUnderFs; // an absolute path was handed back to us
        }
        p = p.TrimStart('/');
        return _rootUnderFs.Length == 0 ? p : _rootUnderFs + "/" + p;
    }

    private string ToRelative(string underFs)
    {
        var prefix = _rootUnderFs + "/";
        return underFs.StartsWith(prefix, StringComparison.Ordinal) ? underFs.Substring(prefix.Length) : underFs;
    }

    private DataLakeFileClient File(string path) => _fs.GetFileClient(Resolve(path));

    public async IAsyncEnumerable<TableFileInfo> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The Delta log is a flat directory (_delta_log/*.json + *.checkpoint.parquet), so treat the prefix's
        // directory part as the listing target and filter by the full prefix. GetPathsAsync lists a directory's
        // children (non-recursive is enough for the flat log). A missing directory (fresh table) => empty.
        // IMPORTANT: keep the prefix's trailing slash — a directory prefix like "_delta_log/" must list the
        // "_delta_log" directory itself, NOT the table root (trimming the slash would drop a level → the log
        // files, one dir deeper, would be missed, and the read-back sees an empty log = "no metadata action").
        var rel = prefix.Replace('\\', '/').TrimStart('/');
        int slash = rel.LastIndexOf('/');
        string dirRel = slash >= 0 ? rel.Substring(0, slash) : string.Empty; // "_delta_log/" & "_delta_log/00" → "_delta_log"
        string dirUnderFs = _rootUnderFs.Length == 0
            ? dirRel
            : (dirRel.Length == 0 ? _rootUnderFs : _rootUnderFs + "/" + dirRel);
        string fullPrefixUnderFs = Resolve(prefix);

        AsyncPageable<PathItem> pages;
        try
        {
            pages = _fs.GetPathsAsync(dirUnderFs, recursive: false, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            yield break; // directory doesn't exist yet (a table with no _delta_log)
        }

        var enumerator = pages.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                PathItem item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    item = enumerator.Current;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    yield break;
                }
                if (item.IsDirectory == true)
                {
                    continue;
                }
                var name = (item.Name ?? string.Empty).Replace('\\', '/');
                if (!name.StartsWith(fullPrefixUnderFs, StringComparison.Ordinal))
                {
                    continue;
                }
                yield return new TableFileInfo(
                    ToRelative(name),
                    item.ContentLength ?? 0,
                    item.LastModified);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<IRandomAccessFile> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = File(path);
        long length = (await file.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value.ContentLength;
        return new OneLakeRandomAccessFile(file, length);
    }

    public async ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => (await File(path).ExistsAsync(cancellationToken).ConfigureAwait(false)).Value;

    public async ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = File(path);
        using Stream s = await file.OpenReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async ValueTask<ISequentialFile> CreateAsync(
        string path, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var file = File(path);
        // Put-if-absent when overwrite == false: fail if the target already exists (the ISequentialFile contract).
        var options = new DataLakePathCreateOptions();
        if (!overwrite)
        {
            options.Conditions = new DataLakeRequestConditions { IfNoneMatch = ETag.All };
        }
        try
        {
            await file.CreateAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (!overwrite && ex.Status == 409)
        {
            throw new IOException($"OneLakeDataLakeFileSystem: file already exists: {path}");
        }
        return new OneLakeSequentialFile(file);
    }

    public async ValueTask<bool> RenameAsync(
        string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        // A TRUE atomic ADLS rename (the whole reason for this filesystem): put-if-absent via IfNoneMatch=*
        // on the destination, so a conflicting target returns false (the Delta commit-conflict signal
        // engineered-wood maps to DeltaConflictException) instead of overwriting.
        var src = File(sourcePath);
        try
        {
            await src.RenameAsync(
                destinationPath: Resolve(targetPath),
                destinationFileSystem: _fs.Name,
                destinationConditions: new DataLakeRequestConditions { IfNoneMatch = ETag.All },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
        {
            return false; // target exists (409) or precondition failed (412) => commit conflict
        }
    }

    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
        => await File(path).DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteAllBytesAsync(
        string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var file = File(path);
        using var ms = new MemoryStream();
        // MemoryStream over the (possibly array-backed) memory; copy to be safe about lifetime.
        await ms.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        await file.UploadAsync(ms, overwrite: true, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Offset-addressed read handle over a OneLake file (Azure DataLake), using range GETs.</summary>
internal sealed class OneLakeRandomAccessFile : IRandomAccessFile
{
    private readonly DataLakeFileClient _file;
    private readonly long _length;

    public OneLakeRandomAccessFile(DataLakeFileClient file, long length)
    {
        _file = file;
        _length = length;
    }

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
        => new ValueTask<long>(_length);

    public async ValueTask<IMemoryOwner<byte>> ReadAsync(FileRange range, CancellationToken cancellationToken = default)
    {
        var owner = new ExactMemoryOwner((int)range.Length);
        if (range.Length == 0)
        {
            return owner;
        }
        Response<FileDownloadInfo> resp = await _file
            .ReadAsync(new DataLakeFileReadOptions { Range = new HttpRange(range.Offset, range.Length) }, cancellationToken)
            .ConfigureAwait(false);
        using Stream content = resp.Value.Content;
        int total = 0;
        int len = owner.Array.Length;
        while (total < len)
        {
            int n = await content.ReadAsync(owner.Array.AsMemory(total, len - total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException($"OneLakeDataLakeFileSystem: short read ({total}/{len}) at offset {range.Offset}");
            }
            total += n;
        }
        return owner;
    }

    public async ValueTask<IReadOnlyList<IMemoryOwner<byte>>> ReadRangesAsync(
        IReadOnlyList<FileRange> ranges, CancellationToken cancellationToken = default)
    {
        var result = new IMemoryOwner<byte>[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            result[i] = await ReadAsync(ranges[i], cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Sequential (append-only) write handle over a OneLake file: buffers appends and flushes on dispose
/// (Azure DFS needs an explicit Flush to commit appended data at the final length).</summary>
internal sealed class OneLakeSequentialFile : ISequentialFile
{
    private readonly DataLakeFileClient _file;
    private long _position;
    private bool _closed;

    public OneLakeSequentialFile(DataLakeFileClient file) => _file = file;

    public long Position => _position;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0)
        {
            return;
        }
        using var ms = new MemoryStream();
        await ms.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        await _file.AppendAsync(ms, _position, cancellationToken: cancellationToken).ConfigureAwait(false);
        _position += data.Length;
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _file.FlushAsync(_position, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        // Commit the appended bytes at the final length.
        await _file.FlushAsync(_position).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
