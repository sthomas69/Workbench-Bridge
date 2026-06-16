using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WorkbenchBridge.Ipc;
using WorkbenchBridge.Service;

namespace WorkbenchBridge.Cli;

/// <summary>
/// Applies a Local.json config to the machine - WITHOUT requiring elevation.
///
/// The CLI (running in the user's non-elevated context) only:
///   1. Locates and reads the user's appsettings.Local.json.
///   2. Validates it parses and has at least one ComPortMapping.
///   3. Sends the raw JSON to the WorkbenchBridge service over the named pipe.
///
/// The SERVICE (running as LocalSystem) does all the privileged work: seeding
/// com0com pairs via setupc.exe, renaming them via the registry + pnputil,
/// releasing ComDB reservations, normalising EmuBR/EmuOverrun, persisting the
/// config, and restarting its own bridges. See BridgeWorker.HandleConfigureAsync
/// and Provisioner. The service never stops itself; only the com0com pairs are
/// reloaded (per-device, via pnputil), so there is no chicken-and-egg problem.
/// </summary>
public static class ConfigureCommand
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory)
    {
        await Task.Yield();

        string sourcePath = GetFlag(args, "--source");
        bool dryRun = HasFlag(args, "--dry-run");

        string cliDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(sourcePath))
            sourcePath = ResolveDefaultSourcePath(cliDir);

        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Source config not found: {sourcePath}");
            Console.Error.WriteLine(
                "Pass --source <path> or create the file from appsettings.Local.json.example.");
            return 1;
        }

        string localJson;
        try
        {
            localJson = await File.ReadAllTextAsync(sourcePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read {sourcePath}: {ex.Message}");
            return 1;
        }

        // Validate locally so the user gets a clear error before we round-trip to
        // the service. The service re-parses authoritatively.
        var config = LoadConfig(localJson, sourcePath);
        if (config is null) return 1;
        if (config.ComPortMapping.Count == 0)
        {
            Console.Error.WriteLine($"{sourcePath} has no Bridge.ComPortMapping entries.");
            return 1;
        }

        Console.WriteLine($"Source config:  {sourcePath}");
        PrintMappings(config);
        Console.WriteLine(dryRun
            ? "Sending dry-run request to the WorkbenchBridge service..."
            : "Sending configure request to the WorkbenchBridge service...");
        Console.WriteLine();

        // Connect timeout is short (the service answers the pipe immediately);
        // the provisioning work itself can take a while and is awaited on the
        // response read, which has no timeout.
        using var client = new IpcClient(timeoutMs: 30_000);
        var request = new IpcRequest
        {
            Command = IpcCommand.Configure,
            Params = JsonSerializer.SerializeToElement(new ConfigureParams
            {
                LocalJson = localJson,
                DryRun = dryRun
            })
        };

        IpcResponse response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(
                "The configure command applies changes through the running service. " +
                "Start the WorkbenchBridge service and try again.");
            return 1;
        }

        if (response.Data is not null)
        {
            var result = response.Data.Value.Deserialize<ConfigureResult>();
            if (result is not null)
                foreach (var line in result.Log)
                    Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine(response.Message ?? (response.Success ? "Done." : "Failed."));
        return response.Success ? 0 : 1;
    }

    // -------------------------------------------------------------

    private static BridgeConfig? LoadConfig(string json, string path)
    {
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var conf = new ConfigurationBuilder()
                .AddJsonStream(stream)
                .Build();
            var bridge = new BridgeConfig();
            conf.GetSection("Bridge").Bind(bridge);
            return bridge;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse {path}: {ex.Message}");
            return null;
        }
    }

    private static void PrintMappings(BridgeConfig config)
    {
        Console.WriteLine($"Applying {config.ComPortMapping.Count} ComPortMapping entry(ies):");
        Console.WriteLine($"  {"Slot",-6} {"TCP",-5} {"User",-22} {"Internal",-22}");
        foreach (var m in config.ComPortMapping)
        {
            Console.WriteLine(
                $"  {m.SlotLabel,-6} {m.PiTcpPort,-5} " +
                $"{m.User.RegistryKey + " -> " + m.User.PortName,-22} " +
                $"{m.Internal.RegistryKey + " -> " + m.Internal.PortName,-22}");
        }
        Console.WriteLine();
    }

    private static string ResolveDefaultSourcePath(string cliDir)
    {
        // Try the in-source-tree location first (gitignored, dev-canonical).
        var dir = new DirectoryInfo(cliDir);
        var binDir = dir.Parent?.Parent?.Parent;
        var projectDir = binDir?.Parent;
        var srcDir = projectDir?.Parent;
        if (srcDir is not null)
        {
            var candidate = Path.Combine(
                srcDir.FullName, "WorkbenchBridge.Service", "appsettings.Local.json");
            if (File.Exists(candidate)) return candidate;
        }
        // Fall back to a copy next to the CLI exe.
        return Path.Combine(cliDir, "appsettings.Local.json");
    }

    private static string GetFlag(string[] args, string name, string defaultValue = "")
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return defaultValue;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}
