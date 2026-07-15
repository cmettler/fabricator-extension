using System;
using System.Collections.Concurrent;

namespace Fabricator.Bridge;

/// <summary>The DuckDB-side type of a provider setting (maps to BOOLEAN / BIGINT / VARCHAR).</summary>
public enum ProviderSettingType
{
    Bool,
    Long,
    Varchar,
}

/// <summary>
/// A setting a provider declares (see <see cref="IBackend.Settings"/>). The host registers each as a DuckDB
/// extension option at load (so <c>SET</c> / <c>duckdb_settings()</c> work), then pushes value changes back
/// into <see cref="ProviderSettingsStore"/> so the provider reads them in C# — no per-method ABI params. See
/// docs/settings-architecture.md.
/// </summary>
/// <param name="Name">The DuckDB setting name (globally unique; conventionally provider-prefixed, e.g. <c>mssql_*</c>).</param>
/// <param name="Type">DuckDB type of the value.</param>
/// <param name="Default">Default value (null =&gt; unset); a <see cref="bool"/>/<see cref="long"/>/<see cref="string"/> matching <paramref name="Type"/>.</param>
/// <param name="Description">Shown in <c>duckdb_settings()</c>.</param>
/// <param name="Min">For <see cref="ProviderSettingType.Long"/>, an inclusive minimum validated on <c>SET</c> (null =&gt; none).</param>
public sealed record ProviderSetting(
    string Name,
    ProviderSettingType Type,
    object? Default = null,
    string Description = "",
    long? Min = null);

/// <summary>
/// Process-wide store of current provider setting values, keyed by provider name then setting name
/// (case-insensitive). The host pushes values here via the <c>set_setting</c> ABI when a setting is
/// <c>SET</c> (and once at registration for defaults); providers read them via the typed getters. Values are
/// kept as their rendered string form (settings are bool/long/varchar) and parsed on read.
/// </summary>
public sealed class ProviderSettingsStore
{
    /// <summary>The shared store the bridge pushes into and providers read from.</summary>
    public static ProviderSettingsStore Instance { get; } = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _byProvider =
        new(StringComparer.OrdinalIgnoreCase);

    private ConcurrentDictionary<string, string?> Bucket(string provider) =>
        _byProvider.GetOrAdd(provider, _ => new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Push a value (rendered string; null =&gt; unset/reset). Called by the host on <c>SET</c>/registration.</summary>
    public void Set(string provider, string name, string? value) => Bucket(provider)[name] = value;

    /// <summary>The raw string value, or null if unset.</summary>
    public string? GetString(string provider, string name) =>
        Bucket(provider).TryGetValue(name, out var v) ? v : null;

    /// <summary>The value parsed as a long, or null if unset/blank/unparseable.</summary>
    public long? GetLong(string provider, string name) =>
        long.TryParse(GetString(provider, name), out var v) ? v : null;

    /// <summary>The value parsed as a bool, or null if unset/blank/unparseable. Accepts true/false/1/0.</summary>
    public bool? GetBool(string provider, string name)
    {
        var s = GetString(provider, name);
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        if (bool.TryParse(s, out var b))
        {
            return b;
        }
        return s.Trim() switch { "1" => true, "0" => false, _ => (bool?)null };
    }
}
