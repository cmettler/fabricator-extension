// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fabricator.Bridge;

/// <summary>
/// Central .NET-logging entry point for the managed bridge. Lets any component obtain an <see cref="ILogger"/>
/// to trace what actually reaches the provider — the native Delta reader's per-file queries, the applied
/// static/dynamic filters, pruned files, the resolved snapshot version, etc.
///
/// <para><b>Off by default</b> (a <see cref="NullLoggerFactory"/> — zero overhead). Enable a file sink via env:
/// <c>FABRICATOR_LOG_LEVEL</c> (Trace|Debug|Information|Warning|Error|None; default None) + optional
/// <c>FABRICATOR_LOG_FILE</c> (path; default <c>%TEMP%/fabricator.log</c> or <c>/tmp/fabricator.log</c>). Because the
/// CLR is hosted by hostfxr inside DuckDB there is no reliable console, so the default sink is a file.</para>
///
/// <para>The <see cref="Factory"/> is <b>pluggable</b>: the host can replace it or add a provider (e.g. a future
/// provider that forwards to DuckDB's internal logging via a host callback) with <see cref="AddProvider"/> —
/// so the same <c>ILogger</c> call sites route wherever configured.</para>
/// </summary>
public static class FabricatorLog
{
    private static readonly object Gate = new();
    private static ILoggerFactory _factory = BuildDefault();

    /// <summary>The active logger factory. Assign a custom factory (or null to disable) to reroute all logging.</summary>
    public static ILoggerFactory Factory
    {
        get { lock (Gate) { return _factory; } }
        set { lock (Gate) { _factory = value ?? NullLoggerFactory.Instance; } }
    }

    /// <summary>A logger for the given category (e.g. <c>"Fabricator.Delta"</c>).</summary>
    public static ILogger CreateLogger(string category)
    {
        lock (Gate) { return _factory.CreateLogger(category); }
    }

    /// <summary>Attaches an additional <see cref="ILoggerProvider"/> to the active factory (e.g. a
    /// DuckDB-forwarding provider). No-op on a <see cref="NullLoggerFactory"/>.</summary>
    public static void AddProvider(ILoggerProvider provider)
    {
        lock (Gate) { _factory.AddProvider(provider); }
    }

    /// <summary>
    /// Wires ILogger output to DuckDB's own internal logging: <paramref name="sink"/> is a
    /// <c>(level, category, message)</c> delegate the host sets once (from <c>Bootstrap.Initialize</c>) over the
    /// <c>host_log</c> reverse-callback, so events also land in DuckDB's <c>duckdb_logs</c>. The <b>C# seam is
    /// ready now</b>; it stays inert until the host callback is added (a small additive ABI step — see
    /// docs/multifile-delta.md §"Batch 2"). Idempotent; forwards regardless of the file-sink env config (the two
    /// sinks are independent), so a caller can enable DuckDB forwarding even with logging otherwise "off".
    /// </summary>
    public static void EnableHostForwarding(Action<int, string, string> sink)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_factory, NullLoggerFactory.Instance))
            {
                // Nothing routes through NullLoggerFactory — promote to a real (empty) factory so the forwarding
                // provider actually receives events even when FABRICATOR_LOG_LEVEL was unset.
                _factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));
            }
            _factory.AddProvider(new HostForwardingLoggerProvider(sink));
        }
    }

    /// <summary>The integer log levels crossing to the host <c>host_log</c> callback (stable ABI contract):
    /// 0 Trace, 1 Debug, 2 Information, 3 Warning, 4 Error, 5 Critical.</summary>
    internal static int LevelCode(LogLevel l) => l switch
    {
        LogLevel.Trace => 0,
        LogLevel.Debug => 1,
        LogLevel.Information => 2,
        LogLevel.Warning => 3,
        LogLevel.Error => 4,
        LogLevel.Critical => 5,
        _ => 2,
    };

    private static ILoggerFactory BuildDefault()
    {
        var levelText = Environment.GetEnvironmentVariable("FABRICATOR_LOG_LEVEL");
        if (string.IsNullOrWhiteSpace(levelText) ||
            string.Equals(levelText, "none", StringComparison.OrdinalIgnoreCase) ||
            !Enum.TryParse<LogLevel>(levelText, ignoreCase: true, out var level) ||
            level == LogLevel.None)
        {
            return NullLoggerFactory.Instance; // disabled — no allocation, no file
        }

        var path = Environment.GetEnvironmentVariable("FABRICATOR_LOG_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Path.GetTempPath(), "fabricator.log");
        }

        try
        {
            return LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(level);
                builder.AddProvider(new FileLoggerProvider(path!, level));
            });
        }
        catch
        {
            return NullLoggerFactory.Instance; // an unwritable path must never break the extension
        }
    }
}

/// <summary>A minimal thread-safe file <see cref="ILoggerProvider"/> — the CLR is hosted (no console), so a file
/// is the useful default sink. One line per event: <c>ISO-8601Z LEVEL [category] message</c>. Appends; best-effort
/// (a write failure is swallowed so logging can never take down the extension).</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly LogLevel _min;
    private readonly object _writeGate = new();

    public FileLoggerProvider(string path, LogLevel min)
    {
        _path = path;
        _min = min;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_writeGate)
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); }
            catch { /* best-effort: never let logging fault the extension */ }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _owner._min && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var sb = new StringBuilder(160);
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
              .Append(' ').Append(Level(logLevel))
              .Append(" [").Append(_category).Append("] ")
              .Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                // At Debug/Trace ONLY, append the inner-exception chain and the STACK TRACE. The message alone
                // names WHAT failed but never WHERE, and "which of our operations produced this provider error?"
                // is the question that actually costs time: a OneLake commit conflict arrives as a generic Azure
                // RequestFailedException, and identifying the call site by reading code instead of reading a
                // trace is how a conditional-create status mapping stayed wrong (see
                // AdlsGen2TableFileSystem.CreateAsync). Kept off at Warning/Information so the normal sink
                // stays one line per event.
                if (_owner._min <= LogLevel.Debug)
                {
                    for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
                    {
                        sb.Append("\n    caused by ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                    }
                    if (exception.StackTrace is { Length: > 0 } trace)
                    {
                        sb.Append('\n').Append(trace);
                    }
                }
            }
            sb.Append('\n');
            _owner.Write(sb.ToString());
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "?????",
        };
    }
}

/// <summary>Forwards ILogger events to DuckDB's internal logging via a host <c>(level, category, message)</c>
/// delegate (the <c>host_log</c> reverse-callback, set by <see cref="FabricatorLog.EnableHostForwarding"/>). The
/// managed seam is complete; wiring the host callback is a small additive ABI step.</summary>
internal sealed class HostForwardingLoggerProvider : ILoggerProvider
{
    private readonly Action<int, string, string> _sink;

    public HostForwardingLoggerProvider(Action<int, string, string> sink) => _sink = sink;

    public ILogger CreateLogger(string categoryName) => new Fwd(_sink, categoryName);

    public void Dispose() { }

    private sealed class Fwd : ILogger
    {
        private readonly Action<int, string, string> _sink;
        private readonly string _category;

        public Fwd(Action<int, string, string> sink, string category)
        {
            _sink = sink;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None)
            {
                return;
            }
            var msg = formatter(state, exception);
            if (exception is not null)
            {
                msg += " | " + exception.GetType().Name + ": " + exception.Message;
            }
            try { _sink(FabricatorLog.LevelCode(logLevel), _category, msg); }
            catch { /* forwarding must never fault the extension */ }
        }
    }
}
