namespace EVG_Updater;

/// <summary>
/// IEC 62386-105 firmware update protocol implementation.
/// Drives the bootloader via DALI 32-bit forward frames sent through the gateway.
/// Mirrors the proven Python full_update.py 4-phase flow.
/// </summary>
public class DaliBootloader
{
    // IEC 62386-105 opcodes
    private const byte OP_STANDARD     = 0xFB;
    private const byte OP_BEGIN_BLOCK  = 0xCB;
    private const byte OP_BLOCK_DATA   = 0xBD;
    private const byte ADDR_FW_UPDATE  = 0xBF;

    // Commands (byte 2 of standard frame)
    private const byte CMD_START_FW_TRANSFER = 0x00;
    private const byte CMD_FINISH_FW_UPDATE  = 0x03;
    private const byte CMD_QUERY_FW_FEATURES = 0x05;
    private const byte CMD_QUERY_FW_RUNNING  = 0x07;
    private const byte CMD_QUERY_BLOCK_FAULT = 0x08;

    // Block 0 layout
    private const int BLOCK0_SIZE                = 64;
    private const int BLOCK0_GTIN_OFFSET         = 5;
    private const int BLOCK0_GTIN_LENGTH         = 6;
    private const int BLOCK0_DEVICE_KEY_OFFSET   = 0x2B;
    private const int BLOCK0_FLETCHER_FA_OFFSET  = 0x2C;
    private const int BLOCK0_FLETCHER_FB_OFFSET  = 0x2D;

    // Block 1 layout: [size_hi][size_lo][13 zeros][firmware][2 CRC zeros]
    private const int BLOCK_HEADER_SIZE = 15;
    private const int BYTES_PER_FRAME   = 3;

    // Probe the bootloader periodically during firmware transfer — if it has
    // set BLOCK FAULT for any reason (heap exhausted, frame decoded weirdly,
    // EEPROM I2C nack, …) we'd rather find out at frame 128/3581 than spend
    // 3 minutes pumping bytes into a doomed transfer.
    private const int CHECK_INTERVAL = 128;

    private readonly DaliGateway _gateway;
    private byte _deviceAddress;

    public event Action<string>? OnLog;
    public event Action<int, int>? OnProgress; // (current, total)

    public DaliBootloader(DaliGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// Run the full firmware update sequence (Phases 1–4).
    /// </summary>
    public async Task<bool> UpdateFirmwareAsync(
        byte[] firmwareBytes,
        byte shortAddress,
        byte[] gtin,
        byte evgModeId,
        CancellationToken ct = default)
    {
        _deviceAddress = (byte)(shortAddress << 1); // 32-bit gear address byte

        OnLog?.Invoke($"Device addr=0x{_deviceAddress:X2}  GTIN={Convert.ToHexString(gtin)}  "
                    + $"mode=0x{evgModeId:X2}  fw={firmwareBytes.Length} bytes");

        if (!await Phase1EnterBootloaderAsync(ct)) return false;
        if (!await Phase2Block0Async(firmwareBytes, gtin, evgModeId, ct)) return false;
        if (!await Phase3FirmwareAsync(firmwareBytes, ct)) return false;
        await Phase4FinishAsync(ct);

        OnLog?.Invoke("*** Firmware update sequence complete ***");
        return true;
    }

    // ─── Phase 1: Query + Start FW Transfer ───────────────────────────────
    private async Task<bool> Phase1EnterBootloaderAsync(CancellationToken ct)
    {
        OnLog?.Invoke("[Phase 1] Query FW features...");
        var features = await _gateway.SendQueryAsync(
            new byte[] { _deviceAddress, OP_STANDARD, CMD_QUERY_FW_FEATURES, 0x00 }, ct: ct);
        if (features == null)
        {
            OnLog?.Invoke("  ERROR: no answer to QUERY FW UPDATE FEATURES");
            return false;
        }
        OnLog?.Invoke($"  features = 0x{features:X2}");

        OnLog?.Invoke("[Phase 1] START FW TRANSFER (sendTwice)...");
        var yes = await _gateway.SendQueryAsync(
            new byte[] { _deviceAddress, OP_STANDARD, CMD_START_FW_TRANSFER, 0x00 },
            sendTwice: true, timeoutMs: 3000, ct: ct);
        if (yes != 0xFF)
        {
            OnLog?.Invoke($"  ERROR: device did not respond YES (got {FormatByte(yes)})");
            return false;
        }
        OnLog?.Invoke("  device entered bootloader");

        OnLog?.Invoke("[Phase 1] Wait 500ms for bootloader init...");
        await Task.Delay(500, ct);

        OnLog?.Invoke("[Phase 1] QUERY FW UPDATE RUNNING...");
        var running = await _gateway.SendQueryAsync(
            new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_QUERY_FW_RUNNING, 0x00 }, ct: ct);
        if (running != 0xFF)
            OnLog?.Invoke($"  WARNING: no YES (got {FormatByte(running)}) — continuing anyway");
        else
            OnLog?.Invoke("  bootloader confirmed running");

        return true;
    }

    // ─── Phase 2: Send Block 0 (validation) ───────────────────────────────
    private async Task<bool> Phase2Block0Async(byte[] firmware, byte[] gtin, byte evgModeId, CancellationToken ct)
    {
        OnLog?.Invoke("[Phase 2] BEGIN BLOCK 0...");
        await _gateway.SendDataAsync(new byte[] { OP_BEGIN_BLOCK, 0x00, 0x00, 0x00 }, ct: ct);

        // Pre-compute Fletcher-16 over the firmware payload — bootloader checks
        // this against fa/fb it accumulates while receiving Block 1, before the
        // flash commit. Mismatch -> BLOCK FAULT, commit aborted.
        byte fa = 0, fb = 0;
        foreach (var x in firmware)
        {
            fa = (byte)((fa + x) & 0xFF);
            fb = (byte)((fb + fa) & 0xFF);
        }
        OnLog?.Invoke($"[Phase 2] Fletcher-16(fw) = fa=0x{fa:X2} fb=0x{fb:X2}");

        var block0 = new byte[BLOCK0_SIZE];
        Array.Fill(block0, (byte)0xFF);
        block0[0] = 0x00;
        block0[1] = (byte)(BLOCK0_SIZE - 2); // 0x3E
        Array.Copy(gtin, 0, block0, BLOCK0_GTIN_OFFSET, Math.Min(gtin.Length, BLOCK0_GTIN_LENGTH));
        block0[BLOCK0_DEVICE_KEY_OFFSET]  = evgModeId;
        block0[BLOCK0_FLETCHER_FA_OFFSET] = fa;
        block0[BLOCK0_FLETCHER_FB_OFFSET] = fb;

        OnLog?.Invoke("[Phase 2] Send 22 BLOCK_DATA frames...");
        for (int i = 0; i < block0.Length; i += BYTES_PER_FRAME)
        {
            var chunk = new byte[]
            {
                OP_BLOCK_DATA,
                block0[i],
                (i + 1 < block0.Length) ? block0[i + 1] : (byte)0xFF,
                (i + 2 < block0.Length) ? block0[i + 2] : (byte)0xFF,
            };
            await _gateway.SendDataAsync(chunk, ct: ct);
        }

        await _gateway.DrainInflightAsync(ct: ct);
        await Task.Delay(50, ct);

        OnLog?.Invoke("[Phase 2] QUERY BLOCK FAULT...");
        var fault = await _gateway.SendQueryAsync(
            new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_QUERY_BLOCK_FAULT, 0x00 }, ct: ct);
        if (fault == 0xFF)
        {
            OnLog?.Invoke("  ERROR: BLOCK FAULT detected (GTIN/mode mismatch?)");
            return false;
        }
        OnLog?.Invoke("  no fault -> Block 0 validated");
        return true;
    }

    // ─── Phase 3: Send Block 1 = firmware ─────────────────────────────────
    private async Task<bool> Phase3FirmwareAsync(byte[] firmware, CancellationToken ct)
    {
        OnLog?.Invoke($"[Phase 3] BEGIN BLOCK 1 ({firmware.Length} bytes)...");
        await _gateway.SendDataAsync(new byte[] { OP_BEGIN_BLOCK, 0x00, 0x00, 0x01 }, ct: ct);

        var block1 = new byte[BLOCK_HEADER_SIZE + firmware.Length + 2];
        block1[0] = (byte)((firmware.Length >> 8) & 0xFF);
        block1[1] = (byte)(firmware.Length & 0xFF);
        Array.Copy(firmware, 0, block1, BLOCK_HEADER_SIZE, firmware.Length);
        // Last 2 CRC bytes left as 0 (not verified by bootloader)

        int totalFrames = (block1.Length + BYTES_PER_FRAME - 1) / BYTES_PER_FRAME;
        OnLog?.Invoke($"[Phase 3] Send {totalFrames} BLOCK_DATA frames...");

        int sent = 0;
        for (int i = 0; i < block1.Length; i += BYTES_PER_FRAME)
        {
            var chunk = new byte[]
            {
                OP_BLOCK_DATA,
                block1[i],
                (i + 1 < block1.Length) ? block1[i + 1] : (byte)0xFF,
                (i + 2 < block1.Length) ? block1[i + 2] : (byte)0xFF,
            };
            await _gateway.SendDataAsync(chunk, ct: ct);
            sent++;

            if (sent % 50 == 0 || sent == totalFrames)
                OnProgress?.Invoke(sent, totalFrames);

            // After every CHECK_INTERVAL frames we drain in-flight, then send
            // QUERY BLOCK FAULT and see if anything comes back.
            // Silence (null) = healthy, 0xFF = abort early.
            if (sent % CHECK_INTERVAL == 0 && sent != totalFrames)
            {
                await _gateway.DrainInflightAsync(ct: ct);
                await Task.Delay(50, ct);
                var midFault = await _gateway.SendQueryAsync(
                    new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_QUERY_BLOCK_FAULT, 0x00 },
                    timeoutMs: 800, ct: ct);
                if (midFault == 0xFF)
                {
                    OnLog?.Invoke($"  ERROR: bootloader signalled BLOCK FAULT at frame "
                                + $"{sent}/{totalFrames} — aborting");
                    return false;
                }
                OnLog?.Invoke($"  check OK at frame {sent}/{totalFrames}");
            }
        }

        await _gateway.DrainInflightAsync(ct: ct);
        await Task.Delay(100, ct);

        OnLog?.Invoke("[Phase 3] Final QUERY BLOCK FAULT...");
        var fault = await _gateway.SendQueryAsync(
            new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_QUERY_BLOCK_FAULT, 0x00 }, ct: ct);
        if (fault == 0xFF)
        {
            OnLog?.Invoke("  ERROR: BLOCK FAULT on firmware block");
            return false;
        }
        OnLog?.Invoke("  no fault -> firmware block validated");
        return true;
    }

    // ─── Phase 4: FINISH FW UPDATE ────────────────────────────────────────
    private async Task Phase4FinishAsync(CancellationToken ct)
    {
        OnLog?.Invoke("[Phase 4] FINISH FW UPDATE (sendTwice; EVG reboots)...");
        // EVG reboot means it likely won't answer — SendQueryAsync returns
        // null on timeout, no throw. The gateway WS itself stays up.
        var res = await _gateway.SendQueryAsync(
            new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_FINISH_FW_UPDATE, 0x00 },
            sendTwice: true, timeoutMs: 3000, ct: ct);
        OnLog?.Invoke($"  finish answer: {FormatByte(res)}");
    }

    private static string FormatByte(byte? b) => b == null ? "<null>" : $"0x{b:X2}";
}
