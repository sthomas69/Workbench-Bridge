Workbench-Bridge
================

Bridges local Windows COM ports to ESP32 devices on a Universal-ESP32-Workbench
(Raspberry Pi) over RFC2217, so any IDE / esptool flashes them as if the board
were plugged into your PC. Self-contained: NO .NET install required.

TWO EXECUTABLES
  Workbench-Bridge.Service.exe   the bridge worker (console app OR Windows service)
  Workbench-Bridge.Cli.exe       management CLI (status, set, flash, logs, ...)

RUN IT (quick try)
  Double-click  Workbench-Bridge.Service.exe
    -> Windows asks for admin (com0com + COM ports need it), then a console
       window runs the bridge with live logs. Ctrl-C to stop.

INSTALL AS A WINDOWS SERVICE (auto-starts on boot)
  Workbench-Bridge.Service.exe --install
    -> copies the exe + your appsettings.Local.json into
       C:\Program Files\Workbench-Bridge\  and registers a delayed-auto service.
  Workbench-Bridge.Service.exe --uninstall   stop + remove the service.

MANAGE A RUNNING BRIDGE
  Workbench-Bridge.Cli.exe status              health + per-slot stats
  Workbench-Bridge.Cli.exe set COMxx --verbose per-bridge logging
  Workbench-Bridge.Cli.exe flash COMxx fw.bin  flash a board over the bridge
  Workbench-Bridge.Cli.exe logs --where        show where the logs live
  Workbench-Bridge.Cli.exe logs --clear        clear the logs
  Workbench-Bridge.Cli.exe --help              full command list

CONFIG (lives next to the exe)
  appsettings.json        public defaults (safe to ship)
  appsettings.Local.json  YOUR per-machine slot map (SLOT1..n -> COM ports + Pi);
                          edit this. --install carries it into Program Files.

LOGS
  C:\ProgramData\Workbench-Bridge\logs
    service-*.log, cli-*.log, port-COMxx-*.log  (hourly roll, 3-day purge)

FLAGS accept Windows or Unix styles:  --help / -h / -? / /?,   --version / -v.
No arguments: the Service runs the console (elevates); the CLI shows this help.
