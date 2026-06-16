// heartbeat.ino - dead-simple ESP32 serial test sketch for Workbench-Bridge.
//
// Purpose: confirm a slot's serial paths through the bridge WITHOUT flashing
// real firmware. It proves both directions:
//   * RX (ESP32 -> IDE):  a "TICK:" line once per second.
//   * TX (IDE -> ESP32):  whatever you type is echoed back as an "ECHO:" line.
//
// Serial: 115200 baud, 8N1.
//
// Flash a classic USB-UART ESP32 (shows on the Pi as /dev/ttyUSB*) for remote
// flashing over the bridge; native-USB boards (/dev/ttyACM*) usually can't be
// flashed remotely - see the README in this folder.

// Built-in LED defaults. Override these if your board uses a different pin or
// polarity. For ESP-01, confirm your module's LED wiring if LED_BUILTIN is not
// provided by the board package.
#if defined(LED_BUILTIN)
const int STATUS_LED_PIN = LED_BUILTIN;
#elif defined(ARDUINO_ARCH_AVR)
const int STATUS_LED_PIN = 13;  // Classic Arduino Uno/Nano.
#elif defined(ARDUINO_ARCH_ESP8266)
const int STATUS_LED_PIN = 2;   // Common ESP8266 fallback.
#else
const int STATUS_LED_PIN = 2;   // Common ESP32 fallback.
#endif

#if defined(ARDUINO_ARCH_ESP8266)
const bool STATUS_LED_ON_LEVEL = LOW;   // Many ESP8266 boards use active-low LEDs.
#else
const bool STATUS_LED_ON_LEVEL = HIGH;
#endif

const unsigned long TICK_FLASH_MS = 150;
const unsigned long RX_FLASH_MS = 100;
const int MAX_RX_BYTES_PER_LOOP = 32;

// ---------------------------------------------------------------------------
// EDIT ME before flashing each board.
// This distinctive banner prints at BOOT and on every TICK, so the serial
// monitor unambiguously confirms *which* board/slot you just flashed - a
// known-answer string you won't see by accident.
// Examples:
//   "Me Grut ESP32-S3-Zero slot6"      (Waveshare ESP32-S3-Zero,  COM46)
//   "Me Grut Large-ESP32 slot5"        (Large ESP32 dev board,    COM45)
// ---------------------------------------------------------------------------
const char* BOARD_BANNER = "Me Grut ESP32-S3-Zero slot6";

unsigned long lastTick = 0;
unsigned long ledOffAt = 0;
bool ledIsOn = false;
String rx;

void setLed(bool on) {
  if (STATUS_LED_PIN < 0) {
    return;
  }

  if (ledIsOn == on) {
    return;
  }

  digitalWrite(STATUS_LED_PIN, on ? STATUS_LED_ON_LEVEL : !STATUS_LED_ON_LEVEL);
  ledIsOn = on;
}

void flashLedFor(unsigned long durationMs) {
  setLed(true);
  ledOffAt = millis() + durationMs;
}

void updateLed(unsigned long now) {
  if (ledIsOn && (long)(now - ledOffAt) >= 0) {
    setLed(false);
  }
}

void setup() {
  pinMode(STATUS_LED_PIN, OUTPUT);
  setLed(false);

  Serial.begin(115200);
  delay(200);
  Serial.println();
  Serial.print("BOOT: heartbeat ready - ");
  Serial.println(BOARD_BANNER);
}

void loop() {
  unsigned long now = millis();
  updateLed(now);

  // Heartbeat once per second.
  if (now - lastTick >= 1000) {
    lastTick = now;
    Serial.print("TICK:");
    Serial.print(now);
    Serial.print(" ");
    Serial.println(BOARD_BANNER);
    flashLedFor(TICK_FLASH_MS);
  }

  // Echo incoming serial data back, one line per received line.
  int rxBytesHandled = 0;
  while (Serial.available() > 0 && rxBytesHandled < MAX_RX_BYTES_PER_LOOP) {
    char c = (char)Serial.read();
    rxBytesHandled++;
    flashLedFor(RX_FLASH_MS);

    if (c == '\n' || c == '\r') {
      if (rx.length() > 0) {
        Serial.print("ECHO:");
        Serial.println(rx);
        rx = "";
      }
    } else {
      rx += c;
      // Flush long lines so a sender that never sends a newline still echoes.
      if (rx.length() >= 80) {
        Serial.print("ECHO:");
        Serial.println(rx);
        rx = "";
      }
    }
  }

  updateLed(millis());
  delay(1);
}
