using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DALI_Updater;

/// <summary>
/// WebSocket client for the OpenKNX GW-REG1-Dali gateway.
/// Simple synchronous send→receive loop — no background tasks.
/// </summary>
public class DaliGateway : IDisposable
{
    private ClientWebSocket? _ws;
    private readonly byte[] _rxBuf = new byte[4096];

    public event Action<string>? OnLog;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string ip, CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"ws://{ip}"), ct);
        OnLog?.Invoke($"Connected to ws://{ip}/");

        var greeting = await ReceiveOneAsync(ct);
        if (greeting != null)
            OnLog?.Invoke($"Gateway: {greeting}");
    }

    public async Task DisconnectAsync()
    {
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
        OnLog?.Invoke("Disconnected");
    }

    /// <summary>
    /// Send a DALI frame, then read the gateway response(s) before returning.
    /// Returns the "result" field from the daliFrame response, or -1 on timeout.
    /// For waitForAnswer frames, also returns any backward frame data via out parameter.
    /// </summary>
    public async Task<int> SendAndReceiveAsync(byte[] daliData,
        bool waitForAnswer = false, bool sendTwice = false,
        int timeoutMs = 5000, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected");

        // Build and send the JSON message
        var msg = JsonSerializer.Serialize(new
        {
            type = "daliFrame",
            data = new
            {
                line = 0,
                numberOfBits = daliData.Length * 8,
                mode = new { sendTwice, waitForAnswer, priority = 3 },
                daliData = daliData.Select(b => (int)b).ToArray()
            }
        });

        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);

        // Read responses until we get the daliFrame result
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        int frameResult = -1;
        try
        {
            while (true)
            {
                var json = await ReceiveOneAsync(cts.Token);
                if (json == null) break;

                var (type, result, data) = ParseResponse(json);

                if (type == "daliFrame")
                {
                    frameResult = result;
                    // For sendTwice, wait for second confirmation
                    if (sendTwice && frameResult == 0)
                    {
                        sendTwice = false; // only wait once more
                        continue;
                    }
                    break;
                }
                // daliMonitor/daliAnswer — log and continue waiting
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout waiting for response — that's OK for 32-bit frames
        }

        return frameResult;
    }

    /// <summary>
    /// Send a DALI frame and wait for an 8-bit backward frame response.
    /// </summary>
    public async Task<byte?> SendAndWaitResponseAsync(byte[] daliData,
        bool sendTwice = false, int timeoutMs = 2000,
        CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected");

        var msg = JsonSerializer.Serialize(new
        {
            type = "daliFrame",
            data = new
            {
                line = 0,
                numberOfBits = daliData.Length * 8,
                mode = new { sendTwice, waitForAnswer = true, priority = 3 },
                daliData = daliData.Select(b => (int)b).ToArray()
            }
        });

        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);

        // Read responses — look for daliMonitor backward frame or daliAnswer
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            while (true)
            {
                var json = await ReceiveOneAsync(cts.Token);
                if (json == null) break;

                var (type, result, data) = ParseResponse(json);

                if (type == "backward")
                {
                    return (byte)data;
                }
                if (type == "daliAnswer")
                {
                    if (result == 8) return (byte)data;
                    return null; // no answer
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout = no response = silence
        }

        return null;
    }

    /// <summary>
    /// Simple fire-and-forget send. Reads and discards the gateway response.
    /// </summary>
    public async Task SendDaliFrameAsync(byte[] daliData,
        bool waitForAnswer = false, bool sendTwice = false,
        CancellationToken ct = default)
    {
        await SendAndReceiveAsync(daliData, waitForAnswer, sendTwice, ct: ct);
    }

    private async Task<string?> ReceiveOneAsync(CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return null;
        var result = await _ws.ReceiveAsync(_rxBuf, ct);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        return Encoding.UTF8.GetString(_rxBuf, 0, result.Count);
    }

    /// <summary>
    /// Parse a gateway JSON response. Returns (type, result, data).
    /// type: "daliFrame", "daliAnswer", "daliMonitor", "backward", or "other"
    /// </summary>
    private (string type, int result, int data) ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            var d = root.GetProperty("data");

            switch (type)
            {
                case "daliFrame":
                {
                    int r = d.TryGetProperty("result", out var re) ? re.GetInt32() : -1;
                    return ("daliFrame", r, 0);
                }
                case "daliAnswer":
                {
                    int r = d.GetProperty("result").GetInt32();
                    int v = d.TryGetProperty("daliData", out var dd) ? dd.GetInt32() : 0;
                    return ("daliAnswer", r, v);
                }
                case "daliMonitor":
                {
                    bool isEcho = d.TryGetProperty("isEcho", out var e) && e.GetBoolean();
                    int bits = d.TryGetProperty("bits", out var b) ? b.GetInt32() : 0;
                    if (!isEcho && bits == 8 && d.TryGetProperty("data", out var md))
                    {
                        int val = md[0].GetInt32();
                        OnLog?.Invoke($"  << backward frame: 0x{val:X2}");
                        return ("backward", 8, val);
                    }
                    return ("daliMonitor", 0, 0);
                }
            }
            return ("other", 0, 0);
        }
        catch
        {
            return ("error", -1, 0);
        }
    }

    public void Dispose()
    {
        _ws?.Dispose();
    }
}
