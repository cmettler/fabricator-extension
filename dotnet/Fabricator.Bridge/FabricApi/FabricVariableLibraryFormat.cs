using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fabricator.Bridge;

// ---------------------------------------------------------------------------------------------------
// The variable-library DEFINITION FORMAT — deliberately dependency-free.
//
// Nothing here references Arrow, the Fabric SDK, or the rest of the bridge: it turns (path, base64 payload)
// pairs into a resolved model and nothing else. That is not tidiness — this is the half of the feature that
// can be WRONG (Microsoft's own pages disagree with themselves on the folder name, the type casing, and the
// type of an override's value), and it is the half no live tenant call can check unless a variable library
// happens to exist. Keeping it free of dependencies means it can be exercised on its own.
// ---------------------------------------------------------------------------------------------------

/// <summary>One variable, resolved against a value set.</summary>
internal sealed record ResolvedVariable(
    string Name, string? Type, string? Note, string? Value, string? ValueJson, bool IsOverridden);

/// <summary>
/// A variable library's decoded definition: the defaults, every value set's sparse overrides, and the
/// declared ordering.
/// </summary>
/// <remarks>
/// ⚠ Every <see cref="JsonElement"/> kept here is <c>.Clone()</c>d. An element is a view INTO its
/// <see cref="JsonDocument"/>, and the documents are disposed as soon as parsing finishes — holding an
/// un-cloned element would read freed memory at render time.
/// </remarks>
internal sealed class VariableLibraryDefinition
{
    /// <summary>Variable name → its declaration. Case-insensitive: variable names are not case sensitive.</summary>
    internal Dictionary<string, (string? Type, string? Note, JsonElement Value)> Defaults { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Value-set name → (variable name → overriding value). Both levels case-insensitive.</summary>
    internal Dictionary<string, Dictionary<string, JsonElement>> ValueSets { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Value-set names in <c>settings.json</c>'s declared order (may be partial or absent).</summary>
    internal List<string> DeclaredOrder { get; } = new();

    /// <summary>The active value set, from the item's PROPERTIES (not the definition) — may be null.</summary>
    internal string? ActiveValueSet { get; set; }

    /// <summary>
    /// Value-set names for display: the declared order first, then anything else alphabetically — matching
    /// the service's own rule that names missing from <c>valueSetsOrder</c> are appended alphabetically.
    /// </summary>
    internal IEnumerable<string> OrderedValueSets()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in DeclaredOrder)
        {
            if (ValueSets.ContainsKey(name) && seen.Add(name))
            {
                yield return name;
            }
        }
        foreach (var name in ValueSets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(name))
            {
                yield return name;
            }
        }
    }

    /// <summary>
    /// The variables resolved against <paramref name="valueSet"/> (null/empty ⇒ the active set; no active set
    /// ⇒ the defaults alone), in declaration order.
    /// </summary>
    /// <remarks>
    /// A NAMED set that does not exist throws, listing the ones that do: silently falling back to the defaults
    /// would report plausible values for the wrong environment, which is the worst available outcome for a
    /// function whose entire purpose is telling environments apart.
    /// </remarks>
    internal IEnumerable<ResolvedVariable> Resolve(string? valueSet, out string effectiveValueSet)
    {
        var wanted = string.IsNullOrWhiteSpace(valueSet) ? ActiveValueSet : valueSet;
        Dictionary<string, JsonElement>? overrides = null;
        if (!string.IsNullOrWhiteSpace(wanted) && !ValueSets.TryGetValue(wanted!, out overrides))
        {
            // An ACTIVE set with no file is legitimate, not a caller error: the DEFAULT value set is not stored
            // under valueSets/ at all — it IS variables.json — so "active = the default set" lands here.
            if (!string.IsNullOrWhiteSpace(valueSet))
            {
                var known = ValueSets.Count == 0
                    ? "this library has no alternative value sets"
                    : "known sets: " + string.Join(", ", OrderedValueSets());
                throw new NotSupportedException(
                    $"fabric variables: value set '{valueSet}' does not exist ({known}).");
            }
            overrides = null;
        }
        effectiveValueSet = wanted ?? string.Empty;
        return Rows(overrides);
    }

    private IEnumerable<ResolvedVariable> Rows(Dictionary<string, JsonElement>? overrides)
    {
        foreach (var (name, decl) in Defaults)
        {
            bool overridden = overrides is not null && overrides.ContainsKey(name);
            var value = overridden ? overrides![name] : decl.Value;
            yield return new ResolvedVariable(
                name, decl.Type, decl.Note, Render(value), RawJson(value), overridden);
        }
    }

    /// <summary>
    /// The value as SQL text: a String variable yields its bare content, everything else its JSON rendering —
    /// so an <c>ItemReference</c> comes back as <c>{"workspaceId":"…","itemId":"…"}</c>, which DuckDB's
    /// <c>-&gt;&gt;</c> operator can pick apart. Returning NULL for object types instead would make the one
    /// variable kind this feature exists for unreadable through the scalar.
    /// </summary>
    internal static string? Render(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => Compact(v),
    };

    /// <summary>The value as JSON text in every case (a String variable's is quoted, so it stays parseable).</summary>
    internal static string? RawJson(JsonElement v) =>
        v.ValueKind == JsonValueKind.Undefined ? null : Compact(v);

    /// <summary>
    /// The element re-serialized WITHOUT the source formatting.
    /// </summary>
    /// <remarks>
    /// ⚠ Not cosmetic, and <see cref="JsonElement.GetRawText"/> is the trap: it returns the raw source span,
    /// so an object-valued variable stored pretty-printed comes back carrying its newlines and indentation —
    /// measured live as
    /// <c>{\r\n        "workspaceId": …}</c> in a SQL column. Any producer may pretty-print (the portal, a git
    /// sync, our own writer), so normalizing belongs on the READ side, not in whatever wrote the document.
    /// Re-serializing through a writer also guarantees one canonical spelling, which is what makes comparing
    /// two <c>value_json</c> values meaningful.
    /// </remarks>
    private static string Compact(JsonElement v)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            v.WriteTo(writer);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Decodes variable-library definition parts. The whole knowledge of the on-disk format.</summary>
internal static class VariableLibraryFormat
{
    /// <param name="parts">(path, base64 payload) pairs, as the item definition carries them.</param>
    internal static VariableLibraryDefinition Decode(IEnumerable<(string Path, string Payload)> parts)
    {
        var parsed = new VariableLibraryDefinition();
        foreach (var (rawPath, payload) in parts)
        {
            // ⚠ Microsoft's own definition page spells the value-set folder BOTH ways — the parts table says
            // `valueSets\valueSetName.json`, the payload example says `valueSet/valueSet1.json`. Normalize the
            // separator and accept either folder name; guessing one yields zero value sets and NO error.
            string path = rawPath.Replace('\\', '/');
            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch (FormatException)
            {
                continue; // A part we cannot decode is a part we do not understand — never a hard failure.
            }
            if (path.Equals("variables.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadVariables(json, parsed);
            }
            else if (path.Equals("settings.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadSettings(json, parsed);
            }
            else if (IsValueSetPath(path))
            {
                ReadValueSet(json, StemOf(path), parsed);
            }
        }
        return parsed;
    }

    private static bool IsValueSetPath(string path)
    {
        int slash = path.LastIndexOf('/');
        if (slash < 0 || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var folder = path[..slash];
        return folder.Equals("valueSets", StringComparison.OrdinalIgnoreCase)
               || folder.Equals("valueSet", StringComparison.OrdinalIgnoreCase);
    }

    private static string StemOf(string path)
    {
        int slash = path.LastIndexOf('/');
        var file = slash < 0 ? path : path[(slash + 1)..];
        return file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;
    }

    private static void ReadVariables(string json, VariableLibraryDefinition into)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("variables", out var vars) || vars.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var v in vars.EnumerateArray())
        {
            var name = Text(v, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            // `type` is passed through VERBATIM, never parsed into an enum: the same documentation page shows
            // both "String" and lowercase "boolean", and the concept page lists Guid + ConnectionReference
            // which the REST page's table omits. Any closed set written here would be wrong somewhere.
            var value = v.TryGetProperty("value", out var raw) ? raw.Clone() : default;
            into.Defaults[name!] = (Text(v, "type"), Text(v, "note"), value);
        }
    }

    private static void ReadValueSet(string json, string fileStem, VariableLibraryDefinition into)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // The doc says the file name "must be similar to" the set name, which is not the same as equal — so
        // the declared name wins and the stem is only the fallback.
        var name = Text(root, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fileStem;
        }
        var overrides = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("variableOverrides", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in list.EnumerateArray())
            {
                var varName = Text(o, "name");
                if (string.IsNullOrEmpty(varName))
                {
                    continue;
                }
                // ⚠ The schema table types an override's `value` as String, but an ItemReference default is an
                // OBJECT and an override of one is too. Keep the raw element; reading it as a string drops
                // every advanced-typed override to null.
                overrides[varName!] = o.TryGetProperty("value", out var raw) ? raw.Clone() : default;
            }
        }
        into.ValueSets[name!] = overrides;
    }

    private static void ReadSettings(string json, VariableLibraryDefinition into)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("valueSetsOrder", out var order)
            || order.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var e in order.EnumerateArray())
        {
            var name = e.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                into.DeclaredOrder.Add(name!);
            }
        }
    }

    private static string? Text(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // -----------------------------------------------------------------------------------------------
    // WRITE side — building and mutating the same documents. Kept here, beside the reader, so the two
    // halves cannot drift on what the format is.
    // -----------------------------------------------------------------------------------------------

    internal const string VariablesPath = "variables.json";
    internal const string SettingsPath = "settings.json";

    private const string VariablesSchema =
        "https://developer.microsoft.com/json-schemas/fabric/item/variableLibrary/definition/variables/1.0.0/schema.json";
    private const string ValueSetSchema =
        "https://developer.microsoft.com/json-schemas/fabric/item/variableLibrary/definition/valueSet/1.0.0/schema.json";
    private const string SettingsSchema =
        "https://developer.microsoft.com/json-schemas/fabric/item/variableLibrary/definition/settings/1.0.0/schema.json";

    /// <summary>The definition path of a value set. We WRITE the plural spelling and READ either.</summary>
    /// <remarks>
    /// The docs use both (see <see cref="Decode"/>); plural is the one the parts TABLE gives, which is the
    /// normative list, so that is what we emit. Reading stays tolerant because the file may not be ours.
    /// </remarks>
    internal static string ValueSetPath(string valueSet) => $"valueSets/{valueSet}.json";

    internal static JsonObject NewVariablesDoc() =>
        new() { ["$schema"] = VariablesSchema, ["variables"] = new JsonArray() };

    internal static JsonObject NewValueSetDoc(string name) =>
        new() { ["$schema"] = ValueSetSchema, ["name"] = name, ["variableOverrides"] = new JsonArray() };

    internal static JsonObject NewSettingsDoc() =>
        new() { ["$schema"] = SettingsSchema, ["valueSetsOrder"] = new JsonArray() };

    /// <summary>
    /// Renders a SQL text value into the JSON a variable of <paramref name="type"/> must carry.
    /// </summary>
    /// <remarks>
    /// <para>Typing matters on the way in: writing <c>"500"</c> where the library expects <c>500</c> produces a
    /// variable that reads back as a string and compares wrong everywhere it is used.</para>
    /// <para>An UNRECOGNIZED type is parsed as JSON and falls back to a string. That is deliberate rather than a
    /// refusal: the two documentation pages disagree about which types exist (the REST page's table omits
    /// <c>Guid</c> and <c>ConnectionReference</c>), so any closed list here would reject a legitimate type. The
    /// service validates the value against the real type regardless, which is the backstop that makes leniency
    /// safe here.</para>
    /// </remarks>
    internal static JsonNode? ValueFor(string? type, string? text)
    {
        if (text is null)
        {
            return null;
        }
        switch ((type ?? string.Empty).ToLowerInvariant())
        {
            case "string":
            case "datetime":
            case "guid":
                return JsonValue.Create(text);
            case "integer":
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? JsonValue.Create(i)
                    : throw new NotSupportedException(
                        $"fabric variables: '{text}' is not a valid Integer value.");
            case "number":
                // decimal first: it round-trips the literal a user typed, where double can print 1.1 as
                // 1.1000000000000001.
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
                {
                    return JsonValue.Create(dec);
                }
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)
                    ? JsonValue.Create(dbl)
                    : throw new NotSupportedException(
                        $"fabric variables: '{text}' is not a valid Number value.");
            case "boolean":
                return bool.TryParse(text, out var bo)
                    ? JsonValue.Create(bo)
                    : throw new NotSupportedException(
                        $"fabric variables: '{text}' is not a valid Boolean value (use true or false).");
            case "itemreference":
            case "connectionreference":
                return ParseObject(type!, text);
            default:
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    return JsonValue.Create(text);
                }
        }
    }

    private static JsonNode ParseObject(string type, string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new NotSupportedException(
                $"fabric variables: a {type} value must be a JSON object "
                + $"(e.g. {{\"workspaceId\":\"…\",\"itemId\":\"…\"}}) — {ex.Message}");
        }
        return node as JsonObject
               ?? throw new NotSupportedException(
                   $"fabric variables: a {type} value must be a JSON OBJECT, not {node?.GetValueKind().ToString() ?? "null"}.");
    }

    /// <summary>
    /// Inserts or replaces a variable in a <c>variables.json</c> document. Returns true when it was added.
    /// </summary>
    internal static bool UpsertVariable(JsonObject doc, string name, string? type, JsonNode? value, string? note)
    {
        var list = ArrayAt(doc, "variables");
        var existing = FindByName(list, name);
        var entry = new JsonObject
        {
            ["name"] = existing is null ? name : NameOf(existing) ?? name, // keep the stored casing
            ["type"] = type,
            ["value"] = value,
        };
        if (note is not null)
        {
            entry["note"] = note;
        }
        else if (existing is not null && existing["note"] is JsonNode keptNote)
        {
            entry["note"] = keptNote.DeepClone();
        }
        if (existing is null)
        {
            list.Add(entry);
            return true;
        }
        list[list.IndexOf(existing)] = entry;
        return false;
    }

    /// <summary>
    /// Inserts or replaces an override in a value-set document. Returns true when it was added.
    /// </summary>
    internal static bool UpsertOverride(JsonObject doc, string name, JsonNode? value)
    {
        var list = ArrayAt(doc, "variableOverrides");
        var existing = FindByName(list, name);
        var entry = new JsonObject
        {
            ["name"] = existing is null ? name : NameOf(existing) ?? name,
            ["value"] = value,
        };
        if (existing is null)
        {
            list.Add(entry);
            return true;
        }
        list[list.IndexOf(existing)] = entry;
        return false;
    }

    /// <summary>The declared type of a variable in a <c>variables.json</c> document, or null if absent.</summary>
    /// <remarks>
    /// This is why an OVERRIDE needs no type argument: the declaration owns the type, and taking it from there
    /// makes it impossible to override an Integer with a quoted string by passing the wrong one.
    /// </remarks>
    internal static string? DeclaredType(JsonObject doc, string name)
    {
        var existing = FindByName(ArrayAt(doc, "variables"), name);
        return existing?["type"]?.GetValue<string>();
    }

    internal static bool HasVariable(JsonObject doc, string name) =>
        FindByName(ArrayAt(doc, "variables"), name) is not null;

    internal static int VariableCount(JsonObject doc) => ArrayAt(doc, "variables").Count;

    private static JsonArray ArrayAt(JsonObject doc, string property)
    {
        if (doc[property] is JsonArray a)
        {
            return a;
        }
        var created = new JsonArray();
        doc[property] = created;
        return created;
    }

    // Variable names are NOT case sensitive, so an upsert must match that way or a second entry appears with
    // different casing and the library becomes invalid.
    private static JsonObject? FindByName(JsonArray list, string name) =>
        list.OfType<JsonObject>()
            .FirstOrDefault(o => string.Equals(NameOf(o), name, StringComparison.OrdinalIgnoreCase));

    private static string? NameOf(JsonObject o) =>
        o["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
