"""Heap-Leak-Test: jagt eine komplette Firmware (Block 0 + Block 1) im Stil
des C#-DaliBootloaders als 32-bit DALI-Frames durch den Bus.

Kein Bootloader auf dem Bus — Frames werden fire-and-forget gesendet
(waitForAnswer=False). Wir wollen nur sehen, ob das Gateway während des
Streams einen Heap-Leak zeigt (Serial-Monitor).
"""
import asyncio, json, time, websockets

GATEWAY = "ws://192.168.178.131"
FIRMWARE_PATH = (
    r"E:\_DATEN\Alexander\Projekte\_Projektordner\_HomeAutomation\DALI\EVG"
    r"\OpenDALI_EVG\Firmware\.pio\build\genericCH32V003F4P6\firmware.bin"
)
PACE_S = 0.05  # 50 ms zwischen Frames

# IEC 62386-105 opcodes (aus DaliBootloader.cs)
OP_BEGIN_BLOCK = 0xCB
OP_BLOCK_DATA = 0xBD

# Block-0-Felder
BLOCK0_SIZE = 64
BLOCK0_GTIN_OFFSET = 5
BLOCK0_DEVICE_KEY_OFFSET = 0x2B

# Block-1+ Header
BLOCK_HEADER_SIZE = 15
BYTES_PER_FRAME = 3

# Beispiel-GTIN aus MainForm-Default (irrelevant für RX-Test, Hauptsache 6 Byte)
GTIN = bytes.fromhex("3452334E0CAD")
EVG_MODE_ID = 0x01


def build_block0(gtin: bytes, evg_mode_id: int) -> bytes:
    block = bytearray(b"\xFF" * BLOCK0_SIZE)
    block[0] = 0x00
    block[1] = BLOCK0_SIZE - 2
    block[BLOCK0_GTIN_OFFSET:BLOCK0_GTIN_OFFSET + len(gtin)] = gtin
    block[BLOCK0_DEVICE_KEY_OFFSET] = evg_mode_id
    return bytes(block)


def build_firmware_block(firmware: bytes) -> bytes:
    data_size = len(firmware)
    block = bytearray(BLOCK_HEADER_SIZE + data_size + 2)
    block[0] = (data_size >> 8) & 0xFF
    block[1] = data_size & 0xFF
    # Header-Bytes 2..14 bleiben 0
    block[BLOCK_HEADER_SIZE:BLOCK_HEADER_SIZE + data_size] = firmware
    # CRC-Bytes bleiben 0 (Bootloader verifiziert nicht)
    return bytes(block)


def chunk_block_to_frames(block: bytes) -> list[bytes]:
    frames = []
    for i in range(0, len(block), BYTES_PER_FRAME):
        b1 = block[i]
        b2 = block[i + 1] if i + 1 < len(block) else 0xFF
        b3 = block[i + 2] if i + 2 < len(block) else 0xFF
        frames.append(bytes([OP_BLOCK_DATA, b1, b2, b3]))
    return frames


async def drain(ws, stop_event, stats):
    while not stop_event.is_set():
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=0.1)
            parsed = json.loads(raw)
            t = parsed.get("type", "?")
            if t == "daliFrame":
                stats["acks"] += 1
            elif t == "daliMonitor":
                stats["monitor"] += 1
            else:
                stats["other"] += 1
        except asyncio.TimeoutError:
            continue
        except websockets.ConnectionClosed:
            break


async def send_frame_32(ws, payload: bytes):
    """Sendet ein 4-Byte-Payload als 32-bit DALI-Frame (fire-and-forget)."""
    msg = {
        "type": "daliFrame",
        "data": {
            "line": 0,
            "numberOfBits": 32,
            "mode": {"sendTwice": False, "waitForAnswer": False, "priority": 3},
            "daliData": list(payload),
        },
    }
    await ws.send(json.dumps(msg))


async def main():
    # Firmware lesen
    with open(FIRMWARE_PATH, "rb") as f:
        firmware = f.read()
    print(f"Firmware: {FIRMWARE_PATH}")
    print(f"  Größe: {len(firmware)} Byte\n")

    # Frames vorberechnen
    block0 = build_block0(GTIN, EVG_MODE_ID)
    block1 = build_firmware_block(firmware)
    block0_frames = chunk_block_to_frames(block0)
    block1_frames = chunk_block_to_frames(block1)

    total = 1 + len(block0_frames) + 1 + len(block1_frames)
    eta_s = total * PACE_S
    print(f"Plan:")
    print(f"  BEGIN BLOCK 0           : 1 Frame")
    print(f"  Block 0 Daten ({BLOCK0_SIZE}B)     : {len(block0_frames)} Frames")
    print(f"  BEGIN BLOCK 1           : 1 Frame")
    print(f"  Block 1 Daten ({len(block1)}B): {len(block1_frames)} Frames")
    print(f"  ---")
    print(f"  Total                   : {total} Frames (~{eta_s:.0f}s @ {PACE_S*1000:.0f}ms Pacing)\n")

    async with websockets.connect(GATEWAY) as ws:
        greeting = await ws.recv()
        print(f"Connected. Greeting head: {greeting[:80]}…\n")

        stats = {"acks": 0, "monitor": 0, "other": 0}
        stop = asyncio.Event()
        drain_task = asyncio.create_task(drain(ws, stop, stats))

        t0 = time.time()

        # === Phase 1: BEGIN BLOCK 0 ===
        print(">>> BEGIN BLOCK 0")
        await send_frame_32(ws, bytes([OP_BEGIN_BLOCK, 0x00, 0x00, 0x00]))
        await asyncio.sleep(PACE_S)

        # === Phase 2: Block 0 Daten ===
        print(f">>> Block 0: {len(block0_frames)} Frames")
        for idx, frame in enumerate(block0_frames, 1):
            await send_frame_32(ws, frame)
            await asyncio.sleep(PACE_S)
        print(f"    ...{len(block0_frames)} ok")

        # === Phase 3: BEGIN BLOCK 1 ===
        print(">>> BEGIN BLOCK 1")
        await send_frame_32(ws, bytes([OP_BEGIN_BLOCK, 0x00, 0x00, 0x01]))
        await asyncio.sleep(PACE_S)

        # === Phase 4: Firmware-Daten ===
        print(f">>> Block 1 (Firmware): {len(block1_frames)} Frames")
        log_step = max(1, len(block1_frames) // 20)
        for idx, frame in enumerate(block1_frames, 1):
            await send_frame_32(ws, frame)
            if idx % log_step == 0 or idx == len(block1_frames):
                pct = idx * 100 // len(block1_frames)
                print(f"    {idx}/{len(block1_frames)} ({pct}%)  acks={stats['acks']} mon={stats['monitor']}")
            await asyncio.sleep(PACE_S)

        elapsed = time.time() - t0
        print(f"\nFertig in {elapsed:.1f}s ({total/elapsed:.1f} Frames/s)")

        # Restliche Monitor-Frames einsammeln
        await asyncio.sleep(2)
        stop.set()
        await drain_task

        print(f"\nGesamt-Bilanz:")
        print(f"  Frames gesendet : {total}")
        print(f"  daliFrame acks  : {stats['acks']}")
        print(f"  daliMonitor     : {stats['monitor']}")
        print(f"  sonstige        : {stats['other']}")
        print("\nJetzt im Serial-Monitor [DALI-TX] free_heap=… vor und nach diesem Run vergleichen.")


asyncio.run(main())
