using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace WorkbenchBridge.Service;

/// <summary>
/// Reads and clears entries in Windows' COM port number reservation table
/// ("ComDB"), located at HKLM\SYSTEM\CurrentControlSet\Control\COM Name
/// Arbiter\ComDB. The value is a single REG_BINARY bitmap: bit N (counting
/// from 0) corresponds to COM(N+1) — set means the number is reserved.
///
/// When a com0com pair is removed by deleting the CNCAn/CNCBn registry
/// subkeys directly (instead of via setupc remove, which can fail for
/// "ghost" pairs that have no live PnP device), ComDB retains the stale
/// reservation. The next `setupc install ... PortName=COM41 ...` then
/// surfaces a GUI dialog asking the user to confirm reusing the name.
/// Clearing the bit beforehand suppresses that dialog.
/// </summary>
public sealed class ComPortDatabase
{
    private const string ComDbKeyPath =
        @"SYSTEM\CurrentControlSet\Control\COM Name Arbiter";
    private const string ComDbValueName = "ComDB";

    private readonly ILogger _logger;

    public ComPortDatabase(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Releases the reservation for the given COM port name (e.g. "COM41").
    /// No-op if the name isn't currently reserved. Requires elevation.
    /// Returns true if a bit was actually cleared.
    /// </summary>
    public bool Release(string portName)
    {
        if (!TryParsePortNumber(portName, out int num))
        {
            _logger.LogWarning("Cannot parse COM port number from '{Name}'; skipping ComDB release.", portName);
            return false;
        }

        using var key = Registry.LocalMachine.OpenSubKey(ComDbKeyPath, writable: true);
        if (key is null)
        {
            _logger.LogWarning("ComDB registry key HKLM\\{Path} not found.", ComDbKeyPath);
            return false;
        }

        if (key.GetValue(ComDbValueName) is not byte[] db)
        {
            _logger.LogDebug("ComDB has no '{Value}' entry; nothing to release.", ComDbValueName);
            return false;
        }

        int bitIndex = num - 1; // COM1 -> bit 0
        int byteIndex = bitIndex / 8;
        int bitMask = 1 << (bitIndex % 8);

        if (byteIndex >= db.Length)
        {
            _logger.LogDebug("ComDB bitmap is shorter than {Bytes} bytes; {Name} not reserved.", byteIndex + 1, portName);
            return false;
        }

        if ((db[byteIndex] & bitMask) == 0)
        {
            _logger.LogDebug("{Name} not currently reserved in ComDB; no change.", portName);
            return false;
        }

        db[byteIndex] &= (byte)~bitMask;
        key.SetValue(ComDbValueName, db, RegistryValueKind.Binary);
        _logger.LogInformation("Released ComDB reservation for {Name} (bit {Bit}).", portName, bitIndex);
        return true;
    }

    private static bool TryParsePortNumber(string portName, out int number)
    {
        number = -1;
        if (portName.Length < 4) return false;
        if (!portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(portName.AsSpan(3), out number) && number > 0;
    }
}
