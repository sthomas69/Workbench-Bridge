using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkbenchBridge.Ipc;
using WorkbenchBridge.Service;

namespace WorkbenchBridge.Cli;

/// <summary>
/// <c>device-info &lt;slot&gt;</c> (alias <c>identify</c>): actively identify the chip
/// on a slot by asking the Pi portal to run a chip-id probe
/// (<c>POST /api/devices/{slot}/device_id</c>), then print what it learned.
///
/// The &lt;slot&gt; argument may be:
///   * a slot label (e.g. "SLOT1"), used directly in the Pi API path; or
///   * a configured COM port (e.g. "COM41"), resolved to its slot label and Pi
///     host via the service, so users can identify by the port their IDE uses.
///
/// The Pi host/port come from the service (for the COM-port form) or from
/// appsettings (for the label form / --host / --port overrides). Tolerant of
/// old portals: a missing endpoint is reported, not crashed on.
/// </summary>
public static class DeviceInfoCommand
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory)
    {
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length < 2)
        {
            Console.Error.WriteLine("Usage: device-info <slot|comport> [--host <ip>] [--port <portalPort>]");
            Console.Error.WriteLine("  Asks the Pi to identify the chip on the slot and prints the result.");
            return 1;
        }

        string target = positional[1];
        var config = CliConfig.Load();

        string host = GetFlag(args, "--host", config.Pi.Host);
        int portalPort = int.TryParse(GetFlag(args, "--port"), out int p) ? p : config.Pi.PortalPort;
        string slotLabel = target;

        // If the user passed a COM port, resolve it to a slot label + Pi host via
        // the service so they don't have to remember the SLOTn label.
        if (target.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await ResolveComPortAsync(target);
            if (resolved is null)
            {
                Console.Error.WriteLine(
                    $"'{target}' is not a configured COM port (and the service may not be running).");
                Console.Error.WriteLine("Pass the slot label directly, e.g. 'device-info SLOT1'.");
                return 1;
            }
            slotLabel = resolved.Value.Label;
            host = resolved.Value.Host;
        }

        Console.WriteLine($"Identifying {slotLabel} via {host}:{portalPort} ...");

        using var discovery = new PiDeviceDiscovery(
            host, portalPort, config.Pi.DiscoveryEndpoint,
            loggerFactory.CreateLogger<PiDeviceDiscovery>());

        var result = await discovery.RequestDeviceIdAsync(slotLabel);
        if (!result.Success)
        {
            Console.Error.WriteLine($"Device identification failed: {result.Error}");
            Console.Error.WriteLine(
                "The Pi portal may be unreachable, or predate the /device_id endpoint.");
            return 1;
        }

        // The portal answers 200 with {"ok": false, "error": ...} when the slot
        // has no device or the esptool probe failed - treat that as a failure.
        if (result.Fields.TryGetValue("ok", out var okField) &&
            okField.ValueKind == JsonValueKind.False)
        {
            string err = result.Fields.TryGetValue("error", out var e)
                ? e.GetString() ?? "unknown error"
                : "unknown error";
            Console.Error.WriteLine($"Pi could not identify {slotLabel}: {err}");
            Console.Error.WriteLine("Is a device present on that slot?");
            return 1;
        }

        Console.WriteLine();
        if (result.Fields.Count > 0)
        {
            int width = result.Fields.Keys.Max(k => k.Length);
            foreach (var (key, value) in result.Fields)
                Console.WriteLine($"  {key.PadRight(width)}  {FormatValue(value)}");
        }
        else if (!string.IsNullOrWhiteSpace(result.Raw))
        {
            Console.WriteLine(result.Raw);
        }
        else
        {
            Console.WriteLine("(portal returned an empty response)");
        }

        return 0;
    }

    // -------------------------------------------------------------

    private static async Task<(string Label, string Host)?> ResolveComPortAsync(string comPort)
    {
        using var client = new IpcClient();
        try
        {
            var response = await client.SendAsync(new IpcRequest { Command = IpcCommand.Status });
            if (!response.Success || response.Data is null) return null;

            var status = response.Data.Value.Deserialize<ServiceStatus>();
            var bridge = status?.Bridges.FirstOrDefault(b =>
                b.UserPort.Equals(comPort, StringComparison.OrdinalIgnoreCase) ||
                b.InternalPort.Equals(comPort, StringComparison.OrdinalIgnoreCase));

            if (bridge is null || string.IsNullOrEmpty(bridge.Label)) return null;
            return (bridge.Label, bridge.Host);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Join(", ",
            value.EnumerateArray().Select(e => e.ToString())),
        _ => value.ToString()
    };

    private static string GetFlag(string[] args, string name, string defaultValue = "")
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return defaultValue;
    }
}
