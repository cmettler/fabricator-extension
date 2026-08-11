using System;
using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// Offline tests for the SESSION layer of <see cref="ProviderSettingsStore"/> — slice 1 of honouring DuckDB's
/// <c>SetScope</c>.
/// </summary>
/// <remarks>
/// <para><b>What this is for.</b> The store used to be purely process-wide and the host's set-callback
/// discarded <c>SetScope</c>, so a <c>SET</c> in one DuckDB connection was visible to every other one in the
/// process. MEASURED against SQL Server with the sharpest observable available: <c>SET mssql_mars='false'</c>
/// in connection A made a same-catalog CTAS in connection B — which set nothing — return <b>10</b> rows
/// instead of <b>15</b>. A setting applied in one connection changed the DATA another connection saw. The
/// control (same script, no <c>SET</c>) returned 15, so the <c>SET</c> was the only variable.</para>
/// <para>The practical consequence, and the reason it matters: configuring ONE dbt model with a pre-hook
/// cannot work — the value leaks to models running concurrently on other threads, and (with no scoping at
/// all) also persists to every later model even at <c>--threads 1</c>.</para>
/// <para>⚠ These tests pin the STORE only. The host still has to pass the session key and clear it when a
/// connection closes; until it does, nothing below is reachable from SQL.</para>
/// </remarks>
public class ProviderSettingsScopeTests
{
    private const string P = "testprov";

    // Each test gets its own store — CurrentSession is AsyncLocal and the singleton is process-wide, so
    // sharing one would make these order-dependent.
    private static ProviderSettingsStore New() => new();

    [Fact]
    public void A_global_value_is_seen_with_no_session_in_scope()
    {
        var s = New();
        ProviderSettingsStore.CurrentSession = 0;
        s.Set(P, "k", "global");
        Assert.Equal("global", s.GetString(P, "k"));
    }

    [Fact]
    public void A_session_value_shadows_the_global_one_for_that_session_only()
    {
        var s = New();
        s.Set(P, "k", "global");
        s.SetForSession(1, P, "k", "mine");

        ProviderSettingsStore.CurrentSession = 1;
        Assert.Equal("mine", s.GetString(P, "k"));

        // THE HEADLINE PROPERTY: another session is unaffected. This is the leak, in miniature.
        ProviderSettingsStore.CurrentSession = 2;
        Assert.Equal("global", s.GetString(P, "k"));

        ProviderSettingsStore.CurrentSession = 0;
        Assert.Equal("global", s.GetString(P, "k"));
    }

    [Fact]
    public void Two_sessions_hold_independent_values()
    {
        var s = New();
        s.SetForSession(1, P, "k", "one");
        s.SetForSession(2, P, "k", "two");

        ProviderSettingsStore.CurrentSession = 1;
        Assert.Equal("one", s.GetString(P, "k"));
        ProviderSettingsStore.CurrentSession = 2;
        Assert.Equal("two", s.GetString(P, "k"));
    }

    /// <summary>
    /// ⚠ Session 0 means "no session", and it must fall through to GLOBAL rather than create a phantom
    /// session — registration defaults and an explicit <c>SET GLOBAL</c> both arrive that way. Getting this
    /// wrong would put defaults somewhere no read could ever find them.
    /// </summary>
    [Fact]
    public void Session_zero_writes_and_reads_the_global_slot()
    {
        var s = New();
        s.SetForSession(0, P, "k", "viazero");
        Assert.Equal(0, s.SessionCount);

        ProviderSettingsStore.CurrentSession = 7;   // an unrelated session still sees it
        Assert.Equal("viazero", s.GetString(P, "k"));
    }

    /// <summary>A session value of null is an explicit UNSET for that session — it must not fall back to the
    /// global value, or a per-session RESET would silently restore whatever the global happened to be.</summary>
    [Fact]
    public void A_null_session_value_shadows_the_global_as_unset()
    {
        var s = New();
        s.Set(P, "k", "global");
        s.SetForSession(1, P, "k", null);

        ProviderSettingsStore.CurrentSession = 1;
        Assert.Null(s.GetString(P, "k"));
    }

    [Fact]
    public void ClearSession_drops_that_session_and_leaves_the_global_and_other_sessions()
    {
        var s = New();
        s.Set(P, "k", "global");
        s.SetForSession(1, P, "k", "one");
        s.SetForSession(2, P, "k", "two");
        Assert.Equal(2, s.SessionCount);

        s.ClearSession(1);
        Assert.Equal(1, s.SessionCount);

        ProviderSettingsStore.CurrentSession = 1;
        Assert.Equal("global", s.GetString(P, "k"));   // back to the global
        ProviderSettingsStore.CurrentSession = 2;
        Assert.Equal("two", s.GetString(P, "k"));      // untouched
    }

    /// <summary>⚠ The lifetime hook must be idempotent and safe for a session that never set anything — the
    /// host will call it on EVERY connection close, not only on ones that used a setting.</summary>
    [Fact]
    public void ClearSession_is_idempotent_and_safe_for_an_unknown_session()
    {
        var s = New();
        s.ClearSession(999);
        s.SetForSession(1, P, "k", "one");
        s.ClearSession(1);
        s.ClearSession(1);
        Assert.Equal(0, s.SessionCount);
    }

    // ---- the typed getters must resolve through the same layering, not just GetString -----------------

    [Fact]
    public void GetBool_and_GetLong_honour_the_session_layer()
    {
        var s = New();
        s.Set(P, "b", "true");
        s.Set(P, "n", "10");
        s.SetForSession(1, P, "b", "false");
        s.SetForSession(1, P, "n", "20");

        ProviderSettingsStore.CurrentSession = 1;
        Assert.False(s.GetBool(P, "b"));
        Assert.Equal(20L, s.GetLong(P, "n"));

        ProviderSettingsStore.CurrentSession = 2;
        Assert.True(s.GetBool(P, "b"));
        Assert.Equal(10L, s.GetLong(P, "n"));
    }

    /// <summary>Providers are namespaced within a session — one provider's session value must not answer
    /// another's read, or two backends sharing a setting NAME would cross-talk.</summary>
    [Fact]
    public void Providers_are_independent_within_a_session()
    {
        var s = New();
        s.SetForSession(1, "provA", "k", "a");
        ProviderSettingsStore.CurrentSession = 1;
        Assert.Equal("a", s.GetString("provA", "k"));
        Assert.Null(s.GetString("provB", "k"));
    }
}
