namespace WorkbenchBridge.Service;

/// <summary>
/// Configuration model matching config/bridge-config.json.
/// </summary>
public sealed class BridgeConfig
{
    public PiConfig Pi { get; set; } = new();
    public ComPortRangeConfig ComPortRange { get; set; } = new();
    public List<ComPortMappingConfig> ComPortMapping { get; set; } = new();
    public ToolsConfig Tools { get; set; } = new();
    public ServiceConfig Service { get; set; } = new();
}

public sealed class PiConfig
{
    public string Host { get; set; } = "192.168.8.32";
    public int PortalPort { get; set; } = 8080;
    public string DiscoveryEndpoint { get; set; } = "/api/devices";
}

public sealed class ComPortRangeConfig
{
    public int UserPortStart { get; set; } = 41;
    public int InternalPortOffset { get; set; } = 200;
}

public sealed class ComPortMappingConfig
{
    public string SlotLabel { get; set; } = "";
    public string UserPort { get; set; } = "";
    public string InternalPort { get; set; } = "";
    public int PiTcpPort { get; set; }
}

public sealed class ToolsConfig
{
    public string Com0comPath { get; set; } = @"C:\Program Files (x86)\com0com";
    public string EsptoolPath { get; set; } = @"C:\Program Files\esptool-windows-amd64";
}

public sealed class ServiceConfig
{
    public int DiscoveryPollingIntervalSeconds { get; set; } = 10;
    public int ReconnectDelayMs { get; set; } = 5000;
}
