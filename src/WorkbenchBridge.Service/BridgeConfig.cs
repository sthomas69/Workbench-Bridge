namespace WorkbenchBridge.Service;

/// <summary>
/// Bound to the "Bridge" section of appsettings. appsettings.json holds
/// safe public defaults; appsettings.Local.json (gitignored) supplies the
/// real per-machine values and is layered on top at startup.
/// </summary>
public sealed class BridgeConfig
{
    public PiConfig Pi { get; set; } = new();
    public WindowsServicesConfig WindowsServices { get; set; } = new();
    public RegistryConfig Registry { get; set; } = new();
    public ToolsConfig Tools { get; set; } = new();
    public List<ComPortMappingConfig> ComPortMapping { get; set; } = new();
    public RuntimeConfig Runtime { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

/// <summary>
/// File-logging settings (bound from <c>Bridge:Logging</c> in appsettings.json).
/// One place configures both the service (writer) and the CLI (reader/clearer).
/// Three clearly-named streams live under <see cref="Directory"/>, rolled by
/// <see cref="RollingInterval"/> and purged after <see cref="RetentionDays"/>:
///   * <c>service-&lt;yyyyMMddHH&gt;.log</c> — service controller / generic control,
///   * <c>cli-&lt;yyyyMMddHH&gt;.log</c>     — CLI command invocations,
///   * <c>port-COMxx-&lt;yyyyMMddHH&gt;.log</c> — one per bridged COM port (serial traffic,
///     including <c>--raw</c>; capped small so high-volume captures rotate often).
/// </summary>
public sealed class LoggingConfig
{
    /// <summary>Directory holding all log files. ProgramData = machine-wide, admin-writable.</summary>
    public string Directory { get; set; } = @"C:\ProgramData\Workbench-Bridge\logs";

    /// <summary>Serilog roll cadence: "Hour" (default) or "Day".</summary>
    public string RollingInterval { get; set; } = "Hour";

    /// <summary>Delete any *.log older than this many days. Default 3.</summary>
    public int RetentionDays { get; set; } = 3;

    /// <summary>Size cap (MB) for the service + cli logs before an intra-period roll.</summary>
    public int ControllerFileSizeMB { get; set; } = 20;

    /// <summary>Size cap (MB) for per-port logs. Small so a busy --raw capture rolls often.</summary>
    public int PortFileSizeMB { get; set; } = 5;
}

public sealed class PiConfig
{
    public string Host { get; set; } = "workbench.local";
    public int PortalPort { get; set; } = 8080;
    public string DiscoveryEndpoint { get; set; } = "/api/devices";
}

/// <summary>
/// Windows service names. Informational/reserved: provisioning reloads com0com
/// per-device with pnputil /restart-device and the WorkbenchBridge service
/// applies config in-process, so neither service is stopped via SCM anymore.
/// </summary>
public sealed class WindowsServicesConfig
{
    /// <summary>Service name of the WorkbenchBridge worker service.</summary>
    public string WorkbenchServiceName { get; set; } = "WorkbenchBridge";

    /// <summary>Kernel driver service name for com0com.</summary>
    public string Com0comServiceName { get; set; } = "com0com";
}

/// <summary>
/// Registry locations the CLI and service read/write.
/// </summary>
public sealed class RegistryConfig
{
    /// <summary>HKLM-relative key path holding CNCA{n}/CNCB{n} pair subkeys.</summary>
    public string Com0comParametersKey { get; set; } =
        @"SYSTEM\CurrentControlSet\Services\com0com\Parameters";
}

public sealed class ToolsConfig
{
    /// <summary>com0com install folder. The service (via Provisioner) seeds
    /// missing pair indices from here with setupc.exe install (PnP) when it
    /// handles a Configure request.</summary>
    public string Com0comPath { get; set; } = @"C:\Program Files (x86)\com0com";
    public string EsptoolPath { get; set; } = @"C:\Program Files\esptool-windows-amd64";
}

/// <summary>
/// One bridged slot: a com0com pair (User &lt;-&gt; Internal) plus the TCP
/// RFC 2217 endpoint on the Pi that the Internal side bridges to.
/// </summary>
public sealed class ComPortMappingConfig
{
    public string SlotLabel { get; set; } = "";

    /// <summary>RFC 2217 TCP port on the Pi for this slot.</summary>
    public int PiTcpPort { get; set; }

    /// <summary>User-facing side of the com0com pair (the IDE opens this).</summary>
    public Com0comSideConfig User { get; set; } = new();

    /// <summary>Internal side of the com0com pair (the bridge opens this).</summary>
    public Com0comSideConfig Internal { get; set; } = new();

    /// <summary>Returns the pair index parsed from User.RegistryKey (e.g. "CNCA3" -> 3).</summary>
    public int GetPairIndex()
    {
        var key = User.RegistryKey;
        if (key.Length > 4 && int.TryParse(key.AsSpan(4), out int idx))
            return idx;
        throw new InvalidOperationException(
            $"Cannot parse pair index from User.RegistryKey '{key}' (expected e.g. 'CNCA3').");
    }
}

/// <summary>
/// One side of a com0com pair, addressed by registry subkey.
/// </summary>
public sealed class Com0comSideConfig
{
    /// <summary>Registry subkey name under com0com\Parameters (e.g. "CNCA0", "CNCB3").</summary>
    public string RegistryKey { get; set; } = "";

    /// <summary>Desired PortName REG_SZ value (e.g. "COM41").</summary>
    public string PortName { get; set; } = "";
}

/// <summary>
/// Runtime knobs for the service loop.
/// </summary>
public sealed class RuntimeConfig
{
    public int DiscoveryPollingIntervalSeconds { get; set; } = 10;
    public int ReconnectDelayMs { get; set; } = 5000;
}
