# heartbeat

A tiny Arduino sketch for exercising a bridged slot end-to-end. On boot it prints
`BOOT: heartbeat ready - <BOARD_BANNER>`, then prints `TICK:<millis> <BOARD_BANNER>`
once a second and echoes anything you send back as `ECHO:<line>`. Use it to verify
**both** serial directions through a bridged slot, and to prove a flash landed on the
board you think it did.

## Verification banner (edit before flashing each board)

Near the top of `heartbeat.ino`:

```cpp
const char* BOARD_BANNER = "Me Grut ESP32-S3-Zero slot6";
```

Set this to a distinctive, per-board string before you compile/flash, e.g.
`"Me Grut Large-ESP32 slot5"` (SLOT5/COM45). It's a known-answer token you won't see
by accident — when it appears on the serial monitor you know **exactly which
board/slot** booted the firmware you just flashed, so a successful flash is
unambiguous.

## Flash it

Over the bridge, with the slot's **user** COM port selected — works for both classic
USB-UART boards (`/dev/ttyUSB*` on the Pi) **and** native-USB boards
(`/dev/ttyACM*`); see the main README's "Native-USB ESP32s" section:

```
esptool --port COM41 write-flash ...        # or just use Arduino IDE -> COM41
```

or build/upload straight from the Arduino IDE with the slot's user COM port selected
(e.g. COM41 for SLOT1). Recompile per chip family (esp32 / esp32s3 / …).

## Watch the heartbeat

Without the IDE, via the service's bridge:

```
workbenchbridge-cli logs -f -c COM41        # service must be running
```

or read the user COM port directly with any serial monitor at 115200 8N1.

## Boards on the bench

Reference inventory of the physical boards used with heartbeat, captured from
`esptool flash-id`. Set `BOARD_BANNER` to the matching string before flashing.

| Board (physical description) | Banner | Slot / COM | Chip | Crystal | MAC |
|---|---|---|---|---|---|
| Large ESP32 dev board — RPi heat-sink, yellow pin headers | `Me Grut Large-ESP32 slot5` | SLOT5 / COM45 | ESP32-D0WDQ6 rev v1.0 (CP2102, uart-bridge) | 40 MHz | `b4:e6:2d:c0:12:69` |
| Waveshare ESP32-S3-Zero — black USB-C cable, small copper heat-sink (was the `ttyACM0` SLOT2→SLOT6 board) | `Me Grut ESP32-S3-Zero slot6` | SLOT6 / COM46 | ESP32-S3 (QFN56) rev v0.2, USB-Serial/JTAG | 40 MHz | `34:b7:da:59:cf:00` |
| TTGO ESP32 LoRa v1 + OLED (SX127x + SSD1306, CP2102) | _set when flashed_ | SLOT8 / COM48 | ESP32-D0WDQ6 rev v1.0 (CP2102, uart-bridge) | **26 MHz** | `24:0a:c4:30:96:d0` |

> **Crystal note:** the TTGO LoRa boards use a **26 MHz** crystal (most ESP32 dev
> boards are 40 MHz). A heartbeat built for a 40 MHz target still flashes and runs,
> but its serial output comes back at a scaled baud (looks garbled). Build for the
> correct board target if you want readable output.
