// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The Fluid/Liquid template engine, packaged as a plugin. It exposes no catalog (ATTACH throws) and exists
/// purely to contribute connection-free GLOBAL functions: <c>fluid_render</c> (template → TEXT) and
/// <c>fluid_query</c> (template → SQL, i.e. a RELATION).
/// <para><b>Why it is a plugin rather than part of a backend.</b> It lived in <c>Fabricator.SqlServer</c>,
/// which put a template engine — and the <c>Fluid.Core</c> package — inside the SQL Server backend, where it
/// has nothing to do with SQL Server and rode into every shipped payload whether or not anyone rendered a
/// template. It is also the one dependency the AOT SKU has to reason about (docs/aot-bridge.md: Parlot's
/// compiled mode uses <c>System.Linq.Expressions</c>), so moving it out of the core removes that conditional
/// entirely.</para>
/// <para><b>⚠ CONSEQUENCE, and it is user-visible: these functions are OPT-IN.</b> A global function can only
/// be registered during <c>Extension::Load()</c>, so this plugin must be present in a plugin root at load
/// time — installing it mid-session does not surface the functions until the next session
/// (docs/plugin-system.md, "the reload split").</para>
/// <para><b>⚠ It is also the first in-tree plugin with a PRIVATE PACKAGE DEPENDENCY.</b>
/// <c>Fabricator.SamplePlugin</c> is pure IL, so until this existed nothing exercised
/// <c>ProviderRegistry.InstallPluginResolver</c> actually loading a plugin's own NuGet closure (here
/// <c>Fluid.Core</c> and its transitive <c>Parlot</c>) out of the plugin folder.</para>
/// </summary>
public sealed class FluidPluginBackend : IProvider
{
    public string Name => "fluid";

    /// <summary>
    /// FALSE: this contributes global functions and one setting, and hosts nothing to ATTACH. The registry
    /// refuses <c>PROVIDER 'fluid'</c> BY NAME because of this, so the two throwing members below are a
    /// backstop rather than the user-facing message.
    /// </summary>
    public bool HostsCatalog => false;

    public IEnumerable<IScalarFunction> GlobalScalarFunctions =>
        new IScalarFunction[] { new FluidRenderFunction() };

    public IEnumerable<ISqlTableFunction> GlobalSqlTableFunctions =>
        new ISqlTableFunction[] { new FluidQueryFunction() };

    /// <summary>
    /// The one setting this plugin declares: where <c>{% include %}</c> / <c>{% render %}</c> resolve from.
    /// </summary>
    /// <remarks>
    /// ⚠ It is registered because a PLUGIN's settings go through the same path a backend's do
    /// (<c>ProviderRegistry.All()</c> at load), which is also why it shares the load-time opt-in property of
    /// the functions above: the plugin has to be in a plugin root when the extension loads.
    /// </remarks>
    public IEnumerable<ProviderSetting> Settings =>
        new[]
        {
            new ProviderSetting(
                HostTemplateFileProvider.RootSetting,
                ProviderSettingType.Varchar,
                Default: null,
                Description: "Directory or URI prefix that {% include %} and {% render %} resolve against, " +
                             "read through DuckDB's own FileSystem (so s3://, abfss:// and onelake:// work " +
                             "with the secrets already in scope). Unset by default: an include is REFUSED " +
                             "rather than resolved against the process working directory."),
        };

    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) =>
        throw new NotSupportedException("fluid: a template engine, not a catalog (global functions only).");

    public IProviderCatalog OpenCatalog(string connectionString, string optionsJson) =>
        throw new NotSupportedException("fluid: a template engine, not a catalog (global functions only).");
}

/// <summary>
/// GLOBAL scalar (connection-free, no ATTACH): <c>fluid_render(template, params)</c> renders the Liquid
/// template with the params bag, where <c>params</c> is EITHER a DuckDB STRUCT/MAP (type-safe, no quoting) OR
/// a JSON string.
/// <para>Fluid is secure-by-default: the context is stock apart from this plugin's two SQL-quoting filters,
/// no tags are registered, and only the variables bound here are reachable — there is no CLR member
/// traversal.</para>
/// <para>The params bag, the number model and the nested-value handling are
/// <see cref="FluidValueModel"/>'s, shared with <see cref="FluidQueryFunction"/> so the same bag cannot mean
/// two different things depending on which function received it.</para>
/// </summary>
internal sealed class FluidRenderFunction : IScalarFunction
{
    public string Name => "fluid_render";

    public Schema Parameters => new(new[]
    {
        new Field("template", StringType.Default, nullable: true),
        // The SQLNULL sentinel => the host registers this param as LogicalType::ANY, so a caller may pass a
        // STRUCT (preferred), a MAP, or a JSON string; Invoke reads the column's runtime type.
        new Field("params", NullType.Default, nullable: true),
    }, metadata: null);

    public Field Result => new("result", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var templates = (StringArray)args.Column(0);
        var paramsCol = args.Column(1); // a StructArray/MapArray (preferred), a StringArray (JSON), or NullArray
        var b = new StringArray.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (templates.IsNull(i))
            {
                b.AppendNull();
                continue;
            }
            int row = i;
            // ⚠ This surface renders at EXECUTE time (a VOLATILE scalar, never folded into the plan), so a
            // writing template here runs once per ROW rather than once per BIND. fluid_query permits exec
            // too, where it runs during binding — see FluidHostExec.
            b.Append(FluidEngine.Render(Name, templates.GetString(i),
                                        ctx => FluidValueModel.Bind(ctx, paramsCol, row)));
        }
        return b.Build();
    }
}
