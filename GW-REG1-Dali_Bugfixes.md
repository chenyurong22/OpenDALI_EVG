# GW-REG1-Dali — Bug-Fixes (gefunden bei IEC 62386-105 Firmware-Update)

**Stand:** 2026-05-04
**Basis:** GW-REG1-Dali v0.9.6 (`https://github.com/OpenKNX/GW-REG1-Dali`)
**Bibliothek:** `dali-iot-gateway` (`https://github.com/thewhobox/dali-iot-gateway`)

## Zusammenfassung

Bei der Implementierung des IEC 62386-105 Firmware-Update-Protokolls über DALI 32-Bit Forward Frames sind **vier echte Bugs** im Gateway gefunden worden. Sie betreffen bisher nicht primär die Standard-DALI-Nutzung (8-/16-Bit Frames), wirken sich aber bei dauerhaftem WebSocket-Datenverkehr und 32-Bit Frames sofort aus.

Die schwerwiegendste Auswirkung war ein deterministischer **Heap-Leak von ~300 Byte pro WebSocket-Nachricht**, der nach ca. 300 32-Bit-Frames zu einem Guru-Meditation-Crash führte (NULL-Pointer-Dereferenzierung in `sendRawWebsocket`).

---

## Fix 1 — Heap-Leak in `ws_async_send()` (Hauptursache)

**Datei:** `lib/dali-iot-gateway/src/IotGateway.cpp`
**Funktion:** `ws_async_send()`

### Problem

In `sendRawWebsocket()` werden zwei Speicherbereiche per `malloc()` allokiert:
1. Eine `async_resp_arg`-Struktur (~16 Byte)
2. Ein Buffer für den JSON-Payload (~150 Byte)

`ws_async_send()` ruft die WebSocket-Übertragung auf und gibt den Speicher frei — aber **es wurde nur `resp_arg` freigegeben, nicht `resp_arg->buffer`**. Pro gesendete WebSocket-Nachricht entstand somit ein Leak von typischerweise 150 Byte. Beim DALI-Firmware-Update werden 2 Nachrichten pro DALI-Frame versendet (`daliMonitor` + `daliFrame result`) → ~300 Byte Leak pro DALI-Frame.

Zusätzlich gab es einen frühen Return-Pfad (`httpd_get_client_list` Fehler), der **beide** Allokationen nicht freigab.

### Fix

Beide Allokationen am Ende der Funktion und im frühen Return-Pfad freigeben:

```cpp
static void ws_async_send(void *arg)
{
    // ... bisherige Sendelogik ...

    if (ret != ESP_OK) {
        free(resp_arg->buffer);   // NEU: war auf early-return-Pfad geleakt
        free(resp_arg);
        return;
    }

    // ... Sendeschleife ...

    free(resp_arg->buffer);       // NEU: war auf normalem Pfad geleakt
    free(resp_arg);
}
```

### Validierung

Vorher: ~300 Byte/Frame Leak; nach ca. 300 Frames Heap erschöpft → Crash.
Nachher: Restleak deutlich reduziert (~32 Byte/Frame, separate Ursache, keine weitere Untersuchung).

---

## Fix 2 — Fehlende NULL-Prüfungen in `sendRawWebsocket()`

**Datei:** `lib/dali-iot-gateway/src/IotGateway.cpp`
**Funktion:** `sendRawWebsocket()`

### Problem

Beide `malloc()`-Aufrufe in `sendRawWebsocket()` prüften den Rückgabewert nicht. Wenn der Heap erschöpft ist, gibt `malloc()` `NULL` zurück. Die folgende Zeile

```cpp
resp_arg->hd = &server;
```

dereferenziert `NULL` und löst einen ESP32 Hardware-Trap aus:

```
Guru Meditation Error: Core 1 panic'ed (StoreProhibited)
EXCVADDR: 0x00000000
```

Das Gateway startete neu — und beendete damit jede laufende WebSocket-Verbindung.

### Fix

Jeden `malloc()`-Rückgabewert prüfen und bei Fehlschlag sauber abbrechen:

```cpp
struct async_resp_arg *resp_arg = (struct async_resp_arg *)malloc(...);
if (resp_arg == NULL) {
    fd = -1;
    return;
}

// ...

char *buffer = (char *)malloc(length+1);
if (buffer == NULL) {
    free(resp_arg);
    fd = -1;
    return;
}
```

### Validierung

Auch wenn der Heap-Leak (Fix 1) den Auslöser entfernt hat, sind defensive NULL-Checks bei `malloc()` ohnehin Pflicht — sie verhindern künftige Crashes bei jeder Heap-Druck-Situation.

---

## Fix 3 — Speicherleck bei vollem httpd-Workqueue

**Datei:** `lib/dali-iot-gateway/src/IotGateway.cpp`
**Funktion:** `sendRawWebsocket()`

### Problem

`httpd_queue_work()` kann fehlschlagen (z. B. wenn der ESP-IDF-internen httpd-Workqueue voll ist). Der Rückgabewert wurde im Originalcode ignoriert. Bei Fehlschlag wird das Work-Item nicht ausgeführt → `ws_async_send()` läuft nicht → die beiden `malloc()`-Allokationen werden nicht freigegeben → Memory-Leak.

### Fix

```cpp
esp_err_t qret = httpd_queue_work(resp_arg->hd, ws_async_send, resp_arg);
if (qret != ESP_OK) {
    free(resp_arg->buffer);
    free(resp_arg);
}
```

### Validierung

Im Normalbetrieb tritt dieser Pfad nicht auf, aber unter Last (Firmware-Updates, viele gleichzeitige Clients) verhindert dieser Fix unbemerkten Speicherverlust.

---

## Fix 4 — WebSocket-Frametyp wird nicht geprüft

**Datei:** `lib/dali-iot-gateway/src/IotGateway.cpp`
**Funktion:** `ws_handler()`

### Problem

Der Handler übergibt jeden empfangenen WebSocket-Frame ungeprüft an `handleData()`, das den Inhalt mit `deserializeJson()` als JSON parst. Ankommende WebSocket-**Steuer-Frames** (z. B. Ping/Pong, Close) und Binär-Frames enthalten aber keinen JSON-Text und führen zu Fehlermeldungen wie

```
deserializeJson() failed: InvalidInput
```

Im Test trat dies in Verbindung mit einem clientseitigen Disconnect auf — der "Schluss-Steuer-Frame" wurde wie eine normale Datennachricht behandelt.

### Fix

Vor dem Aufruf von `handleData()` den Frametyp prüfen und nur TEXT-Frames als JSON-Daten interpretieren:

```cpp
ret = httpd_ws_recv_frame(req, &ws_pkt, ws_pkt.len);
if (ret != ESP_OK) { ... }

if (ws_pkt.type != HTTPD_WS_TYPE_TEXT) {
    free(buf);
    return ESP_OK;   // Steuer-/Binär-Frame ignorieren
}

gw->handleData(req, ws_pkt.payload);
```

### Validierung

Der Browser-Bus-Monitor sendet selten Ping-Frames, der `DALI_Updater` (.NET WebSocket) während Disconnects einen Close-Frame. Beide werden jetzt sauber ignoriert statt als JSON fehlzuparsen.

---

## Fix 5 — 32-Bit DALI-Frames wurden im JSON nur mit 3 Bytes übertragen

**Datei:** `lib/dali-iot-gateway/src/IotGateway.cpp`
**Funktion:** `receivedMonitor()`

### Problem

Die Indexierung des `daliData`-Arrays in der `daliMonitor`-JSON-Nachricht hat nur 8/16/24-Bit-Frames behandelt. Für 32-Bit-Frames blieb `indexStart = 1`, sodass nur die unteren 3 Bytes des 4-Byte-Frames gesendet wurden — das Opcode-Byte (`0xBD` für `BLOCK DATA`) fehlte:

```cpp
uint8_t indexStart = 1;
if(frame.size == 16)
    indexStart = 2;
else if(frame.size == 8)
    indexStart = 3;
// kein Fall für size == 32 → es werden nur 3 Bytes versendet
```

Der DALI-Bus-Monitor zeigte alle 32-Bit-Frames als „Framing Error" mit nur 3 Hex-Bytes an — was ursprünglich fälschlich als Decoder-Fehler interpretiert wurde. In Wirklichkeit hat der Decoder die 32-Bit-Frames korrekt empfangen, lediglich die JSON-Repräsentation war unvollständig.

### Fix

```cpp
uint8_t indexStart = 1;
if(frame.size == 32)
    indexStart = 0;       // alle 4 Bytes
else if(frame.size == 16)
    indexStart = 2;
else if(frame.size == 8)
    indexStart = 3;
```

### Validierung

Im Bus-Monitor erscheinen 32-Bit-Frames jetzt mit allen 4 Bytes `[0xBD, b1, b2, b3]`, was der Spezifikation für `BLOCK_DATA` (`0xBD`-Opcode + 3-Byte-Payload) entspricht.

---

## Build-Anpassungen (zusätzlich)

Damit das Projekt auf einer frischen Maschine gebaut werden konnte, waren zwei kleine Anpassungen außerhalb der Gateway-Logik nötig:

### `lib/OGM-Common/library.json`

Die Abhängigkeit `RTTStream` (SEGGER RTT für ARM/J-Link Debug) ist mit der Xtensa-Toolchain inkompatibel. Da sie für ESP32-Builds nicht benötigt wird, wurde sie entfernt:

```json
"dependencies": {
    "khoih-prog/TimerInterrupt_Generic": "^1.13.0",
    "nickgammon/Regexp": "^0.1.0",
    "robtillaart/ANSI": "^0.2.0"
    // RTTStream entfernt
}
```

### `lib/OGM-HardwareConfig/include/HardwareConfig/OpenKNX/REG1.h`

Im Block für `OKNXHW_REG1_FRONT_RGB` waren die Macros `PROG_LED_COLOR` etc. als `OpenKNX::Led::Color::Red` definiert — dieser Enum existiert in der vorliegenden OGM-Common-Version nicht. Die Macros erwarten an dieser Stelle drei RGB-Werte, die direkt an `Led::Serial::init(num, mgr, r, g, b)` weitergegeben werden:

```cpp
#define PROG_LED_COLOR  63, 0, 0       // statt OpenKNX::Led::Color::Red
#define INFO1_LED_COLOR 0, 63, 0
#define INFO2_LED_COLOR 0, 63, 0
#define INFO3_LED_COLOR 0, 63, 0
```

---

## Offene Punkte (für später)

Während des Tests wurde ein zusätzlicher kleiner Heap-Leak beobachtet: nach Anwendung der oben genannten Fixes verbleiben **~32 Byte pro DALI-Frame**, die anscheinend dauerhaft gehalten werden. Quelle noch nicht identifiziert (Kandidaten: Frame-Lifecycle in `_rxQueue` / `_txQueue`, ArduinoJson-interne Allokationen, lwIP-TCB-State, oder ein nicht aufgeräumter `sent`-Vector-Eintrag pro Session).

Bei einem typischen Firmware-Update mit ~3500 Frames bedeutet das ~110 KiB zusätzlicher Heap-Verbrauch. Mit den vorhandenen Fixes ist das aktuell nicht kritisch, sollte aber separat untersucht werden.
