// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fluid;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The Fluid/Liquid template engine, packaged as a plugin. It exposes no catalog (ATTACH throws) and exists
/// purely to contribute one connection-free GLOBAL scalar, <c>fabricator_render</c>.
/// <para><b>Why it is a plugin rather than part of a backend.</b> It lived in <c>Fabricator.SqlServer</c>,
/// which put a template engine — and the <c>Fluid.Core</c> package — inside the SQL Server backend, where it
/// has nothing to do with SQL Server and rode into every shipped payload whether or not anyone rendered a
/// template. It is also the one dependency the AOT SKU has to reason about (docs/aot-bridge.md: Parlot's
/// compiled mode uses <c>System.Linq.Expressions</c>), so moving it out of the core removes that conditional
/// entirely.</para>
/// <para><b>⚠ CONSEQUENCE, and it is user-visible: <c>fabricator_render</c> is now OPT-IN.</b> A global
/// function can only be registered during <c>Extension::Load()</c>, so this plugin must be present in a
/// plugin root at load time — installing it mid-session does not surface the function until the next session
/// (docs/plugin-system.md, "the reload split").</para>
/// <para><b>⚠ It is also the first in-tree plugin with a PRIVATE PACKAGE DEPENDENCY.</b>
/// <c>Fabricator.SamplePlugin</c> is pure IL, so until this existed nothing exercised
/// <c>BackendRegistry.InstallPluginResolver</c> actually loading a plugin's own NuGet closure (here
/// <c>Fluid.Core</c> and its transitive <c>Parlot</c>) out of the plugin folder.</para>
/// </summary>
public sealed class FluidPluginBackend : IBackend
{
    public string Name => "fluid";

    public IEnumerable<IScalarFunction> GlobalScalarFunctions =>
        new IScalarFunction[] { new FluidRenderFunction() };

    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) =>
        throw new NotSupportedException("fluid: a template engine, not a catalog (global functions only).");

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        throw new NotSupportedException("fluid: a template engine, not a catalog (global functions only).");
}

/// <summary>
/// GLOBAL scalar (connection-free, no ATTACH): <c>fabricator_render(template, params)</c> renders the Liquid
/// template with the params bag, where <c>params</c> is EITHER a DuckDB STRUCT (type-safe, no quoting) OR a
/// JSON string.
/// <para>Fluid is secure-by-default: the context is stock, no filters or tags are registered, and only the
/// variables set here are reachable — there is no CLR member traversal.</para>
/// </summary>
internal sealed class FluidRenderFunction : IScalarFunction
{
    private static readonly FluidParser Parser = new();

    // Parse-once / render-many: templates are usually a constant literal across a batch, so cache the parsed,
    // thread-safe IFluidTemplate keyed by the template string.
    private static readonly ConcurrentDictionary<string, IFluidTemplate> Cache = new();

    public string Name => "fabricator_render";

    public Schema Parameters => new(new[]
    {
        new Field("template", StringType.Default, nullable: true),
        // The SQLNULL sentinel => the host registers this param as LogicalType::ANY, so a caller may pass a
        // STRUCT (preferred) OR a JSON string; Invoke reads the column's runtime type.
        new Field("params", NullType.Default, nullable: true),
    }, metadata: null);

    public Field Result => new("result", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var templates = (StringArray)args.Column(0);
        var paramsCol = args.Column(1); // a StructArray (preferred), a StringArray (JSON), or a NullArray
        var structType = (paramsCol as StructArray)?.Data.DataType as StructType;
        var b = new StringArray.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (templates.IsNull(i))
            {
                b.AppendNull();
                continue;
            }
            var template = Cache.GetOrAdd(templates.GetString(i), src =>
            {
                if (!Parser.TryParse(src, out var parsed, out var error))
                {
                    throw new ArgumentException($"fabricator_render: template parse error: {error}");
                }
                return parsed;
            });
            var ctx = new TemplateContext();
            if (paramsCol is StructArray sa && structType is not null)
            {
                // STRUCT params: each field becomes a template variable (the field's value at this row).
                for (int k = 0; k < structType.Fields.Count; k++)
                {
                    SetVariable(ctx, structType.Fields[k].Name, ArrowScalar.Read(sa.Fields[k], i));
                }
            }
            else if (paramsCol is StringArray jsonStrs && !jsonStrs.IsNull(i))
            {
                // JSON-string params (programmatic callers).
                using var doc = JsonDocument.Parse(jsonStrs.GetString(i));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        SetVariable(ctx, p.Name, JsonToClr(p.Value));
                    }
                }
            }
            b.Append(template.Render(ctx));
        }
        return b.Build();
    }

    /// <summary>Bind one template variable, mapping a missing value to Liquid's <c>nil</c>.</summary>
    /// <remarks>
    /// ⚠ A NULL is ordinary here, not an edge case: a STRUCT field can be NULL and JSON has <c>null</c>.
    /// <para>Fluid v3 annotates <c>SetValue(string, object)</c> as taking a NON-NULLABLE object, while its
    /// body maps null to <see cref="NilValue.Instance"/> anyway — so passing null would work and would warn.
    /// Routing null to the <c>FluidValue</c> overload OURSELVES keeps us off that implementation detail,
    /// which matters more than usual because the Fluid pin is a PRERELEASE: an internal null-handling
    /// branch is exactly the kind of thing that can move between betas, and it would move SILENTLY.</para>
    /// <para>The rendered result is unchanged either way — nil renders as empty and is falsy — which is what
    /// makes this behaviour-preserving against the 2.31.0 the plugin was first built on.</para>
    /// </remarks>
    private static void SetVariable(TemplateContext ctx, string name, object? value)
    {
        if (value is null)
        {
            ctx.SetValue(name, NilValue.Instance);
        }
        else
        {
            ctx.SetValue(name, value);
        }
    }

    private static object? JsonToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => e.EnumerateArray().Select(JsonToClr).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => JsonToClr(p.Value)),
        _ => e.ToString(),
    };
}

/// <summary>
/// A local boxing reader for one Arrow value.
/// <para><b>⚠ This is a deliberate copy of <c>ArrowValueReader.ReadScalar</c>, not an oversight.</b> That type
/// lives in <c>Fabricator.Bridge</c>, and a plugin references only <c>Fabricator.Abstractions</c> — the
/// minimal, stable plugin surface the out-of-tree plugins are written against (<c>IScalarFunction</c>'s own
/// doc says <c>ArrowValueReader</c> is available only "if a provider references the bridge"). Widening this
/// plugin's reference to the bridge to save ~20 lines would make the in-tree example stop demonstrating the
/// surface third-party authors actually have.</para>
/// <para>The type coverage mirrors the bridge's exactly, including the timestamp rule; only the
/// unsupported-type message differs, because the bridge's names the FILTER path it was written for.</para>
/// </summary>
internal static class ArrowScalar
{
    internal static object? Read(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return null;
        }
        return array switch
        {
            BooleanArray a => a.GetValue(index),
            Int8Array a => a.GetValue(index),
            Int16Array a => a.GetValue(index),
            Int32Array a => a.GetValue(index),
            Int64Array a => a.GetValue(index),
            UInt8Array a => a.GetValue(index),
            UInt16Array a => a.GetValue(index),
            UInt32Array a => a.GetValue(index),
            UInt64Array a => a.GetValue(index),
            FloatArray a => a.GetValue(index),
            DoubleArray a => a.GetValue(index),
            Decimal128Array a => a.GetValue(index),
            Decimal256Array a => a.GetValue(index),
            StringArray a => a.GetString(index),
            LargeStringArray a => a.GetString(index),
            BinaryArray a => a.GetBytes(index).ToArray(),
            Date32Array a => a.GetDateTime(index),
            Date64Array a => a.GetDateTime(index),
            TimestampArray a => ReadTimestamp(a, index),
            _ => throw new NotSupportedException(
                $"fabricator_render: unsupported STRUCT field type {array.Data.DataType.TypeId} - pass the "
                + "params as a JSON string, or cast the field to a supported type."),
        };
    }

    private static object ReadTimestamp(TimestampArray a, int index)
    {
        var ts = a.GetTimestamp(index)!.Value; // DateTimeOffset (stored as UTC when no tz)
        var type = (TimestampType)a.Data.DataType;
        // No timezone => a wall-clock value: hand back a DateTime. With timezone: the DateTimeOffset.
        // ⚠ The explicit (object) casts are load-bearing — without them C#'s conditional operator unifies
        // both branches to DateTimeOffset and the DateTime branch is converted straight back (docs §23).
        return string.IsNullOrEmpty(type.Timezone) ? (object)ts.UtcDateTime : (object)ts;
    }
}
