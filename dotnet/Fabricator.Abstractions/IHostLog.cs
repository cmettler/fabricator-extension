// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// Severity for <see cref="IHostLogger"/>. The numeric values ARE the <c>host_log</c> ABI contract
/// (0 Trace, 1 Debug, 2 Information, 3 Warning, 4 Error, 5 Critical) — do not renumber them.
/// </summary>
public enum HostLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>
/// The host's logging, resolvable from <see cref="FabricatorServices"/>. What a plugin wants from it is
/// reaching DuckDB's own <c>duckdb_logs</c> — which happens through the <c>host_log</c> reverse callback, so
/// it needs the RUNNING host and is therefore a SERVICE rather than something a plugin could reference.
/// </summary>
/// <remarks>
/// <para><b>⚠ Why it is a FACTORY rather than a single <c>Log(category, level, message)</c> method.</b> The
/// category is fixed per call site while the level check happens per event, often inside a per-batch loop.
/// A one-method interface would re-resolve the category on every event, and — worse — would make the cheap
/// <see cref="IHostLogger.IsEnabled"/> gate look expensive enough to skip. Resolve the logger once into a
/// field; call <c>IsEnabled</c> as often as you like.</para>
/// <para><b>⚠ It deliberately carries NO Microsoft.Extensions.Logging types.</b> That is the whole reason it
/// is an interface at all: <c>FabricatorLog</c>'s public surface is MEL (<c>ILogger</c>,
/// <c>ILoggerFactory</c>, <c>ILoggerProvider</c>), so moving IT into a plugin-referenceable assembly would
/// drag MEL into every plugin's compile closure inseparably. Primitive parameters keep it out; the cost is
/// that a caller interpolates its own message, which loses nothing because both sinks are message-string
/// based already (the host callback is <c>(int level, string category, string message)</c>).</para>
/// <para>See docs/plugin-services.md §7.4a (why a service, not a mover) and §10 (as built).</para>
/// </remarks>
public interface IHostLog
{
    /// <summary>
    /// A logger for one category. Categories are dotted names by convention (<c>"Fabricator.Delta"</c>,
    /// <c>"Fabricator.Memory"</c>); a plugin should use its own assembly name so its output is greppable and
    /// cannot be mistaken for the host's.
    /// </summary>
    IHostLogger GetLogger(string category);
}

/// <summary>One category's logger. Obtain from <see cref="IHostLog.GetLogger"/> and hold it.</summary>
public interface IHostLogger
{
    /// <summary>
    /// True when an event at this level would be recorded.
    /// <para><b>⚠ Check this before doing work that exists only to produce a message.</b> That is not
    /// stylistic advice: <c>MemoryProbe</c> reads <c>Environment.WorkingSet</c>, which queries OS process
    /// counters, and its marks sit in per-batch loops — computing the values before the check is the exact
    /// regression the gate exists to prevent.</para>
    /// </summary>
    bool IsEnabled(HostLogLevel level);

    /// <summary>
    /// Records one event. <paramref name="message"/> is taken VERBATIM — it is not a format template, so
    /// braces in it are safe and no argument substitution happens. That matters here because this surface's
    /// nearest neighbours produce braces routinely (a rendered Fluid template, a JSON fragment).
    /// <para>⚠ MEASURED, and recorded because the obvious "defensive" implementation is unnecessary: the
    /// bridge passes the message straight to <c>ILogger.Log</c> with NO arguments, and
    /// <c>FormattedLogValues</c> builds a format parser only when the argument array is non-empty — so the
    /// string is returned untouched and never parsed. Wrapping it as an argument to a fixed
    /// <c>"{Message}"</c> template adds nothing; a mutant removing that wrapper passed the entire gate,
    /// which is what retired it (docs/plugin-services.md §10.4).</para>
    /// </summary>
    void Log(HostLogLevel level, string message);
}
