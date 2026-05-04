namespace DALI_Updater;

public partial class MainForm : Form
{
    private DaliGateway? _gateway;
    private DaliBootloader? _bootloader;
    private CancellationTokenSource? _updateCts;

    public MainForm()
    {
        InitializeComponent();
    }

    private async void btnConnect_Click(object? sender, EventArgs e)
    {
        if (_gateway?.IsConnected == true)
        {
            await _gateway.DisconnectAsync();
            _gateway.Dispose();
            _gateway = null;
            btnConnect.Text = "Connect";
            SetControlsEnabled(false);
            return;
        }

        try
        {
            _gateway = new DaliGateway();
            _gateway.OnLog += msg => BeginInvoke(() => Log(msg));
            // OnConnectionChanged removed in simplified gateway

            await _gateway.ConnectAsync(txtGatewayIp.Text);
        }
        catch (Exception ex)
        {
            Log($"Connection failed: {ex.Message}");
            _gateway?.Dispose();
            _gateway = null;
        }
    }

    private void btnBrowse_Click(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Select firmware binary"
        };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            txtFirmwarePath.Text = ofd.FileName;
            var info = new FileInfo(ofd.FileName);
            Log($"Selected: {info.Name} ({info.Length} bytes)");
        }
    }

    private async void btnStartUpdate_Click(object? sender, EventArgs e)
    {
        if (_gateway == null || !_gateway.IsConnected)
        {
            Log("ERROR: Not connected to gateway");
            return;
        }

        if (string.IsNullOrWhiteSpace(txtFirmwarePath.Text) || !File.Exists(txtFirmwarePath.Text))
        {
            Log("ERROR: Select a valid firmware file");
            return;
        }

        if (!byte.TryParse(txtShortAddress.Text, out byte shortAddr) || shortAddr > 63)
        {
            Log("ERROR: Short address must be 0-63");
            return;
        }

        byte[] firmware;
        try
        {
            firmware = await File.ReadAllBytesAsync(txtFirmwarePath.Text);
        }
        catch (Exception ex)
        {
            Log($"ERROR reading file: {ex.Message}");
            return;
        }

        if (firmware.Length > 32640)
        {
            Log($"ERROR: Firmware too large ({firmware.Length} bytes, max 32640)");
            return;
        }

        // Parse GTIN from hex textbox
        byte[] gtin;
        try
        {
            gtin = Convert.FromHexString(txtGtin.Text.Replace(" ", "").Replace("0x", ""));
            if (gtin.Length != 6) throw new FormatException("Must be 6 bytes");
        }
        catch
        {
            Log("ERROR: GTIN must be 6 bytes hex (e.g. 3452334E0CAD)");
            return;
        }

        byte evgModeId = (byte)numEvgMode.Value;

        btnStartUpdate.Enabled = false;
        btnCancel.Enabled = true;
        progressBar.Value = 0;
        _updateCts = new CancellationTokenSource();

        _bootloader = new DaliBootloader(_gateway);
        _bootloader.OnLog += msg => BeginInvoke(() => Log(msg));
        _bootloader.OnProgress += (cur, total) => BeginInvoke(() =>
        {
            if (total > 0)
                progressBar.Value = Math.Min(100, cur * 100 / total);
        });

        try
        {
            var success = await _bootloader.UpdateFirmwareAsync(
                firmware, shortAddr, gtin, evgModeId, _updateCts.Token);

            progressBar.Value = success ? 100 : 0;
            Log(success ? "=== UPDATE SUCCESSFUL ===" : "=== UPDATE FAILED ===");
        }
        catch (OperationCanceledException)
        {
            Log("Update cancelled by user");
            progressBar.Value = 0;
        }
        catch (Exception ex)
        {
            Log($"Update error: {ex.Message}");
            progressBar.Value = 0;
        }
        finally
        {
            btnStartUpdate.Enabled = true;
            btnCancel.Enabled = false;
            _updateCts = null;
        }
    }

    private void btnCancel_Click(object? sender, EventArgs e)
    {
        _updateCts?.Cancel();
    }

    private void btnClearLog_Click(object? sender, EventArgs e)
    {
        txtLog.Clear();
    }

    private void Log(string message)
    {
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
    }

    private void SetControlsEnabled(bool connected)
    {
        btnStartUpdate.Enabled = connected;
        grpUpdate.Enabled = connected;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _updateCts?.Cancel();
        _gateway?.Dispose();
        base.OnFormClosing(e);
    }
}
