// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Runtime.InteropServices;

namespace Fabricator.Installer;

/// <summary>
/// Answers "which file was this native library loaded from?" — the prerequisite for reading a payload
/// appended to our own artifact.
/// </summary>
/// <remarks>
/// There is no BCL API for this: <see cref="Environment.ProcessPath"/> is the host (duckdb, python,
/// dbt…) and a NativeAOT library has no <c>Assembly.Location</c>. So this is the one genuinely
/// platform-specific code in the installer — two P/Invokes, both pure C#, no build-system divergence.
/// The address of a method in this image is used as the "somewhere inside me" probe.
/// </remarks>
internal static unsafe class ModulePath
{
    private const uint GetModuleHandleFromAddress = 0x00000004; // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
    private const uint GetModuleHandleUnchangedRefcount = 0x00000002; // ..._UNCHANGED_REFCOUNT

    /// <summary>The path of the library containing this code, or null if it cannot be determined.</summary>
    internal static string? OfThisLibrary()
    {
        try
        {
            delegate*<void> probe = &AddressProbe;

            if (OperatingSystem.IsWindows())
            {
                if (!GetModuleHandleExW(
                        GetModuleHandleFromAddress | GetModuleHandleUnchangedRefcount,
                        (nint)probe,
                        out nint module))
                {
                    return null;
                }

                char* buffer = stackalloc char[4096];
                uint written = GetModuleFileNameW(module, buffer, 4096);
                return written == 0 ? null : new string(buffer, 0, (int)written);
            }

            DlInfo info = default;
            return TryDlAddr((nint)probe, ref info) && info.FileName != 0
                ? Marshal.PtrToStringUTF8(info.FileName)
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Exists purely to have an address inside this image.</summary>
    private static void AddressProbe()
    {
    }

    /// <summary><c>dladdr</c> lives in libc on modern glibc and in libdl on older ones (and on musl).</summary>
    private static bool TryDlAddr(nint address, ref DlInfo info)
    {
        try
        {
            return DlAddrLibc(address, ref info) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        try
        {
            return DlAddrLibdl(address, ref info) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public nint FileName;
        public nint BaseAddress;
        public nint SymbolName;
        public nint SymbolAddress;
    }

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(nint module, char* buffer, uint size);

    [DllImport("libc", EntryPoint = "dladdr")]
    private static extern int DlAddrLibc(nint address, ref DlInfo info);

    [DllImport("libdl", EntryPoint = "dladdr")]
    private static extern int DlAddrLibdl(nint address, ref DlInfo info);
}
