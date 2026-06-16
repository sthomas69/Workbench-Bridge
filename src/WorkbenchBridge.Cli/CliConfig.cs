using Microsoft.Extensions.Configuration;
using WorkbenchBridge.Service;

namespace WorkbenchBridge.Cli;

/// <summary>
/// Loads the bridge configuration the same way the service binds it - public
/// defaults from appsettings.json layered with the per-machine
/// appsettings.Local.json - so direct CLI commands (<c>flash</c>,
/// <c>device-info</c>) can resolve the Pi host/port and the esptool location
/// without going through the service. Always returns a usable config: any file
/// that is missing or unparsable is skipped and the bound defaults apply.
/// </summary>
internal static class CliConfig
{
    public static BridgeConfig Load()
    {
        var builder = new ConfigurationBuilder();
        foreach (var path in CandidatePaths())
        {
            if (File.Exists(path))
            {
                try { builder.AddJsonFile(path, optional: true); }
                catch { /* skip an unreadable layer; later layers/defaults still apply */ }
            }
        }

        var conf = builder.Build();
        var bridge = new BridgeConfig();
        conf.GetSection("Bridge").Bind(bridge);
        return bridge;
    }

    /// <summary>
    /// Config layers in precedence order (earliest added = lowest precedence).
    /// appsettings.json holds defaults; the Local.json files override it. The
    /// in-source-tree Local.json is dev-canonical; the copy next to the exe is
    /// the deployed one.
    /// </summary>
    private static IEnumerable<string> CandidatePaths()
    {
        string cliDir = AppContext.BaseDirectory;

        // 1. Public defaults next to the exe (Pi.Host, Tools.EsptoolPath, ...).
        yield return Path.Combine(cliDir, "appsettings.json");

        // 2. In-source-tree Local.json (gitignored, dev-canonical):
        //    <repo>/src/WorkbenchBridge.Service/appsettings.Local.json
        var srcDir = new DirectoryInfo(cliDir).Parent?.Parent?.Parent?.Parent?.Parent;
        if (srcDir is not null)
            yield return Path.Combine(
                srcDir.FullName, "WorkbenchBridge.Service", "appsettings.Local.json");

        // 3. A copy of Local.json next to the exe (deployed).
        yield return Path.Combine(cliDir, "appsettings.Local.json");
    }
}
