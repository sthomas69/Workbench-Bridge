using System.Diagnostics;

namespace WorkbenchBridge.Service;

/// <summary>
/// Installs / removes the bridge as a Windows service. Both operations assume
/// the process is already elevated (Program.cs self-elevates first).
///
/// Install copies the exe and its config — crucially including the machine's
/// <c>appsettings.Local.json</c> slot overrides — into
/// <c>C:\Program Files\Workbench-Bridge\</c> and registers a delayed-auto
/// LocalSystem service from there (a stable path, so rebuilds of the source
/// tree don't break the installed service).
/// </summary>
internal static class ServiceInstaller
{
    // Service NAME stays "WorkbenchBridge" to match AddWindowsService + the IPC
    // pipe constant; only the human-facing DisplayName carries the hyphen.
    public const string ServiceName = "WorkbenchBridge";
    public const string DisplayName = "Workbench-Bridge";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Workbench-Bridge");

    public static int Install()
    {
        if (!ElevationHelper.IsElevated())
        {
            Console.Error.WriteLine("Install requires Administrator rights.");
            return 5;
        }

        string srcExe = Environment.ProcessPath!;
        string srcDir = Path.GetDirectoryName(srcExe)!;
        string exeName = Path.GetFileName(srcExe);
        string installedExe = Path.Combine(InstallDir, exeName);

        Console.WriteLine($"Installing {DisplayName} -> {InstallDir}");
        Directory.CreateDirectory(InstallDir);

        // Copy everything that sits next to the exe. This carries the runtime
        // (self-contained single-file = just the exe) AND the config files,
        // including appsettings.Local.json (your real SLOT1-7 overrides) so the
        // installed service runs your existing setup with zero reconfiguration.
        int copied = 0;
        bool sawLocal = false;
        foreach (var src in Directory.GetFiles(srcDir))
        {
            var name = Path.GetFileName(src);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(src, Path.Combine(InstallDir, name), overwrite: true);
            copied++;
            if (name.Equals("appsettings.Local.json", StringComparison.OrdinalIgnoreCase))
                sawLocal = true;
        }
        Console.WriteLine($"Copied {copied} file(s)." +
            (sawLocal
                ? " Your appsettings.Local.json was carried over."
                : " NOTE: no appsettings.Local.json found next to the exe — the " +
                  "service will start with example defaults until you add your slot config."));

        // Register (or update) the service. sc's binPath value must be a QUOTED
        // path (the install dir has a space), so the ImagePath in the registry is
        // stored quoted — hence the deliberate escaped inner quotes below.
        string quotedPath = $"\\\"{installedExe}\\\"";
        int rc = Sc($"create {ServiceName} binPath= \"{quotedPath}\" " +
                    $"start= delayed-auto obj= LocalSystem DisplayName= \"{DisplayName}\"");
        if (rc == 1073) // ERROR_SERVICE_EXISTS
        {
            Console.WriteLine("Service already exists — updating its binary path.");
            rc = Sc($"config {ServiceName} binPath= \"{quotedPath}\" start= delayed-auto");
        }
        if (rc != 0)
        {
            Console.Error.WriteLine($"sc failed (exit {rc}). Service not registered.");
            return rc;
        }

        Sc($"description {ServiceName} \"Workbench-Bridge: presents ESP32 devices on a " +
           "Universal-ESP32-Workbench (Raspberry Pi) as local Windows COM ports over RFC2217, " +
           "so any IDE/esptool flashes and monitors them as if plugged in directly.\"");

        // Recovery actions. Windows' DEFAULT for a new service is no recovery at
        // all, so set it explicitly: restart on the 1st and 2nd failure (60s
        // apart); take NO action on the 3rd — if it's still failing then
        // something is genuinely wrong, so stop flapping and leave it for a
        // human. reset= 86400 clears the failure count after a day of health.
        Sc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000//0");

        Console.WriteLine();
        Console.WriteLine($"Installed. Service '{ServiceName}' ({DisplayName}): delayed-auto, LocalSystem,");
        Console.WriteLine($"  recovery = restart, restart, then none. Has a description in services.msc.");
        Console.WriteLine($"  Start now:  sc start {ServiceName}      (or it starts on next boot)");
        Console.WriteLine($"  Manage:     Workbench-Bridge.Cli.exe status");
        Console.WriteLine($"  Remove:     Workbench-Bridge.Service.exe --uninstall");
        return 0;
    }

    public static int Uninstall()
    {
        if (!ElevationHelper.IsElevated())
        {
            Console.Error.WriteLine("Uninstall requires Administrator rights.");
            return 5;
        }

        Console.WriteLine($"Stopping and removing service '{ServiceName}'...");
        Sc($"stop {ServiceName}");          // ignore result (may already be stopped)
        int rc = Sc($"delete {ServiceName}");
        if (rc != 0 && rc != 1060) // 1060 = ERROR_SERVICE_DOES_NOT_EXIST
        {
            Console.Error.WriteLine($"sc delete failed (exit {rc}).");
            return rc;
        }

        Console.WriteLine($"Service removed. Files left in place: {InstallDir}");
        Console.WriteLine("(delete that folder by hand if you want them gone — your " +
            "appsettings.Local.json is there.)");
        return 0;
    }

    /// <summary>Run sc.exe with the given argument string; return its exit code.</summary>
    private static int Sc(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (!string.IsNullOrWhiteSpace(outp)) Console.Write(outp);
        if (!string.IsNullOrWhiteSpace(err)) Console.Error.Write(err);
        return p.ExitCode;
    }
}
