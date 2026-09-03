// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// The bridge's implementations of the plugin-facing host services, and the one place they are published.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Every method reads the AMBIENT <c>ClientContext</c> per call and captures nothing.</b> That is the
/// rule these classes exist to keep in one place: a catalog is DATABASE-scoped and outlives the connection
/// that attached it, so a held <c>ClientContext*</c> dangles the day that connection closes — the
/// <c>table_stats</c> SIGSEGV class. It is also the more correct answer for SECRETS, which a user may create
/// after the ATTACH and per session.
/// </para>
/// <para>
/// These replace the <c>HostHttpTransport</c> / <c>HostQueryTransport</c> static-delegate seams. The seam
/// pattern was reached for three times in two weeks and a fourth was written and deleted the same day; the
/// locator is what stops there being a fifth. See docs/plugin-services.md.
/// </para>
/// </remarks>
internal static class HostServices
{
    /// <summary>
    /// Publishes the host services a plugin can resolve. Called from <c>Bootstrap.Initialize</c> once the
    /// host-services block is cached, and BEFORE any plugin is scanned — so a plugin resolving one at load
    /// time still finds it, though it should resolve lazily anyway (<see cref="FabricatorServices"/>).
    /// </summary>
    /// <remarks>
    /// ⚠ A capability the host did not register is NOT published, so <c>Get&lt;T&gt;()</c> answers null and
    /// <c>GetRequired&lt;T&gt;()</c> names the interface. Publishing an implementation that always throws
    /// would make "the host cannot do this" indistinguishable from "the call was wrong".
    /// </remarks>
    internal static void Publish()
    {
        // The host fills its whole services block at once, so these three flags move together in practice;
        // each is tested anyway so that an older host missing one capability loses only that service.
        if (HostFs.Available || HostFs.CanGlob)
        {
            FabricatorServices.Register<IHostFileSystem>(new HostFileSystemService());
        }
        if (HostFs.CanHttp)
        {
            FabricatorServices.Register<IHostHttp>(new HostHttpService());
        }
        if (HostFs.CanQuery)
        {
            FabricatorServices.Register<IHostQuery>(new HostQueryService());
        }
        // /!\ UNCONDITIONAL, unlike the three above, and the asymmetry is the point: logging needs no host
        // CAPABILITY. FabricatorLog always has somewhere to put an event (the FABRICATOR_LOG_FILE sink, or
        // nothing), and when the host_log callback is present it additionally reaches duckdb_logs. So there
        // is no flag to test and nothing to withhold - a plugin can always log, it just may log into the
        // void, which is the same deal the host's own categories get.
        FabricatorServices.Register<IHostLog>(new HostLogService());
    }
}

/// <summary>The host's logging, over <see cref="FabricatorLog"/>.</summary>
/// <remarks>
/// A thin adapter, and deliberately so: every routing decision (which providers, the minimum level, the
/// duckdb_logs forwarding) already lives in <see cref="FabricatorLog"/> and must keep living in ONE place, so
/// a plugin's events are filtered and sunk exactly like the host's own.
/// </remarks>
internal sealed class HostLogService : IHostLog
{
    public IHostLogger GetLogger(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new HostLoggerAdapter(FabricatorLog.CreateLogger(category));
    }
}

/// <summary>One category's logger, over an <c>ILogger</c>.</summary>
internal sealed class HostLoggerAdapter : IHostLogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _log;

    internal HostLoggerAdapter(Microsoft.Extensions.Logging.ILogger log) => _log = log;

    public bool IsEnabled(HostLogLevel level) => _log.IsEnabled(Map(level));

    public void Log(HostLogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        // The message is the format string and there are NO arguments, which is safe for a reason worth
        // recording because the obvious defensive version is unnecessary: MEL's FormattedLogValues builds a
        // LogValuesFormatter only when the argument array is non-empty, so a zero-argument call returns the
        // original string VERBATIM and never parses it. Braces in a message - a rendered Fluid template, a
        // JSON fragment - are therefore inert.
        // /!\ MEASURED, not assumed: this shipped for an hour as Log(_log, level, "{Message}", message) on
        // the theory that braces would otherwise be mangled, and the mutant that removes the indirection
        // SURVIVES the whole gate. The braces assertion in verify_plugin pins MEL's behaviour (a
        // characterization test), not ours.
        Microsoft.Extensions.Logging.LoggerExtensions.Log(_log, Map(level), message);
    }

    private static Microsoft.Extensions.Logging.LogLevel Map(HostLogLevel level) => level switch
    {
        HostLogLevel.Trace => Microsoft.Extensions.Logging.LogLevel.Trace,
        HostLogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
        HostLogLevel.Information => Microsoft.Extensions.Logging.LogLevel.Information,
        HostLogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
        HostLogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
        HostLogLevel.Critical => Microsoft.Extensions.Logging.LogLevel.Critical,
        // Refused BY VALUE rather than clamped: a level nobody declared is a caller bug, and silently
        // logging it as Information would hide the bug in the output it produced.
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "unknown host log level"),
    };
}

/// <summary>DuckDB's FileSystem, over <see cref="HostFs"/>.</summary>
internal sealed class HostFileSystemService : IHostFileSystem
{
    public byte[] ReadAllBytes(string path, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be > 0.");
        }
        return HostFs.ReadAllBytes(AmbientOpener.Current, path, maxBytes);
    }

    public IReadOnlyList<HostFileEntry> Glob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!HostFs.CanGlob)
        {
            throw new NotSupportedException("The host did not register fs_glob, so globbing is unavailable.");
        }
        var json = HostFs.Glob(AmbientOpener.Current, pattern);
        var result = new List<HostFileEntry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var path = el.GetProperty("path").GetString() ?? string.Empty;
            // -1, not 0, when absent: the local filesystem reports no size in a listing (DuckDB's FileSystem
            // has no path-stat), and a 0 there would read as "empty file".
            long size = el.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
            result.Add(new HostFileEntry(path, size));
        }
        return result;
    }
}

/// <summary>DuckDB's HTTP stack, over <see cref="HostFs"/>.</summary>
internal sealed class HostHttpService : IHostHttp
{
    public (string ResponseJson, byte[]? Body) Send(string method, string url, string? headersJson, byte[]? body) =>
        HostFs.HttpRequest(AmbientOpener.Current, method, url, headersJson, body);
}

/// <summary>SQL on the hosting DuckDB, over <see cref="Host"/>.</summary>
internal sealed class HostQueryService : IHostQuery
{
    public IArrowArrayStream Query(string sql, RecordBatch? parameters = null, bool inheritSession = true) =>
        // ⚠ The ambient ClientContext is passed as the caller's SESSION (ABI v83), so a plugin's query
        // resolves names and times the way the statement that reached it does. 0 = a clean session, which is
        // also what an absent ambient degrades to.
        Host.Query(sql, parameters, null, clientSession: inheritSession ? AmbientOpener.Current : 0);

    public long ExecuteNonQuery(string sql) => Host.ExecuteNonQuery(sql);


    // ⚠ The session is read HERE, at open, and NOT per query — which is what the ABI does too. The ambient
    // ClientContext is only valid for the duration of THIS crossing, so a pinned connection that tried to
    // re-read it per query would either dereference a dangling pointer or silently inherit whichever
    // operation happened to be running. Capturing once, on the caller's live context, is the same rule the
    // table function's CaptureSession follows.
    public IHostConnection OpenConnection(bool inheritSession = true) =>
        new PinnedHostConnection(Host.OpenConnection(inheritSession ? AmbientOpener.Current : 0));
}

/// <summary>Adapts <see cref="Host.HostConnection"/> to the plugin-facing <see cref="IHostConnection"/>.</summary>
internal sealed class PinnedHostConnection : IHostConnection
{
    private readonly Host.HostConnection _inner;

    internal PinnedHostConnection(Host.HostConnection inner)
    {
        _inner = inner;
    }

    public IArrowArrayStream Query(string sql, RecordBatch? parameters = null) => _inner.Query(sql, parameters);

    public long ExecuteNonQuery(string sql) => _inner.ExecuteNonQuery(sql);

    public void Dispose() => _inner.Dispose();
}
