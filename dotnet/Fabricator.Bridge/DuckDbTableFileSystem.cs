using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EngineeredWood.IO;

namespace Fabricator.Bridge;

/// <summary>
/// A read-only <see cref="ITableFileSystem"/> (engineered-wood) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via the host reverse-callbacks (<see cref="HostFs"/>). So a managed lakehouse reader
/// (Delta, …) reads <c>az://</c>/<c>s3://</c>/<c>https://</c>/local paths with DuckDB's secrets + backends —
/// one auth config shared with native DuckDB reads, no cloud SDK duplication.
///
/// Paths are resolved relative to the table root (matching <c>LocalTableFileSystem</c>): <see cref="ListAsync"/>
/// returns paths relative to the root, and they are passed back to <see cref="OpenReadAsync"/> /
/// <see cref="ReadAllBytesAsync"/> which re-resolve against the root. The <paramref name="opener"/> (the calling
/// operator's ClientContext) carries secret resolution and is valid for the duration of the synchronous call
/// that drives the read (a table-function execution). Write operations are not supported.
/// </summary>
internal sealed unsafe class DuckDbTableFileSystem : ITableFileSystem
{
    private readonly nint _capturedOpener;
    private readonly string _root; // normalized (forward slashes), no trailing slash

    public DuckDbTableFileSystem(nint opener, string root)
    {
        _capturedOpener = opener;
        _root = Normalize(root).TrimEnd('/');
    }

    /// <summary>
    /// The host-FS opener to use for THIS call, preferring the one currently in scope over the one captured
    /// at construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The opener is a <c>ClientContext*</c> that the host hands us per ABI call and that is only valid for
    /// the duration of that call. Capturing it — which is what this class did — is therefore the reason a
    /// <c>DeltaTable</c> cannot be held open ACROSS calls: the second call would drive IO through a dangling
    /// pointer. That is a use-after-free rather than a staleness bug, and neither Windows nor glibc would
    /// necessarily fault on it (the same asymmetry that let the late <c>ArrowProducer</c> release hide
    /// everywhere except macOS).
    /// </para>
    /// <para>
    /// Reading <see cref="AmbientOpener"/> first fixes that, because the host sets it on every crossing.
    /// The captured value is kept as a FALLBACK rather than deleted: the ambient is an
    /// <see cref="System.Threading.AsyncLocal{T}"/>, so it flows across <c>await</c> and pool-thread hops
    /// but would read 0 in any context where the execution context did not flow, and there the captured
    /// pointer is still the correct one because no object outlives its call today. So this is
    /// behaviour-preserving now (within one call the two values are identical — every construction site
    /// passes <c>Opener()</c>, which just returns the ambient) and becomes load-bearing the moment
    /// something is cached.
    /// </para>
    /// </remarks>
    private nint Opener
    {
        get
        {
            var current = AmbientOpener.Current;
            return current != 0 ? current : _capturedOpener;
        }
    }

    private static string Normalize(string p) => p.Replace('\\', '/');

    private string Resolve(string path)
    {
        var p = Normalize(path);
        if (p.StartsWith(_root, StringComparison.Ordinal))
        {
            return p;
        }
        return _root + "/" + p.TrimStart('/');
    }

    private string ToRelative(string absolute)
    {
        var p = Normalize(absolute);
        var prefix = _root + "/";
        return p.StartsWith(prefix, StringComparison.Ordinal) ? p.Substring(prefix.Length) : p;
    }

    public async IAsyncEnumerable<TableFileInfo> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // DuckDB glob: <root>/<prefix>* (the Delta log is flat — _delta_log/*.json + checkpoint parquet).
        var pattern = _root + "/" + Normalize(prefix) + "*";
        var json = HostFs.Glob(Opener, pattern);
        foreach (var entry in ParseGlob(json))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The host glob no longer opens each match for its size (an open can DOWNLOAD the blob on a
            // fuse mount — the old open-per-commit-json made a lakehouse ATTACH take minutes); object-store
            // listings carry the size, local files get it here via a cheap metadata stat. Unknown stays 0 —
            // the only size consumer is vacuum's bytes-to-delete metric.
            long size = entry.Size;
            if (size < 0)
            {
                try
                {
                    var fi = new System.IO.FileInfo(entry.Path);
                    size = fi.Exists ? fi.Length : 0;
                }
                catch
                {
                    size = 0;
                }
            }
            yield return new TableFileInfo(ToRelative(entry.Path), size, DateTimeOffset.UnixEpoch);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<IRandomAccessFile> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IRandomAccessFile>(new DuckDbRandomAccessFile(Opener, Resolve(path)));
    }

    public ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // NOT a glob probe: DuckDB's S3 (httpfs) glob ECHOES a wildcard-free path back without checking
        // the object store, so a literal glob reported every path as existing — which made engineered-wood's
        // commit-0 existence pre-check throw a phantom DeltaConflictException on S3/MinIO. Probing by
        // opening for read (a HEAD on object stores) is existence-accurate on every backend.
        try
        {
            nint file = HostFs.OpenRead(Opener, Resolve(path));
            HostFs.Close(file);
            return new ValueTask<bool>(true);
        }
        catch
        {
            return new ValueTask<bool>(false);
        }
    }

    public ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        // Bounded reopen-retry for MUTABLE small files (this method's callers: _last_checkpoint +
        // commit JSONs; only _last_checkpoint is overwritten in place). On an object store, DuckDB's
        // httpfs validates the etag recorded at open against the range read — a CONCURRENT writer's
        // checkpoint changes it mid-read and the read throws ("ETag on reading file ... has changed").
        // Reopening reads a consistent NEWER copy, which is always valid for a checkpoint pointer.
        // Data files are immutable (a new commit writes a new file), so they never hit this.
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                nint file = HostFs.OpenRead(Opener, Resolve(path));
                try
                {
                    long size = HostFs.Size(file);
                    var buffer = new byte[size];
                    if (size > 0)
                    {
                        fixed (byte* bp = buffer)
                        {
                            HostFs.Read(file, bp, size, 0);
                        }
                    }
                    return new ValueTask<byte[]>(buffer);
                }
                finally
                {
                    HostFs.Close(file);
                }
            }
            catch (Exception ex) when (attempt < 4
                                       && ex.Message.Contains("ETag on reading file",
                                                              StringComparison.OrdinalIgnoreCase))
            {
                // concurrent in-place overwrite — reopen for a consistent newer copy
            }
        }
    }

    // ---- write surface: over the host fs_* write callbacks (Delta write-back) ----

    /// <summary>The parent directory of a resolved path (for materializing e.g. <c>_delta_log/</c> on a local FS;
    /// a no-op marker on object stores). Empty if there is no parent.</summary>
    private string ParentDir(string resolved)
    {
        int slash = resolved.LastIndexOf('/');
        return slash <= 0 ? string.Empty : resolved.Substring(0, slash);
    }

    private void EnsureParentDir(string resolved)
    {
        // On object stores directories are implicit (the write creates the path); on a local FS they are not.
        // fs_create_dir is recursive (mkdir -p) on the host side, so one call on the immediate parent
        // materializes the whole chain (the table root + `_delta_log/`). Idempotent.
        var parent = ParentDir(resolved);
        if (parent.Length > 0)
        {
            HostFs.CreateDir(Opener, parent);
        }
    }

    public ValueTask<ISequentialFile> CreateAsync(
        string path, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        EnsureParentDir(resolved);
        nint file;
        if (overwrite)
        {
            file = HostFs.OpenWrite(Opener, resolved);
        }
        else if (!HostFs.TryOpenWriteExclusive(Opener, resolved, out file))
        {
            // Contract: CreateAsync fails when the file exists and overwrite is false.
            throw new System.IO.IOException($"DuckDbTableFileSystem: file already exists: {path}");
        }
        return new ValueTask<ISequentialFile>(new DuckDbSequentialFile(file));
    }

    public ValueTask<bool> RenameAsync(
        string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        // DuckDB's FileSystem has no atomic-no-overwrite MoveFile (it overwrites on local, and is NOT
        // implemented on Azure DFS). So emulate the put-if-absent rename that Delta's commit relies on:
        // create the TARGET with EXCLUSIVE_CREATE (the put-if-absent primitive, honored on OneLake/ADLS + POSIX)
        // and copy the source bytes in; if the target already exists, return false (the commit-conflict signal
        // engineered-wood maps to DeltaConflictException) WITHOUT touching the source (the caller deletes it).
        cancellationToken.ThrowIfCancellationRequested();
        var src = Resolve(sourcePath);
        var dst = Resolve(targetPath);
        nint bytesFile = HostFs.OpenRead(Opener, src);
        byte[] bytes;
        try
        {
            long size = HostFs.Size(bytesFile);
            bytes = new byte[size];
            if (size > 0)
            {
                fixed (byte* bp = bytes)
                {
                    HostFs.Read(bytesFile, bp, size, 0);
                }
            }
        }
        finally
        {
            HostFs.Close(bytesFile);
        }

        EnsureParentDir(dst);
        if (!HostFs.TryOpenWriteExclusive(Opener, dst, out nint target))
        {
            return new ValueTask<bool>(false); // target exists => conflict; leave source for the caller to delete
        }
        try
        {
            if (bytes.Length > 0)
            {
                fixed (byte* bp = bytes)
                {
                    HostFs.WriteBytes(target, bp, bytes.Length);
                }
            }
        }
        finally
        {
            HostFs.CloseWrite(target);
        }
        HostFs.Remove(Opener, src); // the source temp is consumed by the (emulated) rename
        return new ValueTask<bool>(true);
    }

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostFs.Remove(Opener, Resolve(path));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAllBytesAsync(
        string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        EnsureParentDir(resolved);
        nint file = HostFs.OpenWrite(Opener, resolved);
        try
        {
            if (data.Length > 0)
            {
                using var pin = data.Pin();
                HostFs.WriteBytes(file, pin.Pointer, data.Length);
            }
        }
        finally
        {
            HostFs.CloseWrite(file);
        }
        return ValueTask.CompletedTask;
    }

    private readonly record struct GlobEntry(string Path, long Size);

    private static List<GlobEntry> ParseGlob(string json)
    {
        var result = new List<GlobEntry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var path = el.GetProperty("path").GetString() ?? string.Empty;
            long size = el.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
            result.Add(new GlobEntry(path, size));
        }
        return result;
    }
}

/// <summary>
/// Offset-addressed read handle over a single host <c>FileSystem</c> file. Opened eagerly; all reads are
/// synchronous host calls wrapped in completed <see cref="ValueTask"/>s (no real async — the hostfxr CLR has
/// no SynchronizationContext, so sync-over-async upstream cannot deadlock).
/// </summary>
internal sealed unsafe class DuckDbRandomAccessFile : IRandomAccessFile
{
    private readonly nint _file;
    private long _length = -1;
    private bool _closed;

    public DuckDbRandomAccessFile(nint opener, string resolvedPath)
    {
        _file = HostFs.OpenRead(opener, resolvedPath);
    }

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
    {
        if (_length < 0)
        {
            _length = HostFs.Size(_file);
        }
        return new ValueTask<long>(_length);
    }

    public ValueTask<IMemoryOwner<byte>> ReadAsync(FileRange range, CancellationToken cancellationToken = default)
    {
        var owner = new ExactMemoryOwner((int)range.Length);
        if (range.Length > 0)
        {
            fixed (byte* bp = owner.Array)
            {
                HostFs.Read(_file, bp, range.Length, range.Offset);
            }
        }
        return new ValueTask<IMemoryOwner<byte>>(owner);
    }

    public ValueTask<IReadOnlyList<IMemoryOwner<byte>>> ReadRangesAsync(
        IReadOnlyList<FileRange> ranges, CancellationToken cancellationToken = default)
    {
        var result = new IMemoryOwner<byte>[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            // Reads complete synchronously (host calls block) — no real await needed.
            result[i] = ReadAsync(ranges[i], cancellationToken).GetAwaiter().GetResult();
        }
        return new ValueTask<IReadOnlyList<IMemoryOwner<byte>>>(result);
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _closed = true;
            HostFs.Close(_file);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Sequential (append-only) write handle over a host <c>FileSystem</c> file (opened via fs_open_write). Writes
/// are synchronous host calls in completed <see cref="ValueTask"/>s; the handle is flushed + closed on dispose.
/// Azure DFS only accepts sequential writes, which is exactly this contract.
/// </summary>
internal sealed unsafe class DuckDbSequentialFile : ISequentialFile
{
    private readonly nint _file;
    private long _position;
    private bool _closed;

    public DuckDbSequentialFile(nint file) => _file = file;

    public long Position => _position;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > 0)
        {
            using var pin = data.Pin();
            HostFs.WriteBytes(_file, pin.Pointer, data.Length);
            _position += data.Length;
        }
        return ValueTask.CompletedTask;
    }

    // The host flushes on close (fs_close_write); there is no separate flush callback. Sequential writes are
    // already handed straight to the FileHandle, so this is a no-op until Dispose.
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public void Dispose()
    {
        if (!_closed)
        {
            _closed = true;
            HostFs.CloseWrite(_file);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="IMemoryOwner{T}"/> backed by an exact-sized array (so <c>Memory.Length</c> equals the
/// requested range length — the parquet reader relies on it).</summary>
internal sealed class ExactMemoryOwner : IMemoryOwner<byte>
{
    public byte[] Array { get; }
    public ExactMemoryOwner(int size) => Array = size == 0 ? System.Array.Empty<byte>() : new byte[size];
    public Memory<byte> Memory => Array;
    public void Dispose() { }
}
