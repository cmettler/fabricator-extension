using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.C;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Caches + wraps the host-services callbacks (the reverse direction of the vtable — functions the C++ host
/// provides so the managed side can reach DuckDB's <c>FileSystem</c>, doing secret-backed remote IO via
/// DuckDB with one shared auth config). The host fills <see cref="FabricatorHostServices"/> and passes it to
/// <c>Bootstrap.Initialize</c>. SPIKE foundation for a future C# lakehouse reader.
/// </summary>
internal static unsafe class HostFs
{
    private static FabricatorHostServices _h;
    private static bool _set;

    public static void Set(FabricatorHostServices h)
    {
        _h = h;
        _set = true;
    }

    /// <summary>True once the host registered usable filesystem callbacks.</summary>
    public static bool Available => _set && _h.FsOpenRead != null;

    /// <summary>True once the host registered the host_log callback (DuckDB internal logging forward).</summary>
    public static bool CanLog => _set && _h.HostLog != null;

    /// <summary>True once the host registered the is_interrupted callback (ClientContext interrupt flag).</summary>
    public static bool CanInterrupt => _set && _h.IsInterrupted != null;

    /// <summary>Reads the calling operator's interrupt flag (Ctrl+C / query timeout) via the host
    /// <c>is_interrupted</c> callback. Returns false when unavailable or the opener is null. Polled by
    /// <see cref="InterruptScope"/> to cancel long-running C# I/O. Never throws.</summary>
    public static bool IsInterrupted(nint opener)
    {
        if (!CanInterrupt || opener == 0)
        {
            return false;
        }
        try { return _h.IsInterrupted(opener) != 0; }
        catch { return false; }
    }

    /// <summary>Forwards a log event into DuckDB's internal logging (duckdb_logs). Best-effort — never throws.</summary>
    public static void Log(int level, string category, string message)
    {
        if (!CanLog)
        {
            return;
        }
        var catPtr = Marshal.StringToCoTaskMemUTF8(category);
        var msgPtr = Marshal.StringToCoTaskMemUTF8(message);
        try { _h.HostLog(level, (byte*)catPtr, (byte*)msgPtr); }
        catch { /* logging must never fault the extension */ }
        finally
        {
            Marshal.FreeCoTaskMem(catPtr);
            Marshal.FreeCoTaskMem(msgPtr);
        }
    }

    /// <summary>Opens a file for reading via DuckDB's FileSystem; <paramref name="opener"/> resolves secrets.</summary>
    public static nint OpenRead(nint opener, string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            byte* err = null;
            nint file;
            int rc = _h.FsOpenRead(opener, (byte*)pathPtr, &file, &err);
            if (rc != 0)
            {
                throw HostError("fs_open_read", err);
            }
            return file;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    public static long Size(nint file)
    {
        byte* err = null;
        long size;
        int rc = _h.FsSize(file, &size, &err);
        if (rc != 0)
        {
            throw HostError("fs_size", err);
        }
        return size;
    }

    public static void Read(nint file, void* buffer, long nrBytes, long location)
    {
        byte* err = null;
        int rc = _h.FsRead(file, buffer, nrBytes, location, &err);
        if (rc != 0)
        {
            throw HostError("fs_read", err);
        }
    }

    public static void Close(nint file)
    {
        if (file != 0 && _h.FsClose != null)
        {
            _h.FsClose(file);
        }
    }

    /// <summary>True once the host registered the glob callback (needed for directory listing).</summary>
    public static bool CanGlob => _set && _h.FsGlob != null;

    /// <summary>Globs <paramref name="pattern"/> (DuckDB glob) via DuckDB's FileSystem; returns the raw JSON
    /// array of <c>{"path":..,"size":..}</c> the host produced (caller parses).</summary>
    public static string Glob(nint opener, string pattern)
    {
        var patPtr = Marshal.StringToCoTaskMemUTF8(pattern);
        try
        {
            byte* err = null;
            byte* outJson = null;
            int rc = _h.FsGlob(opener, (byte*)patPtr, &outJson, &err);
            if (rc != 0)
            {
                throw HostError("fs_glob", err);
            }
            var json = Marshal.PtrToStringUTF8((nint)outJson) ?? "[]";
            if (outJson != null && _h.FreeStr != null)
            {
                _h.FreeStr(outJson);
            }
            return json;
        }
        finally
        {
            Marshal.FreeCoTaskMem(patPtr);
        }
    }

    /// <summary>True once the host registered the write callbacks (the Delta write-back foundation).</summary>
    public static bool CanWrite => _set && _h.FsOpenWrite != null;

    /// <summary>Opens <paramref name="path"/> for sequential writing (create-or-truncate). Throws on error.</summary>
    public static nint OpenWrite(nint opener, string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            byte* err = null;
            nint file;
            int rc = _h.FsOpenWrite(opener, (byte*)pathPtr, 0, &file, &err);
            if (rc != FabricatorStatus.Ok)
            {
                throw HostError("fs_open_write", err);
            }
            return file;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing with EXCLUSIVE_CREATE (put-if-absent). Returns true + a write
    /// handle if it created the file; false (no handle) if the file already existed — the commit-conflict signal;
    /// throws on any other error.
    /// </summary>
    public static bool TryOpenWriteExclusive(nint opener, string path, out nint file)
    {
        file = 0;
        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            byte* err = null;
            nint f;
            int rc = _h.FsOpenWrite(opener, (byte*)pathPtr, 1, &f, &err);
            if (rc == FabricatorStatus.AlreadyExists)
            {
                return false;
            }
            if (rc != FabricatorStatus.Ok)
            {
                throw HostError("fs_open_write(exclusive)", err);
            }
            file = f;
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>Appends <paramref name="nrBytes"/> from <paramref name="buffer"/> to a write handle (sequential).</summary>
    public static void WriteBytes(nint file, void* buffer, long nrBytes)
    {
        byte* err = null;
        int rc = _h.FsWrite(file, buffer, nrBytes, &err);
        if (rc != FabricatorStatus.Ok)
        {
            throw HostError("fs_write", err);
        }
    }

    /// <summary>Flushes + closes a write handle (surfaces flush errors). Frees the handle.</summary>
    public static void CloseWrite(nint file)
    {
        byte* err = null;
        int rc = _h.FsCloseWrite(file, &err);
        if (rc != FabricatorStatus.Ok)
        {
            throw HostError("fs_close_write", err);
        }
    }

    /// <summary>Removes <paramref name="path"/> (no error if missing).</summary>
    public static void Remove(nint opener, string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            byte* err = null;
            int rc = _h.FsRemove(opener, (byte*)pathPtr, &err);
            if (rc != FabricatorStatus.Ok)
            {
                throw HostError("fs_remove", err);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>Creates directory <paramref name="path"/> (idempotent; implicit on object stores).</summary>
    public static void CreateDir(nint opener, string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            byte* err = null;
            int rc = _h.FsCreateDir(opener, (byte*)pathPtr, &err);
            if (rc != FabricatorStatus.Ok)
            {
                throw HostError("fs_create_dir", err);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>True once the host registered the recursive directory-delete callback (ABI v49+).</summary>
    public static bool CanRemoveDir => _set && _h.FsRemoveDir != null;

    /// <summary>Removes directory <paramref name="path"/> RECURSIVELY (all files + subdirs; no error if missing).</summary>
    public static void RemoveDir(nint opener, string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            byte* err = null;
            int rc = _h.FsRemoveDir(opener, (byte*)pathPtr, &err);
            if (rc != FabricatorStatus.Ok)
            {
                throw HostError("fs_remove_dir", err);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>True once the host registered the directory move/rename callback (ABI v50+).</summary>
    public static bool CanMoveDir => _set && _h.FsMoveDir != null;

    /// <summary>Renames/moves directory <paramref name="src"/> to <paramref name="dest"/>
    /// (FileSystem::MoveFile — atomic on a local filesystem; object stores throw "not implemented").</summary>
    public static void MoveDir(nint opener, string src, string dest)
    {
        var srcPtr = Marshal.StringToCoTaskMemUTF8(src);
        var destPtr = Marshal.StringToCoTaskMemUTF8(dest);
        try
        {
            byte* err = null;
            int rc = _h.FsMoveDir(opener, (byte*)srcPtr, (byte*)destPtr, &err);
            if (rc != FabricatorStatus.Ok)
            {
                throw HostError("fs_move_dir", err);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(srcPtr);
            Marshal.FreeCoTaskMem(destPtr);
        }
    }

    /// <summary>True once the host registered the host_query callback.</summary>
    public static bool CanQuery => _set && _h.HostQuery != null;

    /// <summary>
    /// Runs <paramref name="sql"/> on a FRESH host DuckDB connection (its own transaction) and returns the
    /// result as an Arrow stream the caller owns (dispose to release the connection + result). Lets a managed
    /// component reuse the host engine — functions, readers, the catalog — over Arrow. See docs/host-query.md.
    /// </summary>
    public static IArrowArrayStream Query(string sql,
                                          RecordBatch? parameters = null,
                                          IReadOnlyList<(string Name, IArrowArrayStream Stream)>? inputs = null)
    {
        if (!CanQuery)
        {
            throw new InvalidOperationException("host_query is unavailable (the host did not register it)");
        }
        int n = inputs?.Count ?? 0;
        var sqlPtr = Marshal.StringToCoTaskMemUTF8(sql);
        var cstream = CArrowArrayStream.Create();
        CArrowArrayStream* paramStream = null;
        byte** namePtrs = null;
        CArrowArrayStream** streamPtrs = null;
        try
        {
            if (parameters != null)
            {
                paramStream = CArrowArrayStream.Create();
                // a 1-row stream the host reads + binds positionally (consumed + released by the host).
                CArrowArrayStreamExporter.ExportArrayStream(
                    new InMemoryArrayStream(parameters.Schema, new[] { parameters }), paramStream);
            }

            FabricatorHostInputs hi = default;
            FabricatorHostInputs* hiPtr = null;
            if (n > 0)
            {
                namePtrs = (byte**)Marshal.AllocHGlobal(sizeof(nint) * n);
                streamPtrs = (CArrowArrayStream**)Marshal.AllocHGlobal(sizeof(nint) * n);
                for (int i = 0; i < n; i++)
                {
                    namePtrs[i] = (byte*)Marshal.StringToCoTaskMemUTF8(inputs![i].Name);
                    var s = CArrowArrayStream.Create();
                    CArrowArrayStreamExporter.ExportArrayStream(inputs[i].Stream, s); // host consumes + releases it
                    streamPtrs[i] = s;
                }
                hi.Count = n;
                hi.Names = namePtrs;
                hi.Streams = streamPtrs;
                hiPtr = &hi;
            }

            byte* err = null;
            int rc = _h.HostQuery((byte*)sqlPtr, paramStream, hiPtr, cstream, &err);
            if (rc != 0)
            {
                throw HostError("host_query", err);
            }
            var imported = CArrowArrayStreamImporter.ImportArrayStream(cstream);
            cstream = null; // ownership transferred to the imported stream (it frees the alloc on dispose)
            return imported;
        }
        finally
        {
            if (cstream != null)
            {
                CArrowArrayStream.Free(cstream);
            }
            if (paramStream != null)
            {
                // The host's ArrowStreamReader took the stream by value and released its copy (disposing the
                // exporter), so the content is already released — free only OUR allocation (calling release
                // again via CArrowArrayStream.Free would double-free the exporter state -> NRE).
                Marshal.FreeHGlobal((nint)paramStream);
            }
            // Free the marshaling arrays + the input-stream allocations. The stream CONTENT was consumed +
            // released by DuckDB during the (materializing) query; we free only our allocations.
            if (namePtrs != null)
            {
                for (int i = 0; i < n; i++)
                {
                    Marshal.FreeCoTaskMem((nint)namePtrs[i]);
                }
                Marshal.FreeHGlobal((nint)namePtrs);
            }
            if (streamPtrs != null)
            {
                for (int i = 0; i < n; i++)
                {
                    CArrowArrayStream.Free(streamPtrs[i]);
                }
                Marshal.FreeHGlobal((nint)streamPtrs);
            }
            Marshal.FreeCoTaskMem(sqlPtr);
        }
    }

    private static Exception HostError(string op, byte* err)
    {
        var msg = Marshal.PtrToStringUTF8((nint)err) ?? string.Empty;
        if (err != null && _h.FreeStr != null)
        {
            _h.FreeStr(err);
        }
        return new System.IO.IOException($"host {op} failed: {msg}");
    }
}

/// <summary>
/// SPIKE: opens a file via the host FileSystem callbacks and reports its head + tail bytes and size — proving
/// a managed component can do (secret-backed) remote reads through DuckDB's IO. For a Parquet file the head
/// and tail are both <c>PAR1</c>.
/// </summary>
internal static unsafe class HostFileSystemSpike
{
    public static string Run(nint opener, string path)
    {
        if (!HostFs.Available)
        {
            return "host filesystem services unavailable";
        }
        nint file = HostFs.OpenRead(opener, path);
        try
        {
            long size = HostFs.Size(file);
            int headN = (int)Math.Min(4, size);
            int tailN = (int)Math.Min(4, size);
            Span<byte> head = stackalloc byte[4];
            Span<byte> tail = stackalloc byte[4];
            fixed (byte* hp = head)
            {
                HostFs.Read(file, hp, headN, 0);
            }
            fixed (byte* tp = tail)
            {
                HostFs.Read(file, tp, tailN, size - tailN);
            }
            return $"ok size={size} head='{Ascii(head[..headN])}' tail='{Ascii(tail[..tailN])}'";
        }
        finally
        {
            HostFs.Close(file);
        }
    }

    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            sb.Append(b >= 32 && b < 127 ? (char)b : '.');
        }
        return sb.ToString();
    }
}
