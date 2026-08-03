using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Variable Library functions — read a Fabric variable library's variables, resolved against a value set.
/// </summary>
/// <remarks>
/// <para><b>Why these exist.</b> A variable library is Fabric's answer to per-environment configuration: the
/// same library carries a default value set plus alternative sets (dev/test/prod), and a deployment pipeline
/// switches which one is ACTIVE. An <c>ItemReference</c> variable holds exactly a
/// <c>{workspaceId, itemId}</c> pair — which is what our own <c>workspace :=</c> / <c>item :=</c> overrides
/// consume — so a dbt project can read its target lakehouse from the library instead of hardcoding it.</para>
/// <para><b>There is no "effective value" API.</b> The typed model stops at
/// <c>VariableLibraryProperties.ActiveValueSetName</c>; every value lives inside the item's DEFINITION as
/// base64 parts. So resolution is ours: decode <c>variables.json</c> for the defaults, decode
/// <c>valueSets/&lt;name&gt;.json</c> for the sparse overrides, overlay by name. Same shape as
/// <c>fabric_notebook_parameters</c>. The format itself lives in <see cref="VariableLibraryFormat"/>.</para>
/// <para><b>⚠ Reading the definition is a LONG-RUNNING OPERATION</b> (the SDK method takes
/// <c>timeoutInMinutes</c>), like <c>GetNotebookDefinition</c>. Every function here reads it at most once per
/// call, and <see cref="FabricVariableFunction"/> deduplicates by library across an argument batch.</para>
/// </remarks>
internal static class FabricVariableFunctions
{
    /// <summary>The Fabric item type, as <c>ListItems</c>/<c>ResolveItem</c> spell it.</summary>
    internal const string ItemType = "VariableLibrary";

    internal static void Register(
        List<ICatalogScalarFunction> scalars, List<ICatalogTableFunction> tables, FabricApiClient api)
    {
        tables.Add(new FabricVariableLibrariesFunction(api));
        tables.Add(new FabricVariablesFunction(api));
        tables.Add(new FabricVariableValueSetsFunction(api));
        scalars.Add(new FabricVariableFunction(api));

        // Writes — scalars, matching the shortcut CRUD. See FabricVariableWriteFunctions.cs for why they are
        // scalars, why they must all stay VOLATILE, and the whole-document read-modify-write caveat.
        scalars.Add(new FabricCreateVariableLibraryFunction(api));
        scalars.Add(new FabricSetVariableFunction(api));
        scalars.Add(new FabricSetVariablesJsonFunction(api));
        scalars.Add(new FabricSetVariableOverrideFunction(api));
        scalars.Add(new FabricSetActiveValueSetFunction(api));
        scalars.Add(new FabricDropVariableLibraryFunction(api));
    }
}

// ---------------------------------------------------------------------------------------------------
// The definition reader. The FORMAT it decodes lives in FabricVariableLibraryFormat.cs, dependency-free.
// ---------------------------------------------------------------------------------------------------

/// <summary>Resolves a library and fetches its definition, handing the bytes to the format decoder.</summary>
internal static class VariableLibraryReader
{
    /// <summary>
    /// Resolves the library, reads its properties (cheap) and its definition (an LRO), and decodes both.
    /// </summary>
    internal static VariableLibraryDefinition Read(
        FabricApiClient api, string? library, string? workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(library))
        {
            throw new NotSupportedException(
                "fabric variables: name the variable library (list them with fabric_variable_libraries()).");
        }
        var ws = api.ResolveWorkspace(workspace);
        var id = api.ResolveItem(library, FabricVariableFunctions.ItemType, ws);

        // The ACTIVE set lives on the item's properties, not in the definition — a separate, cheap GET.
        var active = FabricApiClient.Wrap("variable_library",
            () => api.Client.VariableLibrary.Items.GetVariableLibrary(ws, id, ct).Value)
            .Properties?.ActiveValueSetName;

        // `format` is omitted deliberately: it is optional and defaults to the item's own format, so naming one
        // would be guessing at a string the service already knows.
        var response = FabricApiClient.Wrap("variable_library_definition",
            () => api.Client.VariableLibrary.Items
                     .GetVariableLibraryDefinition(ws, id, cancellationToken: ct).Value);
        var parts = response.Definition?.Parts
                    ?? (IList<Microsoft.Fabric.Api.VariableLibrary.Models.VariableLibraryPublicDefinitionPart>)
                        System.Array.Empty<Microsoft.Fabric.Api.VariableLibrary.Models.VariableLibraryPublicDefinitionPart>();

        var parsed = VariableLibraryFormat.Decode(
            parts.Where(p => p.Payload is not null)
                 .Select(p => (p.Path ?? string.Empty, p.Payload!)));
        parsed.ActiveValueSet = active;
        return parsed;
    }
}

// ---------------------------------------------------------------------------------------------------
// fabric_variable_libraries() — the cheap listing.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric_variable_libraries()</c> — the variable libraries in this catalog's workspace, with the value set
/// each one currently has active.
/// </summary>
/// <remarks>Cheap: a plain list, no definition read. The active value set comes straight off the item's
/// properties.</remarks>
internal sealed class FabricVariableLibrariesFunction : FabricRowsFunction
{
    internal FabricVariableLibrariesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_variable_libraries";

    /// <summary><c>workspace := 'OtherWS'</c> — list another workspace's libraries.</summary>
    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("description"),
        FabricApiFunctions.Str("active_value_set"),
        FabricApiFunctions.Str("workspace_id"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[0]);
        // WrapList, not Wrap: a PageableResponse is lazy, so the request happens during ENUMERATION and a
        // guard around the call itself would guard nothing.
        foreach (var lib in FabricApiClient.WrapList("variable_libraries",
                     () => Api.Client.VariableLibrary.Items.ListVariableLibraries(ws, cancellationToken: ct)))
        {
            row.Str(0, lib.DisplayName);
            row.Str(1, lib.Id?.ToString());
            row.Str(2, lib.Description);
            row.Str(3, lib.Properties?.ActiveValueSetName);
            row.Str(4, (lib.WorkspaceId ?? ws).ToString());
            row.EndRow();
        }
    }
}

// ---------------------------------------------------------------------------------------------------
// fabric_variables(library [, value_set := …]) — the workhorse.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric_variables(library [, value_set := 'prod'] [, workspace := …])</c> — one row per variable, resolved
/// against the active value set (or the one named).
/// </summary>
/// <remarks>
/// <para><b>Not cheap</b>: reads the item definition, a long-running operation. Never call it per row — that is
/// what <c>fabric_variable()</c> is for.</para>
/// <para><c>value</c> is the value as text; <c>value_json</c> is the same value as JSON (a String variable's is
/// quoted, so it stays parseable). For an <c>ItemReference</c> both carry the object, so
/// <c>value_json -&gt;&gt; 'itemId'</c> gets the id.</para>
/// </remarks>
internal sealed class FabricVariablesFunction : FabricRowsFunction
{
    internal FabricVariablesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_variables";

    public override Schema Parameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("library") }, null);

    public override Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("value_set"),
        FabricApiFunctions.Str("workspace"),
    }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("type"),
        FabricApiFunctions.Str("value"),
        FabricApiFunctions.Str("value_json"),
        FabricApiFunctions.Bool("is_overridden"),
        FabricApiFunctions.Str("value_set"),
        FabricApiFunctions.Str("note"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var parsed = VariableLibraryReader.Read(Api, args[0], args[2], ct);
        foreach (var v in parsed.Resolve(args[1], out var effective))
        {
            row.Str(0, v.Name);
            row.Str(1, v.Type);
            row.Str(2, v.Value);
            row.Str(3, v.ValueJson);
            row.Bool(4, v.IsOverridden);
            row.Str(5, string.IsNullOrEmpty(effective) ? null : effective);
            row.Str(6, v.Note);
            row.EndRow();
        }
    }
}

// ---------------------------------------------------------------------------------------------------
// fabric_variable_value_sets(library) — which environments this library defines.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric_variable_value_sets(library [, workspace := …])</c> — the alternative value sets, in the library's
/// declared order, flagging which is active.
/// </summary>
/// <remarks>
/// <para>Reads the definition (an LRO), same cost as <c>fabric_variables</c>.</para>
/// <para>The DEFAULT value set has no file under <c>valueSets/</c> — it is <c>variables.json</c> itself — so it
/// is not a row here. A library with no alternatives returns zero rows, which is legitimate.</para>
/// </remarks>
internal sealed class FabricVariableValueSetsFunction : FabricRowsFunction
{
    internal FabricVariableValueSetsFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_variable_value_sets";

    public override Schema Parameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("library") }, null);

    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Bool("is_active"),
        FabricApiFunctions.Int64("override_count"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var parsed = VariableLibraryReader.Read(Api, args[0], args[1], ct);
        foreach (var name in parsed.OrderedValueSets())
        {
            row.Str(0, name);
            row.Bool(1, string.Equals(name, parsed.ActiveValueSet, StringComparison.OrdinalIgnoreCase));
            row.Int(2, parsed.ValueSets[name].Count);
            row.EndRow();
        }
    }
}

// ---------------------------------------------------------------------------------------------------
// fabric_variable(library, name) — the scalar, for use as an argument.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric_variable(library, name)</c> — one variable's value, resolved against the active value set, as text.
/// </summary>
/// <remarks>
/// <para><b>Declared CONSISTENT (<see cref="IsVolatile"/> = false), and that is load-bearing.</b> The default
/// for a scalar here is VOLATILE, and a volatile function is never constant-folded — so with the default this
/// would run once per row of whatever it was selected over. As CONSISTENT,
/// <c>SELECT fabric_variable('cfg','x') FROM big_table</c> folds to a literal in the optimizer
/// (<c>BoundFunctionExpression::IsFoldable</c> is exactly <c>stability != VOLATILE</c>) and the definition is
/// read ONCE. Used as a table-function argument it is evaluated once regardless: the binder calls
/// <c>EvaluateScalar</c> at bind time, with <c>allow_unfoldable</c> set.</para>
/// <para><b>Consequence to know:</b> folding happens at plan time, so a PREPARED statement bakes the value into
/// its cached plan and will not observe a later change to the library. Right for configuration, surprising if
/// unexpected.</para>
/// <para><b>Cost.</b> One definition read (an LRO) per distinct library in the argument batch — so the
/// ordinary constant-argument call is one read, and even
/// <c>SELECT fabric_variable('cfg', name) FROM t</c> (a shape <c>fabric_variables()</c> serves better) does not
/// read once per row. There is deliberately no cache ACROSS calls: that needs a staleness policy, and this is
/// configuration people expect to be able to change.</para>
/// <para><b>The active value set is the only one reachable here, on purpose.</b> A variable name is not bound
/// to a value set — the library has exactly one active set at a time, and that is what every other consumer
/// (a notebook, a pipeline) resolves through. Reading a DIFFERENT set is an inspection need, served by
/// <c>fabric_variables(…, value_set := …)</c>; it cannot be offered here because DuckDB scalar functions have
/// no named parameters and match arity exactly, so a third positional argument would break the two-argument
/// call this exists for.</para>
/// <para>For the same reason there is no <c>workspace :=</c> override: this always reads the attach's
/// workspace.</para>
/// </remarks>
internal sealed class FabricVariableFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricVariableFunction(FabricApiClient api) => _api = api;

    public string SchemaName => CatalogFunctionSet.AllSchemas;
    public string Name => "fabric_variable";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("library"),
        FabricApiFunctions.Str("name"),
    }, null);

    public Field Result { get; } = new("value", StringType.Default, nullable: true);

    /// <summary>Pure: same library + name ⇒ same value. See the remarks — this is what avoids a per-row LRO.</summary>
    public bool IsVolatile => false;

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new StringArray.Builder();
        // One definition read per DISTINCT library across the batch, not per row.
        var byLibrary = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        for (int row = 0; row < args.Length; row++)
        {
            var library = FabricArgs.Str(args, 0, row);
            var name = FabricArgs.Str(args, 1, row);
            if (string.IsNullOrWhiteSpace(library) || string.IsNullOrWhiteSpace(name))
            {
                b.AppendNull();
                continue;
            }
            if (!byLibrary.TryGetValue(library!, out var values))
            {
                var parsed = VariableLibraryReader.Read(_api, library, null, CancellationToken.None);
                values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in parsed.Resolve(null, out _))
                {
                    values[v.Name] = v.Value;
                }
                byLibrary[library!] = values;
            }
            // An absent variable is an ERROR, not NULL: a typo in a config lookup that silently yields NULL
            // propagates into whatever the value was feeding, and the failure then surfaces somewhere unrelated.
            if (!values.TryGetValue(name!, out var value))
            {
                throw new NotSupportedException(
                    $"fabric_variable: '{library}' declares no variable named '{name}' "
                    + $"(it has: {(values.Count == 0 ? "none" : string.Join(", ", values.Keys.OrderBy(k => k)))}).");
            }
            b.Append(value);
        }
        return b.Build();
    });
}
