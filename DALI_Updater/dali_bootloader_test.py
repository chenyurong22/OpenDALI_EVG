"""
Test: Send DALI frames via Lunatone DALI-2 IoT WebSocket API.

API format (section 6.2.3):
{
  "type": "daliFrame",
  "data": {
    "line": 0,
    "numberOfBits": 16,
    "mode": {
      "sendTwice": false,
      "waitForAnswer": true,
      "priority": 3
    },
    "daliData": [addr, cmd]
  }
}
"""
import asyncio
import websockets
import json

GATEWAY_IP = "192.168.178.131"
SHORT_ADDR = 0

async def send_dali(ws, dali_data, bits=16, wait_answer=True, send_twice=False, label=""):
    msg = {
        "type": "daliFrame",
        "data": {
            "line": 0,
            "numberOfBits": bits,
            "mode": {
                "sendTwice": send_twice,
                "waitForAnswer": wait_answer,
                "priority": 3
            },
            "daliData": dali_data
        }
    }
    raw = json.dumps(msg)
    await ws.send(raw)
    hex_str = " ".join(f"{b:02X}" for b in dali_data)
    print(f"  TX [{bits}bit]: {hex_str}  // {label}")

async def recv_all(ws, timeout=2.0):
    responses = []
    try:
        while True:
            raw = await asyncio.wait_for(ws.recv(), timeout=timeout)
            msg = json.loads(raw)
            responses.append(msg)
            t = msg.get("type", "?")
            if t == "daliAnswer":
                d = msg.get("data", {})
                result = d.get("result", "?")
                dali = d.get("daliData", "?")
                print(f"  RX daliAnswer: result={result} data={dali}")
            elif t == "daliFrame":
                d = msg.get("data", {})
                result = d.get("result", "?")
                print(f"  RX daliFrame: result={result}")
            else:
                print(f"  RX {t}: {json.dumps(msg.get('data', {}), indent=2)}")
            timeout = 0.5
    except asyncio.TimeoutError:
        pass
    if not responses:
        print(f"  RX: (no response)")
    return responses

async def main():
    uri = f"ws://{GATEWAY_IP}"
    print(f"Connecting to {uri}...")

    async with websockets.connect(uri) as ws:
        greeting = json.loads(await ws.recv())
        gw = greeting.get("data", {})
        print(f"Connected: {gw.get('name', '?')} v{gw.get('version', '?')}")
        print()

        addr_byte = (SHORT_ADDR << 1) | 1  # 0x01 — short addr 0, command

        # Test 1: QUERY GEAR PRESENT (cmd 145) — should get 0xFF (YES)
        print("=== Test 1: QUERY GEAR PRESENT (16-bit) ===")
        await send_dali(ws, [addr_byte, 145], bits=16, wait_answer=True,
                        label="QUERY_GEAR_PRESENT addr=0")
        await recv_all(ws)
        print()

        # Test 2: QUERY ACTUAL LEVEL (cmd 160)
        print("=== Test 2: QUERY ACTUAL LEVEL (16-bit) ===")
        await send_dali(ws, [addr_byte, 160], bits=16, wait_answer=True,
                        label="QUERY_ACTUAL_LEVEL addr=0")
        await recv_all(ws)
        print()

        # Test 3: QUERY FW UPDATE FEATURES (32-bit IEC 62386-105)
        dev_addr_32 = SHORT_ADDR << 1  # 0x00 for gear
        print("=== Test 3: QUERY FW UPDATE FEATURES (32-bit) ===")
        await send_dali(ws, [dev_addr_32, 0xFB, 0x05, 0x00], bits=32, wait_answer=True,
                        label="QUERY_FW_FEATURES")
        await recv_all(ws)
        print()

        # Test 4: START FW TRANSFER (32-bit, config repeat via sendTwice)
        print("=== Test 4: START FW TRANSFER (32-bit, sendTwice) ===")
        await send_dali(ws, [dev_addr_32, 0xFB, 0x00, 0x00], bits=32,
                        wait_answer=True, send_twice=True,
                        label="START_FW_TRANSFER")
        await recv_all(ws, 3.0)
        print()

        # Check UART log
        print("Check the UART log (COM14) to see if the device entered bootloader!")

if __name__ == "__main__":
    asyncio.run(main())
