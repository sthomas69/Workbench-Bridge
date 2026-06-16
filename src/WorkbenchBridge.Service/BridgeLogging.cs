using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace WorkbenchBridge.Service;

/// <summary>
/// Single source of truth for WHERE WorkbenchBridge writes its log files and HOW
/// the Serilog sinks are configured. Both the service host (the writer) and the
/// CLI <c>logs</c> command (the reader/clearer) resolve through here so the two
/// can never drift apart.
///
/// Configured once at process start from <see cref="LoggingConfig"/> (bound from
/// <c>Bridge:Logging</c> in appsettings.json), so the service and the CLI read the
/// identical directory/cadence/retention. Three clearly-named streams live under
/// <see cref="LogDirectory"/>:
/// <list type="bullet">
///   <item><c>service-&lt;period&gt;.log</c> — service controller / generic comms &amp; control.</item>
///   <item><c>cli-&lt;period&gt;.log</c>     — CLI command invocations.</item>
///   <item><c>port-COMxx-&lt;period&gt;.log</c> — one file per bridged COM port (its serial
///         traffic, DTR/RTS, reset translation, and <c>--raw</c> capture).</item>
/// </list>
/// Files roll hourly by default and are purged after <see cref="LoggingConfig.RetentionDays"/>.
/// </summary>
public static class BridgeLogging
{
    private static LoggingConfig _config = new();

    /// <summary>Glob matching every WorkbenchBridge log file in the directory.</summary>
    public const string FileSearchPattern = "*.log";

    /// <summary>The active configuration (defaults until <see cref="Configure"/> runs).</summary>
    public static LoggingConfig Config => _config;

    /// <summary>Directory holding all log files.</summary>
    public static string LogDirectory => _config.Directory;

    /// <summary>
    /// Apply configuration (call once at startup, from the service host and the CLI).
    /// Creates the log directory so the first write/list never fails on a missing path.
    /// </summary>
    public static void Configure(LoggingConfig? config)
    {
        if (config is not null) _config = config;
        try { System.IO.Directory.CreateDirectory(_config.Directory); } catch { /* best effort */ }
    }

    private static RollingInterval Roll() =>
        _config.RollingInterval.Trim().ToLowerInvariant() switch
        {
            "day" => RollingInterval.Day,
            "minute" => RollingInterval.Minute,
            _ => RollingInterval.Hour,
        };

    private static long Mb(int mb) => (long)Math.Max(1, mb) * 1024 * 1024;

    // Stem passed to Serilog; it inserts the rolling period before ".log"
    // (e.g. "service-" -> "service-2026060814.log" for the Hour interval).
    private static string Path(string stem) => System.IO.Path.Combine(_config.Directory, stem + ".log");

    /// <summary>Build the service host logger: console + rolling <c>service-*.log</c>.</summary>
    public static Serilog.Core.Logger BuildServiceLogger() => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("WorkbenchBridge", LogEventLevel.Debug)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(Path("service-"), rollingInterval: Roll(),
            fileSizeLimitBytes: Mb(_config.ControllerFileSizeMB), rollOnFileSizeLimit: true,
            retainedFileCountLimit: null, shared: true,
            outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    /// <summary>Build the CLI logger: console + rolling <c>cli-*.log</c>.</summary>
    public static Serilog.Core.Logger BuildCliLogger() => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("WorkbenchBridge", LogEventLevel.Debug)
        .Enrich.FromLogContext()
        .WriteTo.File(Path("cli-"), rollingInterval: Roll(),
            fileSizeLimitBytes: Mb(_config.ControllerFileSizeMB), rollOnFileSizeLimit: true,
            retainedFileCountLimit: null, shared: true,
            outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    /// <summary>
    /// Create an <see cref="ILoggerFactory"/> that routes a single bridge's logs to its
    /// own <c>port-COMxx-*.log</c> file (small size cap so a busy <c>--raw</c> capture
    /// rolls often). The caller disposes it when the bridge stops. File-only — serial
    /// traffic does not spam the service console.
    /// </summary>
    public static ILoggerFactory CreatePortLoggerFactory(string portName)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(Path($"port-{Sanitize(portName)}-"), rollingInterval: Roll(),
                fileSizeLimitBytes: Mb(_config.PortFileSizeMB), rollOnFileSizeLimit: true,
                retainedFileCountLimit: null, shared: true,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    /// <summary>Make a COM/port name safe for a file name (e.g. "COM41").</summary>
    private static string Sanitize(string name)
    {
        Span<char> buf = stackalloc char[name.Length];
        int n = 0;
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) buf[n++] = c;
        return n == 0 ? "port" : new string(buf[..n]);
    }

    // -----------------------------------------------------------------
    // Retention
    // -----------------------------------------------------------------

    /// <summary>
    /// Delete every <c>*.log</c> in <see cref="LogDirectory"/> last written more than
    /// <see cref="LoggingConfig.RetentionDays"/> ago. Best effort — a file the running
    /// service still holds open is skipped silently and purged on a later pass.
    /// Returns the number of files removed.
    /// </summary>
    public static int PurgeOldLogs(DateTime nowUtc)
    {
        int removed = 0;
        if (!System.IO.Directory.Exists(_config.Directory)) return 0;
        foreach (var f in System.IO.Directory.GetFiles(_config.Directory, FileSearchPattern))
        {
            try
            {
                if (IsExpired(File.GetLastWriteTimeUtc(f), nowUtc, _config.RetentionDays))
                {
                    File.Delete(f);
                    removed++;
                }
            }
            catch { /* locked / vanished — try again next sweep */ }
        }
        return removed;
    }

    /// <summary>Pure predicate (unit-testable): is a file this old expired?</summary>
    public static bool IsExpired(DateTime lastWriteUtc, DateTime nowUtc, int retentionDays) =>
        retentionDays > 0 && lastWriteUtc < nowUtc.AddDays(-retentionDays);
}
