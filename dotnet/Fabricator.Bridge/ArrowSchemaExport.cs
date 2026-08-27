// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.C;

namespace Fabricator.Bridge;

/// <summary>
/// Exports an <see cref="Schema"/> across the Arrow C data interface, INCLUDING the zero-field case that
/// Apache.Arrow cannot handle itself.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>CArrowSchemaExporter.ExportSchema</c> throws
/// <c>ArgumentNullException(Parameter 'fields')</c> for a schema with NO fields (Apache.Arrow 23.0.0; both
/// <c>ExportSchema(new Schema(empty))</c> and <c>ExportType(new StructType(empty))</c> fail, verified with a
/// one-field positive control). A zero-field schema is exactly how "this function takes no arguments" is
/// expressed, so without this helper a zero-argument function is IMPOSSIBLE: the host's
/// <c>GetOrCreateScalarFunction</c> catches any schema-fetch failure and silently erases the function as
/// "stale", so the symptom is a function that never appears — no error anywhere except a Debug-level WARN.
/// That WARN had already been observed and written off as benign.</para>
///
/// <para><b>What it does.</b> Delegates to Apache.Arrow whenever there is at least one field, and hand-builds
/// the empty struct schema (<c>format="+s"</c>, <c>n_children=0</c>) otherwise. The struct is filled through a
/// layout mirror because <c>CArrowSchema.release</c> is internal to Apache.Arrow — the same C-struct mirroring
/// this bridge already does in <c>Abi.cs</c>. Ownership follows the C data interface: the consumer calls
/// <c>release</c>, which frees the format string and NULLs itself to mark the schema released.</para>
/// </remarks>
internal static unsafe class ArrowSchemaExport
{
    /// <summary>Sequential mirror of <c>ArrowSchema</c> — needed only to reach the internal release slot.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ArrowSchemaLayout
    {
        public byte* Format;
        public byte* Name;
        public byte* Metadata;
        public long Flags;
        public long NChildren;
        public void** Children;
        public void* Dictionary;
        public delegate* unmanaged[Cdecl]<ArrowSchemaLayout*, void> Release;
        public void* PrivateData;
    }

    /// <summary>
    /// Exports <paramref name="schema"/> into <paramref name="target"/>, handling the zero-field case.
    /// </summary>
    internal static void Export(Schema schema, CArrowSchema* target)
    {
        if (schema.FieldsList.Count > 0)
        {
            CArrowSchemaExporter.ExportSchema(schema, target);
            return;
        }
        ExportEmptyStruct((ArrowSchemaLayout*)target);
    }

    private static void ExportEmptyStruct(ArrowSchemaLayout* target)
    {
        // "+s" = struct, the format a Schema always exports as. Allocated natively because the consumer reads it
        // after this method returns; freed by the release callback below, which is the only owner.
        byte* format = (byte*)Marshal.AllocHGlobal(3);
        format[0] = (byte)'+';
        format[1] = (byte)'s';
        format[2] = 0;

        target->Format = format;
        target->Name = null;
        target->Metadata = null;
        target->Flags = 0;
        target->NChildren = 0;
        target->Children = null;
        target->Dictionary = null;
        target->Release = &ReleaseEmptyStruct;
        // The release callback frees exactly what was allocated here; carrying the pointer in private_data keeps
        // that self-contained (rather than re-deriving it from Format, which the consumer may have read past).
        target->PrivateData = format;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ReleaseEmptyStruct(ArrowSchemaLayout* schema)
    {
        if (schema is null)
        {
            return;
        }
        if (schema->PrivateData is not null)
        {
            Marshal.FreeHGlobal((IntPtr)schema->PrivateData);
            schema->PrivateData = null;
        }
        schema->Format = null;
        // The C data interface marks a schema released by NULLing its release callback; a consumer that
        // double-releases (DuckDB's readers are defensive about this) then does nothing.
        schema->Release = null;
    }
}
