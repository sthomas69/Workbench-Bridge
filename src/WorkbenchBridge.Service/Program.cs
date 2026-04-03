using Serilog;
using WorkbenchBridge.Service;

// Configure Serilog with rolling file logging.
// Global log: logs/workbenchbridge.log (10MB rolling, 5 files retained)
// Per-port logs are written by the bridge instances when verbose/hexdump enabled.
var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "ESP32WorkbenchBridge", "logs", "workbenchbridge.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("WorkbenchBridge", Serilog.Events.LogEventLevel.Debug)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(logPath,
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB
        retainedFileCountLimit: 5,
        outputTemplate:
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Use Serilog as the logging provider
    builder.Services.AddSerilog();

    // Load bridge configuration
    builder.Services.Configure<BridgeConfig>(
        builder.Configuration.GetSection("Bridge"));

    // Register the bridge worker
    builder.Services.AddHostedService<BridgeWorker>();

    // Support running as a Windows service
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "ESP32WorkbenchBridge";
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
