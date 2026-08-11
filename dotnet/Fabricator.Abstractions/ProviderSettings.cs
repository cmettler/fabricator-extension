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
/// Store of current provider setting values, keyed by provider name then setting name (case-insensitive).
/// The host pushes values here via the <c>set_setting</c> ABI when a setting is <c>SET</c> (and once at
/// registration for defaults); providers read them via the typed getters. Values are kept as their rendered
/// string form (settings are bool/long/varchar) and parsed on read.
///
/// <para><b>TWO LAYERS: per-SESSION over GLOBAL.</b> A read resolves <c>session ?? global ?? null</c>, where
/// the session key is the DuckDB connection the operation belongs to (the host's opener, which is a
/// <c>ClientContext *</c>). Without the session layer the store was process-wide and DuckDB's
/// <c>SetScope</c> was discarded, so a <c>SET</c> in one connection changed what ANOTHER connection did —
/// MEASURED, and with the sharpest possible observable: <c>SET mssql_mars='false'</c> in connection A made a
/// same-catalog CTAS in connection B (which set nothing) return 10 rows instead of 15, i.e. it changed the
/// DATA another connection saw. That breaks the obvious use of a dbt pre-hook to configure ONE model.</para>
///
/// <para>⚠ A session entry must be REMOVED when its connection goes away, or the store grows for the life of
/// the process — see <see cref="ClearSession"/>. The host owns that call.</para>
/// </summary>
public sealed class ProviderSettingsStore
{
    /// <summary>The shared store the bridge pushes into and providers read from.</summary>
    public static ProviderSettingsStore Instance { get; } = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _byProvider =
        new(StringComparer.OrdinalIgnoreCase);

    // session key (the host's opener == a ClientContext *) -> provider -> setting -> value. Separate from
    // the global map above rather than a composite key so ClearSession is one removal, not a scan.
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, ConcurrentDictionary<string, string?>>>
        _bySession = new();

    private ConcurrentDictionary<string, string?> Bucket(string provider) =>
        _byProvider.GetOrAdd(provider, _ => new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Push a value (rendered string; null =&gt; unset/reset). Called by the host on <c>SET</c>/registration.</summary>
    public void Set(string provider, string name, string? value) => Bucket(provider)[name] = value;

    /// <summary>
    /// Push a value scoped to ONE DuckDB session (<paramref name="session"/> = the host opener). A session
    /// value shadows the global one for reads made on that session and nothing else.
    /// </summary>
    /// <remarks>⚠ <paramref name="session"/> 0 means "no session" and falls back to the GLOBAL slot — which is
    /// the correct behaviour for registration defaults and for a <c>SET GLOBAL</c>, and the reason the caller
    /// must be explicit about scope rather than letting a missing key silently become global.</remarks>
    public void SetForSession(long session, string provider, string name, string? value)
    {
        if (session == 0)
        {
            Set(provider, name, value);
            return;
        }
        _bySession
            .GetOrAdd(session, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, string?>>(
                                        StringComparer.OrdinalIgnoreCase))
            .GetOrAdd(provider, _ => new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
            [name] = value;
    }

    /// <summary>Drop every session-scoped value for a closed DuckDB connection. Idempotent.</summary>
    public void ClearSession(long session) => _bySession.TryRemove(session, out _);

    /// <summary>Number of sessions currently holding at least one scoped value (diagnostics/tests).</summary>
    public int SessionCount => _bySession.Count;

    /// <summary>
    /// The session in scope for reads on THIS flow, or 0 for none. An <see cref="System.Threading.AsyncLocal{T}"/>
    /// so it survives <c>await</c> and pool-thread hops, exactly like the host opener it mirrors.
    /// </summary>
    public static long CurrentSession
    {
        get => _currentSession.Value;
        set => _currentSession.Value = value;
    }

    private static readonly System.Threading.AsyncLocal<long> _currentSession = new();

    /// <summary>The raw string value for the session in scope, else the global one, else null.</summary>
    public string? GetString(string provider, string name)
    {
        long session = CurrentSession;
        if (session != 0 && _bySession.TryGetValue(session, out var forSession)
            && forSession.TryGetValue(provider, out var bucket)
            && bucket.TryGetValue(name, out var scoped))
        {
            return scoped;
        }
        return Bucket(provider).TryGetValue(name, out var v) ? v : null;
    }

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
