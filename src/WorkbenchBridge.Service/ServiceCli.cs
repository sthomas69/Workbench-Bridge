using System.Reflection;

namespace WorkbenchBridge.Service;

/// <summary>Arg parsing + help/version for the service exe's CLI front-door.</summary>
internal static class ServiceCli
{
    /// <summary>
    /// True if any token equals one of <paramref name="forms"/> (case-insensitive).
    /// Accepts Windows (<c>/x</c>, <c>/xxx</c>) and Unix (<c>-x</c>, <c>--xxx</c>)
    /// styles equally — the caller lists every spelling it wants to honour.
    /// </summary>
    public static bool HasFlag(string[] args, params string[] forms) =>
        args.Any(a => forms.Any(f => a.Equals(f, StringComparison.OrdinalIgnoreCase)));

    public static int PrintVersion()
    {
        var v = Assembly.GetEntryAssembly()?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? "unknown";
        Console.WriteLine($"Workbench-Bridge service {v}");
        return 0;
    }

    public static int PrintHelp()
    {
        Console.WriteLine(
@"Workbench-Bridge service  (Workbench-Bridge.Service.exe)

Bridges local Windows COM ports to ESP32 devices on a Universal-ESP32-Workbench
(Raspberry Pi) over RFC2217, so any IDE / esptool flashes them as if local.

USAGE
  Workbench-Bridge.Service.exe              run the bridge in a console window
                                            (prompts for admin, shows live logs)
  Workbench-Bridge.Service.exe --install    install + register as a Windows service
                                            (copies to C:\Program Files\Workbench-Bridge\
                                             and brings your appsettings.Local.json)
  Workbench-Bridge.Service.exe --uninstall  stop and remove the Windows service
  Workbench-Bridge.Service.exe --help       this help            (no admin needed)
  Workbench-Bridge.Service.exe --version    version              (no admin needed)

  Flags accept Windows or Unix styles: --install /install, --help -h -? /?, --version -v.

NOTES
  * No arguments -> foreground console app (the default). It elevates via UAC
    because com0com + COM ports need admin. Ctrl-C to stop.
  * The Windows Service Control Manager runs this same exe headless as the
    'WorkbenchBridge' service.
  * Manage a running bridge with the CLI:
        Workbench-Bridge.Cli.exe status | set | flash | logs
  * Logs live in  C:\ProgramData\Workbench-Bridge\logs
    (see 'Workbench-Bridge.Cli.exe logs --where' / '--clear').");
        return 0;
    }
}
