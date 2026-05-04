namespace DALI_Updater;

/// <summary>
/// IEC 62386-105 firmware update protocol implementation.
/// Drives the bootloader via DALI 32-bit forward frames sent through the gateway.
/// </summary>
public class DaliBootloader
{
    // IEC 62386-105 opcodes
    private const byte OP_STANDARD = 0xFB;
    private const byte OP_BEGIN_BLOCK = 0xCB;
    private const byte OP_BLOCK_DATA = 0xBD;
    private const byte ADDR_FW_UPDATE = 0xBF;

    // Commands (byte 2 of standard frame)
    private const byte CMD_START_FW_TRANSFER = 0x00;
    private const byte CMD_RESTART_FW = 0x01;
    private const byte CMD_FINISH_FW_UPDATE = 0x03;
    private const byte CMD_QUERY_FW_FEATURES = 0x05;
    private const byte CMD_QUERY_FW_RUNNING = 0x07;
    private const byte CMD_QUERY_BLOCK_FAULT = 0x08;

    // Block 0 structure
    private const int BLOCK0_GTIN_OFFSET = 5;
    private const int BLOCK0_GTIN_LENGTH = 6;
    private const int BLOCK0_DEVICE_KEY_OFFSET = 0x2B;
    private const int BLOCK0_SIZE = 64;

    // Block 1+ structure
    private const int BLOCK_HEADER_SIZE = 15;
    private const int BYTES_PER_FRAME = 3;

    // Timing
    private const int RESPONSE_TIMEOUT_MS = 2000;

    private readonly DaliGateway _gateway;
    private byte _deviceAddress;

    public event Action<string>? OnLog;
    public event Action<int, int>? OnProgress; // (current, total)

    public DaliBootloader(DaliGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// Run the full firmware update sequence.
    /// </summary>
    public async Task<bool> UpdateFirmwareAsync(
        byte[] firmwareBytes,
        byte shortAddress,
        byte[] gtin,
        byte evgModeId,
        CancellationToken ct = default)
    {
        _deviceAddress = (byte)(shortAddress << 1); // 32-bit gear address

        // Phase 1+2: SKIPPED for RX test bootloader (no protocol responses)
        OnLog?.Invoke("Phase 1+2: SKIPPED (RX test mode)");

        // Phase 3: Send firmware data (Block 1..N)
        OnLog?.Invoke($"Phase 3: Transferring firmware ({firmwareBytes.Length} bytes)...");

        if (!await SendFirmwareBlocksAsync(firmwareBytes, ct))
        {
            OnLog?.Invoke("ERROR: Firmware transfer failed");
            return false;
        }
        OnLog?.Invoke("  Firmware transfer complete");

        // Phase 4: SKIPPED for RX test bootloader
        OnLog?.Invoke("Phase 4: SKIPPED (RX test mode)");

        return true;
    }

    private async Task<byte?> QueryFwFeaturesAsync(CancellationToken ct)
    {
        var frame = new byte[] { _deviceAddress, OP_STANDARD, CMD_QUERY_FW_FEATURES, 0x00 };
        return await _gateway.SendAndWaitResponseAsync(frame, ct: ct);
    }

    private async Task<bool> StartFwTransferAsync(CancellationToken ct)
    {
        var frame = new byte[] { _deviceAddress, OP_STANDARD, CMD_START_FW_TRANSFER, 0x00 };
        // sendTwice handles config repeat automatically
        var response = await _gateway.SendAndWaitResponseAsync(frame, sendTwice: true, ct: ct);
        return response == 0xFF; // YES
    }

    private async Task<bool?> QueryFwUpdateRunningAsync(CancellationToken ct)
    {
        var frame = new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_QUERY_FW_RUNNING, 0x00 };
        var response = await _gateway.SendAndWaitResponseAsync(frame, ct: ct);
        return response == 0xFF ? true : response == null ? null : false;
    }

    private async Task<bool> SendBlock0Async(byte[] gtin, byte evgModeId, CancellationToken ct)
    {
        // BEGIN BLOCK 0
        await _gateway.SendDaliFrameAsync(
            new byte[] { OP_BEGIN_BLOCK, 0x00, 0x00, 0x00 }, ct: ct);
        /* no delay — SendDaliFrameAsync waits for gateway confirmation */

        // Build Block 0 data (64 bytes)
        var block0 = new byte[BLOCK0_SIZE];
        Array.Fill(block0, (byte)0xFF);

        // Header bytes
        block0[0] = 0x00;
        block0[1] = (byte)(BLOCK0_SIZE - 2);

        // GTIN at offset 5
        Array.Copy(gtin, 0, block0, BLOCK0_GTIN_OFFSET, Math.Min(gtin.Length, BLOCK0_GTIN_LENGTH));

        // Device key byte 0 (EVG mode ID) at offset 0x2B
        block0[BLOCK0_DEVICE_KEY_OFFSET] = evgModeId;

        // Send block data in 3-byte chunks
        for (int i = 0; i < block0.Length; i += BYTES_PER_FRAME)
        {
            var chunk = new byte[4];
            chunk[0] = OP_BLOCK_DATA;
            chunk[1] = block0[i];
            chunk[2] = (i + 1 < block0.Length) ? block0[i + 1] : (byte)0xFF;
            chunk[3] = (i + 2 < block0.Length) ? block0[i + 2] : (byte)0xFF;
            await _gateway.SendDaliFrameAsync(chunk, ct: ct);
            /* no delay — SendDaliFrameAsync waits for gateway confirmation */
        }

        // Query block fault
        await Task.Delay(50, ct);
        return await QueryBlockFaultOkAsync(ct);
    }

    private async Task<bool> SendFirmwareBlocksAsync(byte[] firmware, CancellationToken ct)
    {
        // Block format: [size_hi][size_lo][13 header bytes][firmware data][2 CRC bytes]
        int dataSize = firmware.Length;
        var blockData = new byte[BLOCK_HEADER_SIZE + dataSize + 2];
        blockData[0] = (byte)(dataSize >> 8);
        blockData[1] = (byte)(dataSize & 0xFF);
        // Header bytes 2-14 (zeros)
        Array.Copy(firmware, 0, blockData, BLOCK_HEADER_SIZE, dataSize);
        // CRC bytes left as 0 (not verified by bootloader)

        // BEGIN BLOCK 1
        await _gateway.SendDaliFrameAsync(
            new byte[] { OP_BEGIN_BLOCK, 0x00, 0x00, 0x01 }, ct: ct);
        /* no delay — SendDaliFrameAsync waits for gateway confirmation */

        // Send in 3-byte chunks
        int totalFrames = (blockData.Length + BYTES_PER_FRAME - 1) / BYTES_PER_FRAME;
        for (int i = 0; i < blockData.Length; i += BYTES_PER_FRAME)
        {
            var chunk = new byte[4];
            chunk[0] = OP_BLOCK_DATA;
            chunk[1] = blockData[i];
            chunk[2] = (i + 1 < blockData.Length) ? blockData[i + 1] : (byte)0xFF;
            chunk[3] = (i + 2 < blockData.Length) ? blockData[i + 2] : (byte)0xFF;
            await _gateway.SendDaliFrameAsync(chunk, ct: ct);

            int frameIndex = i / BYTES_PER_FRAME;
            if (frameIndex % 50 == 0)
                OnProgress?.Invoke(frameIndex, totalFrames);

            /* no delay — SendDaliFrameAsync waits for gateway confirmation */
        }

        OnProgress?.Invoke(totalFrames, totalFrames);

        // Query block fault
        await Task.Delay(100, ct);
        return await QueryBlockFaultOkAsync(ct);
    }

    private async Task<bool> QueryBlockFaultOkAsync(CancellationToken ct)
    {
        var frame = new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_QUERY_BLOCK_FAULT, 0x00 };
        var response = await _gateway.SendAndWaitResponseAsync(frame, ct: ct);

        // null = no response (silence) = no fault = OK
        // 0xFF = YES = fault detected
        if (response == null) return true;
        if (response == 0xFF)
        {
            OnLog?.Invoke("  BLOCK FAULT detected!");
            return false;
        }
        return true;
    }

    private async Task FinishFwUpdateAsync(CancellationToken ct)
    {
        var frame = new byte[] { ADDR_FW_UPDATE, OP_STANDARD, CMD_FINISH_FW_UPDATE, 0x00 };
        // sendTwice for config repeat — device may reboot before responding
        await _gateway.SendAndWaitResponseAsync(frame, sendTwice: true, timeoutMs: 3000, ct: ct);
    }
}
