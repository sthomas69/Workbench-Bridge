# Workbench-Bridge

A .NET 10 Windows service that creates transparent virtual COM ports bridging to ESP32 devices connected to a remote [Universal-ESP32-Workbench](https://github.com/SensorsIoT/Universal-ESP32-Workbench) (Raspberry Pi).

## Why This Exists

The Universal-ESP32-Workbench by SensorsIoT provides a brilliant solution for sharing ESP32 devices over a network via RFC 2217 (Telnet COM Port Control). However, most Windows IDEs (Arduino IDE, PlatformIO, Visual Studio, etc.) only talk to local COM ports. They have no concept of network-attached serial devices.

Workbench-Bridge solves this by creating virtual COM port pairs using [com0com](https://com0com.sourceforge.net/) and transparently bridging them to the remote ESP32 devices. Your IDE sees a normal COM port (e.g. COM41), writes to it, and the data flows over the network to the physical ESP32 sitting on the Pi. No IDE plugins, no custom drivers, no workarounds.

**The result:** every tool that works with COM ports (esptool, Arduino IDE, PlatformIO, PuTTY, custom apps) just works, as if the ESP32 were plugged directly into your PC.

## Key Features

- **Transparent COM port bridging** via RFC 2217 and com0com virtual null-modem pairs
- **Full baud rate negotiation** including the tricky ESP32 stub flasher handshake (SLIP protocol sniffing with deferred baud change)
- **Arduino IDE compatible** at all baud rates including 921600 (the notoriously difficult one)
- **Windows service** runs in the background, auto-starts bridges on boot
- **CLI management** for adding, removing, starting and stopping bridges at runtime
- **Named pipe IPC** between service (LocalSystem) and CLI (normal user context)
- **Rolling file logging** via Serilog for diagnostics without cluttering the console
- **Multi-slot support** for multiple ESP32 devices on a single Pi

## How It Works

```
Your IDE                    Workbench-Bridge                      Pi (ESP32-Workbench)
                              (Windows Service)

COM41 -----> COM241 -------> RFC 2217 Client -------> TCP:4001 --> /dev/ttyUSB0 --> ESP32
(user port)  (internal port)  (baud rate sync)        (network)    (physical serial)
  ^               ^                                                     ^
  |               |                                                     |
com0com        com0com                                           ser2net / RFC 2217
virtual pair   virtual pair
```

1. **com0com** creates a virtual null-modem pair: COM41 (what your IDE uses) and COM241 (what the bridge connects to)
2. **Workbench-Bridge** opens COM241 and establishes an RFC 2217 session to the Pi
3. Serial data flows bidirectionally between your IDE and the physical ESP32
4. Baud rate changes (including the ESP32 stub flasher's SLIP-framed CHANGE_BAUDRATE command) are detected and forwarded to the Pi via RFC 2217 negotiation

### The Deferred Baud Change

The hardest part of this project was getting Arduino IDE uploads working at 921600 baud. The ESP32 stub flasher sends a CHANGE_BAUDRATE command inside a SLIP frame, but serial reads can split that frame across multiple chunks. If you switch the Pi's baud rate before the entire frame (including the terminating 0xC0 byte) has been sent, you corrupt the stream.

Workbench-Bridge solves this with a deferred baud change state machine in `SlipBaudRateSniffer`: it extracts the target baud rate when it sees the command, but waits until the SLIP frame-end byte (0xC0) has actually been transmitted before switching the Pi to the new rate.

## Project Structure

```
Workbench-Bridge/
  Workbench-Bridge.slnx          # VS2026 solution file
  src/
    WorkbenchBridge.Rfc2217/     # RFC 2217 client, serial bridge, SLIP baud sniffer
    WorkbenchBridge.Service/     # Windows service host (Serilog, health monitoring)
    WorkbenchBridge.Cli/         # Command-line interface (IPC + direct/debug modes)
    WorkbenchBridge.Ipc/         # Named pipe protocol and client/server
    WorkbenchBridge.Tests/       # xUnit tests (55 tests, 28 SLIP sniffer scenarios)
```

## Prerequisites

- **Windows 10/11** (or Windows Server 2019+)
- **.NET 10 Runtime** (or SDK for building from source)
- **[com0com](https://com0com.sourceforge.net/) v3.0.0** for virtual COM port pairs
- **Universal-ESP32-Workbench** running on a Raspberry Pi with ser2net / RFC 2217 configured
- **Visual Studio 2026** (for building from source; any edition)

## Quick Start

### 1. Install com0com

Download and install [com0com v3.0.0](https://com0com.sourceforge.net/). Create a virtual port pair:

```
setupc install PortName=COM41 PortName=COM241
```

### 2. Build

Open `Workbench-Bridge.slnx` in Visual Studio 2026 and build, or from the command line:

```
dotnet build Workbench-Bridge.slnx
```

### 3. Test Standalone (No Service)

Use the CLI in direct mode to test a single bridge:

```
workbench-bridge bridge --local COM241 --host 192.168.8.32 --port 4001 --verbose
```

Then open your IDE on COM41 and flash an ESP32.

### 4. Install as Windows Service

```
sc create WorkbenchBridge binPath="C:\path\to\WorkbenchBridge.Service.exe"
sc start WorkbenchBridge
```

### 5. Manage via CLI

```
workbench-bridge list                                    # show configured bridges
workbench-bridge add COM41 192.168.8.32 4001             # add a bridge (auto-creates COM241)
workbench-bridge start COM41                             # start a bridge
workbench-bridge stop COM41                              # stop a bridge
workbench-bridge status COM41                            # show bridge status and stats
workbench-bridge diagnose COM41                          # test connectivity
workbench-bridge remove COM41                            # remove a bridge
```

## Configuration

Default configuration lives in `appsettings.json` alongside the service executable. Bridges added via CLI are persisted to config and survive service restarts.

```json
{
  "BridgeMappings": [
    {
      "UserPort": "COM41",
      "InternalPort": "COM241",
      "Host": "192.168.8.32",
      "Rfc2217Port": 4001,
      "Label": "SLOT1",
      "Description": "ESP32-S3 DevKit",
      "AutoStart": true
    }
  ]
}
```

## Logging

Logs are written to `%ProgramData%\ESP32WorkbenchBridge\logs\workbenchbridge.log` with daily rolling and 10MB size limits (5 files retained). The service also logs to the console when run interactively.

Use the CLI to adjust log verbosity at runtime:

```
workbench-bridge set COM41 --verbose          # enable verbose logging for a bridge
workbench-bridge set COM41 --hexdump          # enable hex dump of all serial traffic
```

## Testing

```
dotnet test src/WorkbenchBridge.Tests/
```

The test suite includes 55 tests covering the SLIP baud rate sniffer across 28 scenarios: split reads, deferred baud changes, SLIP escaping, Arduino IDE's characteristic 3-read pattern, and real-world byte sequences.

## Attribution

This project builds on top of the excellent [Universal-ESP32-Workbench](https://github.com/SensorsIoT/Universal-ESP32-Workbench) by [SensorsIoT](https://github.com/SensorsIoT). The Workbench provides the Pi-side infrastructure (ser2net, RFC 2217 serving, device management portal) that makes remote ESP32 access possible. Workbench-Bridge is the Windows-side companion that completes the picture by making those remote devices appear as local COM ports.

## Licence

[MIT](LICENSE)
