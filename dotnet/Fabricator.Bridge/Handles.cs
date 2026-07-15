using System.Runtime.InteropServices;

namespace Fabricator.Bridge;

/// <summary>
/// Maps opaque native handles (<c>void*</c>) to managed backend instances using
/// <see cref="GCHandle"/>. The native side holds the <see cref="GCHandle"/> value
/// (as a pointer) for the lifetime of the catalog and releases it via
/// <c>close_catalog</c>.
/// </summary>
internal static class Handles
{
    public static nint Alloc(object target)
        => GCHandle.ToIntPtr(GCHandle.Alloc(target));

    public static T? Resolve<T>(nint handle) where T : class
    {
        if (handle == 0)
        {
            return null;
        }
        var gch = GCHandle.FromIntPtr(handle);
        return gch.Target as T;
    }

    public static void Free(nint handle)
    {
        if (handle == 0)
        {
            return;
        }
        var gch = GCHandle.FromIntPtr(handle);
        if (gch.IsAllocated)
        {
            (gch.Target as IDisposable)?.Dispose();
            gch.Free();
        }
    }
}
