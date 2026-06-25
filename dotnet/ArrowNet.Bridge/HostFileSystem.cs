using System;
using System.Runtime.InteropServices;

namespace ArrowNet.Bridge;

/// <summary>
/// Caches + wraps the host-services callbacks (the reverse direction of the vtable — functions the C++ host
/// provides so the managed side can reach DuckDB's <c>FileSystem</c>, doing secret-backed remote IO via
/// DuckDB with one shared auth config). The host fills <see cref="ArrowNetHostServices"/> and passes it to
/// <c>Bootstrap.Initialize</c>. SPIKE foundation for a future C# lakehouse reader.
/// </summary>
internal static unsafe class HostFs
{
    private static ArrowNetHostServices _h;
    private static bool _set;

    public static void Set(ArrowNetHostServices h)
    {
        _h = h;
        _set = true;
    }

    /// <summary>True once the host registered usable filesystem callbacks.</summary>
    public static bool Available => _set && _h.FsOpenRead != null;

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
