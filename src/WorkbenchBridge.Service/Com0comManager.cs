using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Service;

/// <summary>
/// Manages com0com virtual COM port pairs via setupc.exe.
///
/// Creating pairs correctly:
/// 1. Use PortName=COM# to trigger the Windows Ports class installer
///    (this makes ports visible in Device Manager under "Ports (COM and LPT)" and in IDEs)
/// 2. Then rename to the desired port names via RealPortName=COMxx
///
/// Direct PortName=COM41 does NOT invoke the Ports class installer.
/// </summary>
public sealed partial class Com0comManager
{
    private readonly string _setupcPath;
    private readonly ILogger<Com0comManager> _logger;

    public Com0comManager(string com0comInstallPath, ILogger<Com0comManager> logger)
    {
        _setupcPath = Path.Combine(com0comInstallPath, "setupc.exe");
        _logger = logger;

        if (!File.Exists(_setupcPath))
            throw new FileNotFoundException($"setupc.exe not found at {_setupcPath}");
    }

    /// <summary>
    /// List existing com0com port pairs.
    /// Returns pairs as (pairIndex, portA, portB).
    /// </summary>
    public async Task<List<Com0comPair>> ListPairsAsync(CancellationToken ct = default)
    {
        var output = await RunSetupcAsync("list", ct);
        var pairs = new List<Com0comPair>();

        // Parse output lines like:
        //   CNCA0 PortName=COM41,EmuBR=yes
        //   CNCB0 PortName=COM241,EmuBR=yes
        var regex = PortParseRegex();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = regex.Match(line.Trim());
            if (match.Success)
            {
                string side = match.Groups["side"].Value; // CNCA or CNCB
                int index = int.Parse(match.Groups["idx"].Value);
                string portName = match.Groups["port"].Value;
                string props = match.Groups["props"].Value;

                var existing = pairs.FirstOrDefault(p => p.Index == index);
                if (existing is null)
                {
                    existing = new Com0comPair { Index = index };
                    pairs.Add(existing);
                }

                if (side == "CNCA")
                    existing.PortA = portName;
                else
                    existing.PortB = portName;

                existing.HasEmuBR = props.Contains("EmuBR=yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        return pairs;
    }

    /// <summary>
    /// Ensure a virtual COM port pair exists with the specified names.
    /// Creates it if missing, with EmuBR=yes for proper baud rate emulation.
    /// Returns the pair index.
    /// </summary>
    public async Task<int> EnsurePairAsync(
        string userPort, string internalPort, CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring com0com pair: {User} <-> {Internal}", userPort, internalPort);

        // Check if pair already exists
        var pairs = await ListPairsAsync(ct);
        var existing = pairs.FirstOrDefault(p =>
            (p.PortA == userPort && p.PortB == internalPort) ||
            (p.PortA == internalPort && p.PortB == userPort));

        if (existing is not null)
        {
            _logger.LogDebug("Pair already exists at index {Index}", existing.Index);

            // Ensure EmuBR is enabled
            if (!existing.HasEmuBR)
            {
                await SetEmuBRAsync(existing.Index, ct);
            }

            return existing.Index;
        }

        // Create new pair with auto-assigned names (triggers Ports class installer)
        _logger.LogInformation("Creating new com0com pair with Ports class installer");
        var createOutput = await RunSetupcAsync("install PortName=COM# PortName=COM#", ct);
        _logger.LogDebug("Create output: {Output}", createOutput);

        // Find the newly created pair
        pairs = await ListPairsAsync(ct);
        var newPair = pairs.MaxBy(p => p.Index);
        if (newPair is null)
            throw new InvalidOperationException("Failed to create com0com pair.");

        // Rename to desired names
        await RunSetupcAsync($"change CNCA{newPair.Index} RealPortName={userPort}", ct);
        await RunSetupcAsync($"change CNCB{newPair.Index} RealPortName={internalPort}", ct);

        // Enable baud rate emulation
        await SetEmuBRAsync(newPair.Index, ct);

        _logger.LogInformation(
            "Created pair index {Index}: {User} <-> {Internal}",
            newPair.Index, userPort, internalPort);

        return newPair.Index;
    }

    /// <summary>
    /// Remove a com0com pair by index.
    /// </summary>
    public async Task RemovePairAsync(int index, CancellationToken ct = default)
    {
        _logger.LogInformation("Removing com0com pair index {Index}", index);
        await RunSetupcAsync($"remove {index}", ct);
    }

    /// <summary>
    /// Enable EmuBR (baud rate emulation) on both sides of a pair.
    /// This prevents data from flowing through the virtual pair faster than
    /// the configured baud rate, which is essential for hub4com/RFC 2217 to work.
    /// </summary>
    private async Task SetEmuBRAsync(int index, CancellationToken ct)
    {
        await RunSetupcAsync($"change CNCA{index} EmuBR=yes,EmuOverrun=yes", ct);
        await RunSetupcAsync($"change CNCB{index} EmuBR=yes,EmuOverrun=yes", ct);
        _logger.LogDebug("Enabled EmuBR on pair index {Index}", index);
    }

    private async Task<string> RunSetupcAsync(string arguments, CancellationToken ct)
    {
        _logger.LogDebug("Running: setupc.exe {Args}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = _setupcPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // setupc.exe requires elevation for install/remove/change
            Verb = "runas"
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogWarning("setupc.exe exited with code {Code}: {Error}",
                process.ExitCode, stderr.Trim());
        }

        return stdout;
    }

    [GeneratedRegex(@"(?<side>CNC[AB])(?<idx>\d+)\s+PortName=(?<port>[^,\s]+)(?<props>.*)")]
    private static partial Regex PortParseRegex();
}

public class Com0comPair
{
    public int Index { get; set; }
    public string? PortA { get; set; }
    public string? PortB { get; set; }
    public bool HasEmuBR { get; set; }
}
