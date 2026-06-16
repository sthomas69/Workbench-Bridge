using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace WorkbenchBridge.Service;

/// <summary>
/// Reads and writes com0com virtual port configuration directly via the
/// Windows registry. com0com persists each side of a pair as a subkey
/// CNCA{n} or CNCB{n} under HKLM\...\com0com\Parameters with REG_SZ PortName
/// and REG_DWORD EmuBR/EmuOverrun (non-zero = enabled). The driver reloads
/// these values when its service is restarted.
///
/// Pair seeding (creating brand-new CNCA{n}/CNCB{n} subkeys at the device
/// level so Windows actually exposes COM ports for them) requires PnP and
/// is done by <see cref="Provisioner"/> via setupc.exe install. This class only
/// reads existing subkeys and writes PortName/EmuBR/EmuOverrun values.
/// </summary>
public sealed class Com0comRegistry
{
    private readonly string _parametersKeyPath;
    private readonly ILogger _logger;

    /// <summary>
    /// REG_DWORD value com0com uses for boolean "yes" flags. This matches
    /// what setupc.exe writes when invoked with EmuBR=yes.
    /// </summary>
    private const int FlagOn = unchecked((int)0xFFFFFFFFu);

    /// <summary>REG_DWORD value for a boolean "no" flag.</summary>
    private const int FlagOff = 0;

    public Com0comRegistry(string parametersKeyPath, ILogger logger)
    {
        _parametersKeyPath = parametersKeyPath;
        _logger = logger;
    }

    /// <summary>
    /// Returns a snapshot of all com0com sides keyed by subkey name
    /// (e.g. "CNCA0", "CNCB3").
    /// </summary>
    public Dictionary<string, Com0comSideStatus> ListSides()
    {
        var result = new Dictionary<string, Com0comSideStatus>(StringComparer.OrdinalIgnoreCase);

        using var key = Registry.LocalMachine.OpenSubKey(_parametersKeyPath);
        if (key is null)
        {
            _logger.LogWarning("com0com registry key HKLM\\{Path} not found.", _parametersKeyPath);
            return result;
        }

        foreach (var subName in key.GetSubKeyNames())
        {
            if (!TryParseSide(subName, out _, out _)) continue;

            using var sub = key.OpenSubKey(subName);
            if (sub is null) continue;

            result[subName] = new Com0comSideStatus
            {
                SubkeyName = subName,
                PortName = sub.GetValue("PortName") as string ?? "",
                RealPortName = sub.GetValue("RealPortName") as string ?? "",
                EmuBR = IsFlagOn(sub.GetValue("EmuBR")),
                EmuOverrun = IsFlagOn(sub.GetValue("EmuOverrun"))
            };
        }

        return result;
    }

    /// <summary>
    /// Returns existing pair indices (only those where at least one side subkey exists).
    /// </summary>
    public HashSet<int> ListExistingPairIndices()
    {
        var indices = new HashSet<int>();
        using var key = Registry.LocalMachine.OpenSubKey(_parametersKeyPath);
        if (key is null) return indices;

        foreach (var subName in key.GetSubKeyNames())
        {
            if (TryParseSide(subName, out _, out int idx))
                indices.Add(idx);
        }
        return indices;
    }

    /// <summary>
    /// Writes RealPortName REG_SZ to the named CNCAn/CNCBn subkey under
    /// com0com\Parameters. This is the value the com0com driver hands to
    /// the Microsoft Ports class co-installer on the next device restart,
    /// resulting in the device appearing under "Ports (COM &amp; LPT)" with
    /// the desired COM name. Bypasses setupc.exe's ComDB call (which pops
    /// the "ComDBOpen ERROR 5 Access is denied" GUI dialog).
    /// </summary>
    public void SetRealPortName(string subkeyName, string portName)
    {
        if (!TryParseSide(subkeyName, out _, out _))
            throw new ArgumentException(
                $"Invalid com0com subkey name '{subkeyName}'.");
        string subPath = $@"{_parametersKeyPath}\{subkeyName}";
        using var sub = Registry.LocalMachine.OpenSubKey(subPath, writable: true)
            ?? throw new InvalidOperationException(
                $"HKLM\\{subPath} does not exist; run setupc.exe install first.");

        sub.SetValue("RealPortName", portName, RegistryValueKind.String);
        _logger.LogInformation("{Subkey}.RealPortName = {Port}", subkeyName, portName);
    }

    /// <summary>
    /// Reads the COM name Windows is actually exposing for a com0com side
    /// from the PnP Enum branch (HKLM\...\Enum\COM0COM\PORT\<subkey>\Device
    /// Parameters\PortName). This is the authoritative value that
    /// SerialPort.GetPortNames(), Win32_SerialPort, and Device Manager all
    /// reflect - more reliable than reading Parameters\PortName/RealPortName
    /// which can get out of sync after a Ports-class installer dance.
    /// Returns null if the device hasn't been PnP-registered yet.
    /// </summary>
    public string? ReadExposedPortNameFromEnum(string subkeyName)
    {
        if (!TryParseSide(subkeyName, out _, out _))
            throw new ArgumentException(
                $"Invalid com0com subkey name '{subkeyName}'.");
        string enumPath = $@"SYSTEM\CurrentControlSet\Enum\COM0COM\PORT\{subkeyName}\Device Parameters";
        using var key = Registry.LocalMachine.OpenSubKey(enumPath);
        return key?.GetValue("PortName") as string;
    }

    /// <summary>Windows "Ports (COM &amp; LPT)" device setup class GUID.</summary>
    private const string PortsClassGuid = "{4d36e978-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// True only if Windows currently enumerates this com0com side under the
    /// standard Ports (COM &amp; LPT) class — i.e. it is a real, usable COM port.
    /// A side seeded by setupc whose Ports-class migration did NOT complete sits
    /// in com0com's own 'CNCPorts' class (a different ClassGUID); it still has a
    /// PortName/RealPortName in the registry and can even appear in
    /// SerialPort.GetPortNames()/ComDB, yet is NOT a usable port. This is the
    /// authoritative "did the migration actually finish" check - it reads the
    /// device's ClassGUID from HKLM\...\Enum\COM0COM\PORT\&lt;subkey&gt;.
    /// Returns false if the device isn't PnP-registered at all.
    /// </summary>
    public bool IsExposedInPortsClass(string subkeyName)
    {
        if (!TryParseSide(subkeyName, out _, out _))
            throw new ArgumentException($"Invalid com0com subkey name '{subkeyName}'.");
        string enumPath = $@"SYSTEM\CurrentControlSet\Enum\COM0COM\PORT\{subkeyName}";
        using var key = Registry.LocalMachine.OpenSubKey(enumPath);
        var guid = key?.GetValue("ClassGUID") as string;
        return string.Equals(guid, PortsClassGuid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the <c>serenum</c> upper-filter from a com0com PORT devnode.
    ///
    /// <para>com0com.inf attaches <c>serenum.sys</c> (the serial enumerator) as an
    /// UpperFilter on every port it installs. serenum probes a serial port for a
    /// legacy plug-and-play device; on a com0com port it frequently mis-detects a
    /// "Microsoft Serial Mouse" and reports a phantom mouse PDO. With sermouse
    /// enabled that phantom injects real cursor movement - which is exactly the
    /// desktop hijack seen during pair-7 install churn (2026-06-15). Our com0com
    /// ports never host a serial mouse, so serenum is pure liability; stripping it
    /// stops the mis-detection at the source. Read/write of the COM port itself is
    /// unaffected (serenum is an enumerator, not the function driver).</para>
    ///
    /// <para>The change takes effect on the devnode's next restart/re-enumeration
    /// (which the rename step performs anyway). Returns true if serenum was present
    /// and removed, false if it was already absent.</para>
    /// </summary>
    public bool StripSerenumUpperFilter(string subkeyName)
    {
        if (!TryParseSide(subkeyName, out _, out _))
            throw new ArgumentException($"Invalid com0com subkey name '{subkeyName}'.");
        string enumPath = $@"SYSTEM\CurrentControlSet\Enum\COM0COM\PORT\{subkeyName}";
        using var key = Registry.LocalMachine.OpenSubKey(enumPath, writable: true);
        if (key is null) return false;

        var current = key.GetValue("UpperFilters") as string[];
        if (current is null || current.Length == 0) return false;

        var kept = current
            .Where(f => !string.Equals(f, "serenum", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (kept.Length == current.Length) return false; // serenum not present

        key.SetValue("UpperFilters", kept, RegistryValueKind.MultiString);
        _logger.LogInformation(
            "{Subkey}: stripped 'serenum' upper-filter (serial-mouse mis-detection guard).",
            subkeyName);
        return true;
    }

    /// <summary>
    /// Writes the canonical REG_DWORD EmuBR/EmuOverrun flags to the named side
    /// subkey: EmuBR=on (0xFFFFFFFF), EmuOverrun=off (0).
    ///
    /// EmuBR=yes makes com0com pace bytes to the configured baud rate. EmuOverrun
    /// MUST be off: with it on, com0com emulates a UART receive overrun and
    /// silently DISCARDS bytes when the reader lags, which corrupts ESP32 traffic
    /// (esptool sync/flash, console). With it off, com0com buffers/flow-controls
    /// so no data is lost. (setupc historically wrote both as "yes"; this method
    /// is the authoritative normalisation.)
    ///
    /// We intentionally do NOT touch PortName here - setupc.exe owns
    /// PortName/RealPortName so the Ports-class installer stays in sync.
    /// Requires elevation. Subkey must already exist (created by setupc).
    /// </summary>
    public void EnsureEmuFlags(string subkeyName)
    {
        if (!TryParseSide(subkeyName, out _, out _))
            throw new ArgumentException(
                $"Invalid com0com subkey name '{subkeyName}'. Expected CNCA{{n}} or CNCB{{n}}.");

        string subPath = $@"{_parametersKeyPath}\{subkeyName}";
        using var sub = Registry.LocalMachine.OpenSubKey(subPath, writable: true);
        if (sub is null)
        {
            throw new InvalidOperationException(
                $"Registry subkey HKLM\\{subPath} does not exist. " +
                "Run setupc.exe install for this pair first (the CLI does this automatically).");
        }

        sub.SetValue("EmuBR", FlagOn, RegistryValueKind.DWord);
        sub.SetValue("EmuOverrun", FlagOff, RegistryValueKind.DWord);

        _logger.LogInformation(
            "{Subkey}: normalised EmuBR=0x{On:X8}, EmuOverrun=0x{Off:X8}",
            subkeyName, FlagOn, FlagOff);
    }

    /// <summary>
    /// Deletes the CNCA{n}/CNCB{n} subkey if present. Used to clear "ghost"
    /// registry entries left behind when a pair was renamed via registry only
    /// and never PnP-installed (setupc remove can't see such entries and exits
    /// non-zero). After deleting we call setupc install <n> to recreate the
    /// pair cleanly with proper PnP registration.
    /// </summary>
    public bool DeleteSide(string subkeyName)
    {
        if (!TryParseSide(subkeyName, out _, out _))
            throw new ArgumentException($"Invalid subkey name '{subkeyName}'.");
        string subPath = $@"{_parametersKeyPath}\{subkeyName}";
        using var parent = Registry.LocalMachine.OpenSubKey(_parametersKeyPath, writable: true);
        if (parent is null) return false;
        if (parent.OpenSubKey(subkeyName) is null) return false;
        parent.DeleteSubKeyTree(subkeyName, throwOnMissingSubKey: false);
        _logger.LogInformation("Deleted orphan registry subkey HKLM\\{Path}", subPath);
        return true;
    }

    public static bool TryParseSide(string subkeyName, out string side, out int index)
    {
        side = "";
        index = -1;
        if (subkeyName.Length < 5) return false;
        var prefix = subkeyName.Substring(0, 4);
        if (prefix != "CNCA" && prefix != "CNCB") return false;
        if (!int.TryParse(subkeyName.AsSpan(4), out index)) return false;
        side = prefix;
        return true;
    }

    private static bool IsFlagOn(object? value)
    {
        return value switch
        {
            int i => i != 0,
            uint u => u != 0,
            long l => l != 0,
            string s => s.Equals("yes", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

public sealed class Com0comSideStatus
{
    public required string SubkeyName { get; init; }

    /// <summary>Raw PortName REG_SZ. "COM#" is a placeholder meaning "auto-
    /// assign" - used when the pair is to be renamed via RealPortName.</summary>
    public required string PortName { get; init; }

    /// <summary>RealPortName REG_SZ. Empty means the pair was installed via
    /// PortName directly (registers under com0com's own device class, which
    /// many serial-port enumerators - Arduino IDE, etc - don't list). When
    /// set, com0com's Ports-class installer ran and the device is listed
    /// under Windows' standard "Ports (COM &amp; LPT)" Device Manager node.</summary>
    public required string RealPortName { get; init; }

    public required bool EmuBR { get; init; }
    public required bool EmuOverrun { get; init; }

    /// <summary>True if the pair is registered under the Windows Ports class
    /// (visible to all standard serial enumerators).</summary>
    public bool IsInPortsClass => !string.IsNullOrEmpty(RealPortName);

    /// <summary>The effective COM port name Windows exposes for this side -
    /// RealPortName if set, otherwise PortName.</summary>
    public string EffectivePortName =>
        !string.IsNullOrEmpty(RealPortName) ? RealPortName : PortName;
}
