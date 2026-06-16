using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkbenchBridge.Ipc;
using WorkbenchBridge.Service;

namespace WorkbenchBridge.Cli;

/// <summary>
/// <c>flash &lt;port&gt; &lt;binary&gt;</c>: flash a firmware image to a bridged slot
/// with esptool, sequencing the reset according to the slot's reset profile.
///
/// The reset profile is read from the running service (over IPC), which mirrors
/// it from the Pi portal. esptool itself runs locally and talks to the slot's
/// USER COM port (com0com -> internal port -> RFC 2217 bridge -> Pi -> board):
///
///   * "classic" / "uart-bridge"  esptool drives DTR/RTS reset over the bridge
///                                 (--before default-reset). Works for CP2102/
///                                 CH340 USB-UART boards.
///   * "native-usb" / "usb-jtag"  the board re-enumerates its USB on a bootloader
///                                 reset, so esptool cannot reset it over the
///                                 bridge (--before no-reset). If the slot has
///                                 BOOT/EN wired to Pi GPIO (or is otherwise
///                                 download-mode-capable), we ask the Pi to enter
///                                 download mode first; otherwise we prompt the
///                                 user to hold BOOT and tap RESET by hand.
///
/// Designed to degrade gracefully: on an old portal (no reset profile) it falls
/// back to a classic UART reset, which is the safe default for wired boards.
/// </summary>
public static class FlashCommand
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory)
    {
        // Parse against the full arg list (not the pre-split flag array) so flag
        // VALUES - e.g. the "0x10000" after --address - are found too. <port>
        // and <binary> are the first two positionals after the command.
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length < 3)
        {
            Console.Error.WriteLine(
                "Usage: flash <port> <binary> [--address 0x10000] [--baud 460800] [--chip esp32s3]");
            Console.Error.WriteLine(
                "  <port>    A configured slot's COM port (user or internal) or its slot label.");
            Console.Error.WriteLine(
                "  <binary>  Path to the firmware .bin to write.");
            return 1;
        }

        string portArg = positional[1];
        string binary = positional[2];

        if (!File.Exists(binary))
        {
            Console.Error.WriteLine($"Firmware file not found: {binary}");
            return 1;
        }

        string address = GetFlag(args, "--address", "0x10000");
        string baud = GetFlag(args, "--baud", "460800");

        // 1. Read the slot + its reset profile from the service.
        var status = await SendStatusAsync();
        if (status is null) return 1;

        var bridge = FindBridge(status, portArg);
        if (bridge is null)
        {
            Console.Error.WriteLine($"No configured bridge matches '{portArg}'.");
            if (status.Bridges.Count > 0)
                Console.Error.WriteLine(
                    "Configured ports: " +
                    string.Join(", ", status.Bridges.Select(b =>
                        $"{b.UserPort}{(b.Label is { Length: > 0 } l ? $" ({l})" : "")}")));
            return 1;
        }

        // esptool talks to the user-facing side of the com0com pair.
        string comPort = bridge.UserPort;
        string? chip = GetFlag(args, "--chip") is { Length: > 0 } c
            ? c
            : bridge.ChipFamily ?? bridge.ResetProfile?.ChipFamily;

        // 2. Resolve the reset method. Prefer the explicit reset profile; fall
        //    back to the transport, then to a classic UART reset (safe default).
        var profile = bridge.ResetProfile;
        string transport = (profile?.Transport ?? bridge.Transport ?? "uart-bridge").ToLowerInvariant();
        string resetMethod = (profile?.ResetMethod
            ?? (transport == "native-usb" ? "usb-jtag" : "classic")).ToLowerInvariant();

        Console.WriteLine($"Slot:        {bridge.Label ?? "-"} ({bridge.UserPort})");
        Console.WriteLine($"Device:      {bridge.DeviceType ?? "unknown"}");
        Console.WriteLine($"Transport:   {transport}");
        Console.WriteLine($"Reset:       {resetMethod}");
        Console.WriteLine($"Chip:        {chip ?? "auto-detect"}");
        Console.WriteLine($"Firmware:    {binary} @ {address}");
        Console.WriteLine();

        // 3. Resolve esptool (config EsptoolPath, then PATH).
        var config = CliConfig.Load();
        string? esptool = ResolveEsptool(config.Tools.EsptoolPath);
        if (esptool is null)
        {
            Console.Error.WriteLine(
                "Could not find esptool. Set Bridge:Tools:EsptoolPath in appsettings, or put " +
                "esptool(.exe) on PATH.");
            return 1;
        }

        // 4. Sequence reset / download mode based on the profile.
        //    classic/uart-bridge: esptool drives DTR/RTS over the bridge.
        //    native-usb: the chip re-enumerates on a bootloader reset, which
        //    drops the link, so esptool must attach with the board ALREADY in the
        //    bootloader (--before no_reset). If BOOT/EN are wired to Pi GPIO we
        //    ask the Pi to force download mode (POST /api/serial/recover) and
        //    release it afterwards (POST /api/serial/release); otherwise we prompt
        //    for a manual BOOT+RESET.
        //    esptool --before/--after take underscore forms to match the esptool
        //    the portal itself drives (default_reset/no_reset), accepted across
        //    esptool 4.x and 5.x.
        string before = "default_reset";
        bool classic = resetMethod == "classic" || transport == "uart-bridge";

        PiDeviceDiscovery? pi = null;
        string? releaseLabel = null;
        try
        {
            if (!classic)
            {
                before = "no_reset";
                bool gpioWired = profile is { GpioBoot: not null, GpioEn: not null };
                bool enteredViaPi = false;

                if (gpioWired && !string.IsNullOrEmpty(bridge.Label))
                {
                    pi = new PiDeviceDiscovery(
                        bridge.Host, config.Pi.PortalPort, config.Pi.DiscoveryEndpoint,
                        loggerFactory.CreateLogger<PiDeviceDiscovery>());
                    enteredViaPi = await EnterDownloadViaPiAsync(pi, bridge.Label!, profile!);
                    if (enteredViaPi) releaseLabel = bridge.Label;
                }

                if (!enteredViaPi)
                {
                    if (gpioWired)
                        Console.WriteLine("Could not confirm Pi-driven download mode; falling back to manual.");
                    PromptManualDownloadMode();
                }
            }

            // 5. Build and run esptool.
            var argList = new List<string>();
            if (chip is { Length: > 0 }) { argList.Add("--chip"); argList.Add(chip); }
            argList.Add("--port"); argList.Add(comPort);
            argList.Add("--baud"); argList.Add(baud);
            argList.Add("--before"); argList.Add(before);
            argList.Add("--after"); argList.Add(classic ? "hard_reset" : "no_reset");
            argList.Add("write_flash");
            argList.Add(address);
            argList.Add(binary);

            Console.WriteLine($"Running: {esptool} {string.Join(' ', argList)}");
            Console.WriteLine();

            int exit = await RunProcessAsync(esptool, argList);
            Console.WriteLine();
            Console.WriteLine(exit == 0 ? "Flash complete." : $"esptool exited with code {exit}.");
            return exit == 0 ? 0 : 1;
        }
        finally
        {
            // Release Pi-driven download mode so the board reboots into firmware.
            // Best effort - only when we actually forced it via the Pi.
            if (pi is not null)
            {
                if (releaseLabel is not null)
                {
                    Console.WriteLine($"Releasing {releaseLabel} download-mode GPIO on the Pi...");
                    var (ok, err) = await pi.ReleaseSlotGpioAsync(releaseLabel);
                    Console.WriteLine(ok
                        ? "  released; device rebooting into firmware."
                        : $"  WARNING: release failed: {err} (release GPIO via the portal UI).");
                }
                pi.Dispose();
            }
        }
    }

    // -------------------------------------------------------------

    /// <summary>
    /// Force a GPIO-wired slot into download mode via the Pi and wait for the
    /// portal to report state == "download_mode". The portal drives this
    /// asynchronously (flap cooldown, BOOT low, EN pulse, USB rebind), so we poll
    /// for up to ~40s. Returns true once the slot is in download mode.
    /// </summary>
    private static async Task<bool> EnterDownloadViaPiAsync(
        PiDeviceDiscovery pi, string label, ResetProfileInfo profile)
    {
        Console.WriteLine(
            $"Asking the Pi to force {label} into download mode " +
            $"(gpio_boot={profile.GpioBoot}, gpio_en={profile.GpioEn})...");
        var (ok, err) = await pi.RecoverSlotAsync(label);
        if (!ok)
        {
            Console.WriteLine($"  WARNING: Pi recover request failed: {err}");
            return false;
        }

        Console.Write("  waiting for download mode");
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(1000);
            Console.Write(".");
            var slot = await pi.GetSlotByLabelAsync(label);
            if (slot is not null &&
                string.Equals(slot.State, "download_mode", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(" ready.");
                return true;
            }
        }
        Console.WriteLine(" timed out.");
        return false;
    }

    private static async Task<ServiceStatus?> SendStatusAsync()
    {
        using var client = new IpcClient();
        try
        {
            var response = await client.SendAsync(new IpcRequest { Command = IpcCommand.Status });
            if (!response.Success || response.Data is null)
            {
                Console.Error.WriteLine(
                    $"Could not read slot status from the service: {response.Message}");
                Console.Error.WriteLine("The flash command needs the WorkbenchBridge service running.");
                return null;
            }
            return response.Data.Value.Deserialize<ServiceStatus>();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("The flash command needs the WorkbenchBridge service running.");
            return null;
        }
    }

    private static BridgeInfo? FindBridge(ServiceStatus status, string portArg) =>
        status.Bridges.FirstOrDefault(b =>
            b.UserPort.Equals(portArg, StringComparison.OrdinalIgnoreCase) ||
            b.InternalPort.Equals(portArg, StringComparison.OrdinalIgnoreCase) ||
            (b.Label?.Equals(portArg, StringComparison.OrdinalIgnoreCase) ?? false));

    private static void PromptManualDownloadMode()
    {
        Console.WriteLine();
        Console.WriteLine(">> Put the board into its download bootloader by hand:");
        Console.WriteLine(">>   hold BOOT (IO0), tap RESET (EN), then release BOOT.");
        Console.Write(">> Press Enter when ready (Ctrl+C to abort)... ");
        Console.ReadLine();
        Console.WriteLine();
    }

    /// <summary>
    /// Resolve an esptool executable. <paramref name="configured"/> may be a
    /// direct path to the binary, or a folder containing esptool(.exe). Falls
    /// back to "esptool" on PATH, then "esptool.py".
    /// </summary>
    private static string? ResolveEsptool(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return configured;
            if (Directory.Exists(configured))
            {
                foreach (var name in new[] { "esptool.exe", "esptool" })
                {
                    string candidate = Path.Combine(configured, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        // Fall back to whatever is on PATH; the OS resolves the actual binary.
        foreach (var name in new[] { "esptool", "esptool.py" })
            if (OnPath(name)) return name;

        return null;
    }

    private static bool OnPath(string exe)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        var exts = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, exe + ext))) return true;
                }
                catch { /* malformed PATH entry; ignore */ }
            }
        }
        return false;
    }

    private static async Task<int> RunProcessAsync(string fileName, List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to launch esptool ('{fileName}'): {ex.Message}");
            return 1;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    private static string GetFlag(string[] flags, string name, string defaultValue = "")
    {
        for (int i = 0; i < flags.Length - 1; i++)
            if (flags[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return flags[i + 1];
        return defaultValue;
    }
}
