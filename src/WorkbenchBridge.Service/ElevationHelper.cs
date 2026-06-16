using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WorkbenchBridge.Service;

/// <summary>
/// UAC helpers. The exe ships with an <c>asInvoker</c> manifest (no forced
/// prompt on launch), so it elevates itself only when an operation actually
/// needs admin rights — running the bridge console (com0com + COM ports) or
/// installing/removing the service. <c>--help</c> / <c>--version</c> never elevate.
/// </summary>
internal static class ElevationHelper
{
    /// <summary>True if the current process is running with Administrator rights.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Relaunch this exe elevated (a fresh process behind a UAC prompt) with the
    /// same args. <paramref name="wait"/> = true for one-shot operations
    /// (--install/--uninstall) so we return the child's exit code; false for the
    /// long-running console run so the original (un-elevated) process exits
    /// immediately and only the elevated console window remains.
    /// </summary>
    public static int RelaunchElevated(string[] args, bool wait)
    {
        string exe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule!.FileName;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,   // required for the "runas" verb
            Verb = "runas",           // triggers the UAC elevation prompt
            Arguments = string.Join(' ',
                args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };

        try
        {
            var p = Process.Start(psi);
            if (p is null) return 1;
            if (!wait) return 0;       // console run: let the elevated window take over
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception)
        {
            // User dismissed / declined the UAC prompt.
            Console.Error.WriteLine(
                "This action needs Administrator rights, which were not granted.");
            return 5; // ERROR_ACCESS_DENIED
        }
    }
}
