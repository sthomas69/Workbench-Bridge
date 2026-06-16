using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using WorkbenchBridge.Service;

// ---- Entry dispatch (before the host) --------------------------------------
// The exe ships asInvoker (no forced UAC); we self-elevate only when needed.
// --help / --version answer immediately and never elevate.
if (ServiceCli.HasFlag(args, "--help", "-h", "-?", "/?", "/h", "/help", "help"))
    return ServiceCli.PrintHelp();
if (ServiceCli.HasFlag(args, "--version", "-v", "/v", "/version", "version"))
    return ServiceCli.PrintVersion();

bool launchedByScm = WindowsServiceHelpers.IsWindowsService();

if (ServiceCli.HasFlag(args, "--install", "/install", "install"))
    return ElevationHelper.IsElevated()
        ? ServiceInstaller.Install()
        : ElevationHelper.RelaunchElevated(args, wait: true);

if (ServiceCli.HasFlag(args, "--uninstall", "/uninstall", "uninstall"))
    return ElevationHelper.IsElevated()
        ? ServiceInstaller.Uninstall()
        : ElevationHelper.RelaunchElevated(args, wait: true);

// No verb -> run the bridge. Started interactively (not by the SCM) without
// admin, relaunch elevated so com0com + COM ports work: the standard console start.
if (!launchedByScm && !ElevationHelper.IsElevated())
    return ElevationHelper.RelaunchElevated(args, wait: false);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Resolve appsettings.* next to the exe, NOT the process working directory.
    // A Windows service starts with CWD = C:\Windows\System32, so the default
    // content-root lookup would miss the config and fall back to example slots.
    ContentRootPath = AppContext.BaseDirectory,
});

// Layer machine-specific overrides on top of the public defaults. appsettings.json
// ships safe defaults (incl. Bridge:Logging); appsettings.Local.json is gitignored
// and holds the real per-machine slot config.
builder.Configuration.AddJsonFile("appsettings.Local.json",
    optional: true, reloadOnChange: true);

// File logging: directory / roll cadence / retention all come from Bridge:Logging
// in appsettings.json. Configure BridgeLogging BEFORE building the logger so the
// service (writer) and the CLI (reader/clearer) resolve the identical location.
var logCfg = builder.Configuration.GetSection("Bridge:Logging").Get<LoggingConfig>()
             ?? new LoggingConfig();
BridgeLogging.Configure(logCfg);
Log.Logger = BridgeLogging.BuildServiceLogger();

try
{
    Log.Information(
        "Logging to {Dir}  (service-*.log, cli-*.log, port-COMxx-*.log; roll {Roll}, keep {Days}d)",
        BridgeLogging.LogDirectory, logCfg.RollingInterval, logCfg.RetentionDays);

    // Sweep stale logs on startup; the worker also sweeps periodically.
    int purged = BridgeLogging.PurgeOldLogs(DateTime.UtcNow);
    if (purged > 0) Log.Information("Purged {Count} log file(s) older than {Days}d", purged, logCfg.RetentionDays);

    // Use Serilog as the logging provider (reads the static Log.Logger dynamically).
    builder.Services.AddSerilog();

    // Load bridge configuration.
    builder.Services.Configure<BridgeConfig>(
        builder.Configuration.GetSection("Bridge"));

    // Register the bridge worker.
    builder.Services.AddHostedService<BridgeWorker>();

    // Support running as a Windows service. Name must match the installed SCM entry;
    // the installer / `sc create` registers this as "WorkbenchBridge".
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "WorkbenchBridge";
    });

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

return 0;
