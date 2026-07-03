using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArrowNet.Bridge;

/// <summary>
/// Central .NET-logging entry point for the managed bridge. Lets any component obtain an <see cref="ILogger"/>
/// to trace what actually reaches the provider — the native Delta reader's per-file queries, the applied
/// static/dynamic filters, pruned files, the resolved snapshot version, etc.
///
/// <para><b>Off by default</b> (a <see cref="NullLoggerFactory"/> — zero overhead). Enable a file sink via env:
/// <c>ARROWNET_LOG_LEVEL</c> (Trace|Debug|Information|Warning|Error|None; default None) + optional
/// <c>ARROWNET_LOG_FILE</c> (path; default <c>%TEMP%/arrownet.log</c> or <c>/tmp/arrownet.log</c>). Because the
/// CLR is hosted by hostfxr inside DuckDB there is no reliable console, so the default sink is a file.</para>
///
/// <para>The <see cref="Factory"/> is <b>pluggable</b>: the host can replace it or add a provider (e.g. a future
/// provider that forwards to DuckDB's internal logging via a host callback) with <see cref="AddProvider"/> —
/// so the same <c>ILogger</c> call sites route wherever configured.</para>
/// </summary>
public static class ArrowNetLog
{
    private static readonly object Gate = new();
    private static ILoggerFactory _factory = BuildDefault();

    /// <summary>The active logger factory. Assign a custom factory (or null to disable) to reroute all logging.</summary>
    public static ILoggerFactory Factory
    {
        get { lock (Gate) { return _factory; } }
        set { lock (Gate) { _factory = value ?? NullLoggerFactory.Instance; } }
    }

    /// <summary>A logger for the given category (e.g. <c>"ArrowNet.Delta"</c>).</summary>
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

    private static ILoggerFactory BuildDefault()
    {
        var levelText = Environment.GetEnvironmentVariable("ARROWNET_LOG_LEVEL");
        if (string.IsNullOrWhiteSpace(levelText) ||
            string.Equals(levelText, "none", StringComparison.OrdinalIgnoreCase) ||
            !Enum.TryParse<LogLevel>(levelText, ignoreCase: true, out var level) ||
            level == LogLevel.None)
        {
            return NullLoggerFactory.Instance; // disabled — no allocation, no file
        }

        var path = Environment.GetEnvironmentVariable("ARROWNET_LOG_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Path.GetTempPath(), "arrownet.log");
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
