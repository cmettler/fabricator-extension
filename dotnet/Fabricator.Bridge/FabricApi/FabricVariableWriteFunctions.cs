using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.Fabric.Api.Core.Models;
using Microsoft.Fabric.Api.VariableLibrary.Models;

namespace Fabricator.Bridge;

// ---------------------------------------------------------------------------------------------------
// Variable-library WRITES.
//
// These are SCALARS, matching create_shortcut / drop_shortcut rather than the table-valued
// readers. Two reasons: a write wants typed positional arguments (FabricRowsFunction stringifies every
// argument, so a BOOLEAN named parameter would silently read as NULL — the exact class of half-offered
// capability this codebase keeps finding), and a scalar is what a setup script embeds.
//
// ⚠ EVERY function here must stay VOLATILE (the default — never set IsVolatile => false). A CONSISTENT
// function is constant-folded at plan time, which for a write means it may run at bind, run once for a
// hundred rows, or be elided entirely. The read-side variable() is CONSISTENT precisely because it
// is pure; these are its opposite.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// Read-modify-write plumbing for a variable library's definition.
/// </summary>
/// <remarks>
/// <para><b>⚠ The definition API is WHOLE-DOCUMENT.</b> <c>UpdateVariableLibraryDefinition</c> replaces every
/// part, so a write that sends only the part it changed DELETES the value sets and the settings. Every
/// mutation here therefore reads all parts, replaces one, and sends them all back — that round trip is not
/// defensive, it is the only correct way to change one variable.</para>
/// <para><b>⚠ There is no ETag or If-Match on this API</b>, so the read-modify-write is LAST-WRITER-WINS:
/// two concurrent <c>set_variable</c> calls against one library can lose one of the two changes. Set
/// variables from one place, or use <c>set_variables_json</c>, which writes the whole document in a
/// single call and so cannot interleave with itself.</para>
/// <para><b>Cost</b>: both halves are long-running operations, so one <c>set_variable</c> is two LROs.
/// Declaring several variables one call at a time is correspondingly slow;
/// <c>set_variables_json</c> exists for that.</para>
/// </remarks>
internal static class VariableLibraryWriter
{
    /// <summary>Every part of the current definition, as (path, decoded UTF-8 text).</summary>
    internal static List<(string Path, string Text)> GetParts(
        FabricApiClient api, Guid ws, Guid id, CancellationToken ct)
    {
        var response = FabricApiClient.Wrap("variable_library_definition",
            () => api.Client.VariableLibrary.Items
                     .GetVariableLibraryDefinition(ws, id, cancellationToken: ct).Value);
        var parts = new List<(string, string)>();
        foreach (var p in response.Definition?.Parts ?? new List<VariableLibraryPublicDefinitionPart>())
        {
            if (p.Payload is null)
            {
                continue;
            }
            string text;
            try
            {
                text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p.Payload));
            }
            catch (FormatException)
            {
                continue;
            }
            parts.Add((p.Path?.Replace('\\', '/') ?? string.Empty, text));
        }
        return parts;
    }

    /// <summary>Sends the WHOLE part list back, replacing the definition.</summary>
    internal static void PutParts(
        FabricApiClient api, Guid ws, Guid id, List<(string Path, string Text)> parts, CancellationToken ct)
    {
        // settings.json is documented Required. If the item somehow lacks it, adding a minimal one keeps the
        // document we send valid; we never remove or rewrite an existing one.
        if (!parts.Any(p => p.Path.Equals(VariableLibraryFormat.SettingsPath, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add((VariableLibraryFormat.SettingsPath, VariableLibraryFormat.NewSettingsDoc().ToJsonString()));
        }
        var payload = parts.Select(p => new VariableLibraryPublicDefinitionPart(
            p.Path, Base64(p.Text), PayloadType.InlineBase64)).ToList();

        // ⚠ `updateMetadata` is deliberately NOT set. It requires a `.platform` part in the payload (the same
        // trap recorded for notebook definitions), and we only round-trip the parts the item already had.
        FabricApiClient.Wrap("set_variable_library_definition",
            () => api.Client.VariableLibrary.Items.UpdateVariableLibraryDefinition(
                ws, id,
                new UpdateVariableLibraryDefinitionRequest(new VariableLibraryPublicDefinition(payload)),
                cancellationToken: ct));
    }

    /// <summary>The named part parsed as a JSON object, or <paramref name="fallback"/> when it is absent.</summary>
    internal static JsonObject PartObject(
        List<(string Path, string Text)> parts, string path, Func<JsonObject> fallback)
    {
        foreach (var (p, text) in parts)
        {
            if (!p.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                if (JsonNode.Parse(text) is JsonObject o)
                {
                    return o;
                }
            }
            catch (JsonException)
            {
                // Fall through: a part we cannot parse is replaced rather than allowed to block the write.
            }
            break;
        }
        return fallback();
    }

    /// <summary>Replaces (or appends) a part's text in the list, matching the path case-insensitively.</summary>
    internal static void SetPart(List<(string Path, string Text)> parts, string path, JsonObject doc)
    {
        var text = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = (parts[i].Path, text); // keep the path spelling the service gave us
                return;
            }
        }
        parts.Add((path, text));
    }

    internal static string Base64(string text) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Resolves an existing library to (workspace, item), for the attach's workspace.</summary>
    internal static (Guid Ws, Guid Id) Resolve(FabricApiClient api, string? library)
    {
        if (string.IsNullOrWhiteSpace(library))
        {
            throw new NotSupportedException(
                "fabric variables: name the variable library (list them with variable_libraries()).");
        }
        var ws = api.ResolveWorkspace(null);
        return (ws, api.ResolveItem(library, FabricVariableFunctions.ItemType, ws));
    }
}

// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric.create_variable_library(name, description)</c> — creates an empty variable library, returning
/// its id. Pass NULL for no description.
/// </summary>
/// <remarks>
/// The definition is deliberately NOT supplied: the service creates a valid empty item, and sending our own
/// <c>variables.json</c> would be asserting a schema version we do not control. The setters tolerate a
/// missing part anyway, so nothing downstream depends on which choice was made.
/// </remarks>
internal sealed class FabricCreateVariableLibraryFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricCreateVariableLibraryFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "create_variable_library";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("description"),
    }, null);

    public Field Result { get; } = new("id", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new StringArray.Builder();
        var ws = _api.ResolveWorkspace(null);
        for (int row = 0; row < args.Length; row++)
        {
            var name = FabricArgs.Str(args, 0, row)
                       ?? throw new NotSupportedException(
                           "create_variable_library: 'name' must not be NULL.");
            var request = new CreateVariableLibraryRequest(name);
            var description = FabricArgs.Str(args, 1, row);
            if (!string.IsNullOrEmpty(description))
            {
                request.Description = description;
            }
            var created = FabricApiClient.Wrap("create_variable_library",
                () => _api.Client.VariableLibrary.Items.CreateVariableLibrary(ws, request).Value);
            b.Append(created.Id?.ToString());
        }
        return b.Build();
    });
}

// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric.set_variable(library, name, type, value)</c> — declares a variable or replaces its DEFAULT value.
/// Returns <c>'created'</c> or <c>'updated'</c>.
/// </summary>
/// <remarks>
/// <para><paramref name="Parameters"/>' <c>type</c> is the Fabric variable type — <c>String</c>,
/// <c>Integer</c>, <c>Number</c>, <c>Boolean</c>, <c>DateTime</c>, <c>Guid</c>, <c>ItemReference</c>,
/// <c>ConnectionReference</c> — and it decides how <c>value</c> (always SQL text) is rendered into JSON. An
/// <c>ItemReference</c> takes the object as text:
/// <c>'{"workspaceId":"…","itemId":"…"}'</c>.</para>
/// <para>Two long-running operations per call; see <see cref="VariableLibraryWriter"/> for why, and for the
/// lost-update caveat.</para>
/// </remarks>
internal sealed class FabricSetVariableFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricSetVariableFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "set_variable";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("library"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("type"),
        FabricApiFunctions.Str("value"),
    }, null);

    public Field Result { get; } = new("action", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new StringArray.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            var (ws, id) = VariableLibraryWriter.Resolve(_api, FabricArgs.Str(args, 0, row));
            var name = FabricArgs.Str(args, 1, row)
                       ?? throw new NotSupportedException("set_variable: 'name' must not be NULL.");
            var type = FabricArgs.Str(args, 2, row)
                       ?? throw new NotSupportedException(
                           "set_variable: 'type' must not be NULL (e.g. 'String', 'Integer', 'ItemReference').");
            var value = VariableLibraryFormat.ValueFor(type, FabricArgs.Str(args, 3, row));

            var parts = VariableLibraryWriter.GetParts(_api, ws, id, CancellationToken.None);
            var doc = VariableLibraryWriter.PartObject(
                parts, VariableLibraryFormat.VariablesPath, VariableLibraryFormat.NewVariablesDoc);
            bool created = VariableLibraryFormat.UpsertVariable(doc, name, type, value, note: null);
            VariableLibraryWriter.SetPart(parts, VariableLibraryFormat.VariablesPath, doc);
            VariableLibraryWriter.PutParts(_api, ws, id, parts, CancellationToken.None);
            b.Append(created ? "created" : "updated");
        }
        return b.Build();
    });
}

// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric.set_variables_json(library, variables_json)</c> — replaces the library's whole default variable
/// set in ONE write. Returns the number of variables written.
/// </summary>
/// <remarks>
/// <para>Accepts either a full document <c>{"variables":[…]}</c> or a bare array <c>[…]</c>; each entry needs
/// <c>name</c>, <c>type</c> and <c>value</c>, with <c>value</c> already JSON-typed (a String's is quoted, an
/// Integer's is not).</para>
/// <para>This is the shape a CI/CD script wants: <b>one</b> read-modify-write instead of one per variable, so
/// it is both far faster and the only setter that cannot lose a concurrent update to a sibling variable.
/// Value sets and settings are preserved — only <c>variables.json</c> is replaced.</para>
/// <para><b>It REPLACES, it does not merge</b>: a variable absent from the payload is removed. That is what
/// makes it usable as a declarative desired-state write, and it is also how someone loses variables they
/// meant to keep.</para>
/// </remarks>
internal sealed class FabricSetVariablesJsonFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricSetVariablesJsonFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "set_variables_json";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("library"),
        FabricApiFunctions.Str("variables_json"),
    }, null);

    public Field Result { get; } = new("variables", Int64Type.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new Int64Array.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            var (ws, id) = VariableLibraryWriter.Resolve(_api, FabricArgs.Str(args, 0, row));
            var json = FabricArgs.Str(args, 1, row)
                       ?? throw new NotSupportedException(
                           "set_variables_json: 'variables_json' must not be NULL.");
            var doc = BuildVariablesDoc(json);

            var parts = VariableLibraryWriter.GetParts(_api, ws, id, CancellationToken.None);
            VariableLibraryWriter.SetPart(parts, VariableLibraryFormat.VariablesPath, doc);
            VariableLibraryWriter.PutParts(_api, ws, id, parts, CancellationToken.None);
            b.Append(VariableLibraryFormat.VariableCount(doc));
        }
        return b.Build();
    });

    /// <summary>Normalizes the caller's JSON into a valid <c>variables.json</c> document.</summary>
    internal static JsonObject BuildVariablesDoc(string json)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new NotSupportedException($"set_variables_json: not valid JSON — {ex.Message}");
        }
        var array = parsed switch
        {
            JsonArray a => a,
            JsonObject o when o["variables"] is JsonArray inner => inner,
            _ => throw new NotSupportedException(
                "set_variables_json: expected a JSON array of variables, or an object with a "
                + "'variables' array."),
        };
        var doc = VariableLibraryFormat.NewVariablesDoc();
        foreach (var node in array)
        {
            if (node is not JsonObject entry)
            {
                throw new NotSupportedException(
                    "set_variables_json: every element must be an object with name/type/value.");
            }
            var name = entry["name"]?.GetValue<string>()
                       ?? throw new NotSupportedException(
                           "set_variables_json: an element is missing 'name'.");
            var type = entry["type"]?.GetValue<string>()
                       ?? throw new NotSupportedException(
                           $"set_variables_json: variable '{name}' is missing 'type'.");
            if (entry["value"] is null)
            {
                throw new NotSupportedException(
                    $"set_variables_json: variable '{name}' is missing 'value'.");
            }
            VariableLibraryFormat.UpsertVariable(
                doc, name, type, entry["value"]!.DeepClone(), entry["note"]?.GetValue<string>());
        }
        return doc;
    }
}

// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric.set_variable_override(library, value_set, name, value)</c> — sets a variable's value in an
/// alternative value set, creating the set if it does not exist. Returns <c>'created'</c> or <c>'updated'</c>.
/// </summary>
/// <remarks>
/// <para>Takes no <c>type</c>: the DECLARATION owns the type, so it is read from <c>variables.json</c>. That
/// is not just convenience — it makes it impossible to override an Integer with a quoted string by passing
/// the wrong type here, which the service would then reject or, worse, accept.</para>
/// <para>The variable must already be declared; overriding an undeclared name is refused, because a value set
/// holding an override for a variable that does not exist is a silent no-op at resolution time.</para>
/// </remarks>
internal sealed class FabricSetVariableOverrideFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricSetVariableOverrideFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "set_variable_override";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("library"),
        FabricApiFunctions.Str("value_set"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("value"),
    }, null);

    public Field Result { get; } = new("action", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new StringArray.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            var (ws, id) = VariableLibraryWriter.Resolve(_api, FabricArgs.Str(args, 0, row));
            var set = FabricArgs.Str(args, 1, row)
                      ?? throw new NotSupportedException(
                          "set_variable_override: 'value_set' must not be NULL.");
            var name = FabricArgs.Str(args, 2, row)
                       ?? throw new NotSupportedException(
                           "set_variable_override: 'name' must not be NULL.");

            var parts = VariableLibraryWriter.GetParts(_api, ws, id, CancellationToken.None);
            var variables = VariableLibraryWriter.PartObject(
                parts, VariableLibraryFormat.VariablesPath, VariableLibraryFormat.NewVariablesDoc);
            var type = VariableLibraryFormat.DeclaredType(variables, name);
            if (!VariableLibraryFormat.HasVariable(variables, name))
            {
                throw new NotSupportedException(
                    $"set_variable_override: '{name}' is not declared in this library — declare it "
                    + "first with set_variable(), otherwise the override would never resolve.");
            }
            var value = VariableLibraryFormat.ValueFor(type, FabricArgs.Str(args, 3, row));

            var path = VariableLibraryFormat.ValueSetPath(set);
            var doc = VariableLibraryWriter.PartObject(parts, path, () => VariableLibraryFormat.NewValueSetDoc(set));
            bool created = VariableLibraryFormat.UpsertOverride(doc, name, value);
            VariableLibraryWriter.SetPart(parts, path, doc);
            VariableLibraryWriter.PutParts(_api, ws, id, parts, CancellationToken.None);
            b.Append(created ? "created" : "updated");
        }
        return b.Build();
    });
}

// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric.set_active_value_set(library, value_set)</c> — switches which value set the library resolves
/// through. Returns true.
/// </summary>
/// <remarks>
/// A PROPERTIES update, not a definition update — so unlike the setters above it is a single cheap call, and
/// it is what a deployment pipeline flips per stage.
/// </remarks>
internal sealed class FabricSetActiveValueSetFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricSetActiveValueSetFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "set_active_value_set";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("library"),
        FabricApiFunctions.Str("value_set"),
    }, null);

    public Field Result { get; } = new("ok", BooleanType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new BooleanArray.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            var (ws, id) = VariableLibraryWriter.Resolve(_api, FabricArgs.Str(args, 0, row));
            var set = FabricArgs.Str(args, 1, row)
                      ?? throw new NotSupportedException(
                          "set_active_value_set: 'value_set' must not be NULL.");
            var request = new UpdateVariableLibraryRequest { Properties = new VariableLibraryProperties(set) };
            FabricApiClient.Wrap("set_active_value_set",
                () => _api.Client.VariableLibrary.Items.UpdateVariableLibrary(ws, id, request));
            b.Append(true);
        }
        return b.Build();
    });
}

// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric.drop_variable_library(library, if_exists)</c> — deletes the library. Returns whether it was
/// there.
/// </summary>
internal sealed class FabricDropVariableLibraryFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricDropVariableLibraryFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "drop_variable_library";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("library"),
        new Field("if_exists", BooleanType.Default, nullable: true),
    }, null);

    public Field Result { get; } = new("dropped", BooleanType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new BooleanArray.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            bool ifExists = FabricArgs.Bool(args, 1, row) ?? false;
            var library = FabricArgs.Str(args, 0, row);
            // Checked BEFORE the guarded resolve: a NULL name is a caller bug, and `if_exists` must not
            // convert it into a placid "false". `if_exists` means "the library may be absent", not
            // "swallow anything".
            if (string.IsNullOrWhiteSpace(library))
            {
                throw new NotSupportedException("drop_variable_library: 'library' must not be NULL.");
            }
            Guid ws, id;
            try
            {
                (ws, id) = VariableLibraryWriter.Resolve(_api, library);
            }
            catch (NotSupportedException) when (ifExists)
            {
                // ResolveItem raises "not found" as a NotSupportedException; with if_exists that IS the answer.
                b.Append(false);
                continue;
            }
            FabricApiClient.Wrap("drop_variable_library",
                () => _api.Client.VariableLibrary.Items.DeleteVariableLibrary(ws, id));
            b.Append(true);
        }
        return b.Build();
    });
}
