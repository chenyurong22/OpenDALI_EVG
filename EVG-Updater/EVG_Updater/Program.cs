namespace EVG_Updater;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0)
            return RunCli(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// CLI mode for scripted firmware updates.
    /// Usage: EVG_Updater.exe &lt;firmware.bin&gt; [options]
    ///   --ip &lt;gateway_ip&gt;      Gateway IP (default: 192.168.178.131)
    ///   --addr &lt;0-63&gt;          DALI short address (default: 0)
    ///   --gtin &lt;hex&gt;           6-byte GTIN hex (default: 3452334E0CAD)
    ///   --mode &lt;1-8&gt;           EVG mode ID (default: 5 = RGBW)
    /// </summary>
    static async Task<int> RunCli(string[] args)
    {
        string? firmwarePath = null;
        string gatewayIp = "192.168.178.131";
        byte shortAddr = 0;
        string gtinHex = "3452334E0CAD";
        byte evgMode = 5;

        // Parse args
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ip" when i + 1 < args.Length:
                    gatewayIp = args[++i];
                    break;
                case "--addr" when i + 1 < args.Length:
                    if (!byte.TryParse(args[++i], out shortAddr) || shortAddr > 63)
                    {
                        Console.Error.WriteLine("ERROR: --addr must be 0-63");
                        return 1;
                    }
                    break;
                case "--gtin" when i + 1 < args.Length:
                    gtinHex = args[++i];
                    break;
                case "--mode" when i + 1 < args.Length:
                    if (!byte.TryParse(args[++i], out evgMode) || evgMode < 1 || evgMode > 8)
                    {
                        Console.Error.WriteLine("ERROR: --mode must be 1-8");
                        return 1;
                    }
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    if (!args[i].StartsWith("--") && firmwarePath == null)
                        firmwarePath = args[i];
                    else
                    {
                        Console.Error.WriteLine($"Unknown option: {args[i]}");
                        PrintUsage();
                        return 1;
                    }
                    break;
            }
        }

        if (firmwarePath == null)
        {
            Console.Error.WriteLine("ERROR: No firmware file specified");
            PrintUsage();
            return 1;
        }

        if (!File.Exists(firmwarePath))
        {
            Console.Error.WriteLine($"ERROR: File not found: {firmwarePath}");
            return 1;
        }

        byte[] gtin;
        try
        {
            gtin = Convert.FromHexString(gtinHex.Replace(" ", "").Replace("0x", ""));
            if (gtin.Length != 6) throw new FormatException();
        }
        catch
        {
            Console.Error.WriteLine("ERROR: GTIN must be 6 bytes hex (e.g. 3452334E0CAD)");
            return 1;
        }

        var firmware = await File.ReadAllBytesAsync(firmwarePath);
        if (firmware.Length > 32640)
        {
            Console.Error.WriteLine($"ERROR: Firmware too large ({firmware.Length} bytes, max 32640)");
            return 1;
        }

        Console.WriteLine($"DALI Firmware Updater (CLI)");
        Console.WriteLine($"  Gateway:  ws://{gatewayIp}");
        Console.WriteLine($"  Firmware: {firmwarePath} ({firmware.Length} bytes)");
        Console.WriteLine($"  Address:  {shortAddr}");
        Console.WriteLine($"  GTIN:     {gtinHex}");
        Console.WriteLine($"  EVG Mode: {evgMode}");
        Console.WriteLine();

        using var gateway = new DaliGateway();
        gateway.OnLog += msg => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {msg}");

        try
        {
            await gateway.ConnectAsync(gatewayIp);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Connection failed: {ex.Message}");
            return 1;
        }

        var bootloader = new DaliBootloader(gateway);
        bootloader.OnLog += msg => Console.WriteLine($"  {msg}");
        bootloader.OnProgress += (cur, total) =>
        {
            if (total > 0)
            {
                int pct = cur * 100 / total;
                Console.Write($"\r  Progress: {pct}% ({cur}/{total} frames)");
                if (cur == total) Console.WriteLine();
            }
        };

        var success = await bootloader.UpdateFirmwareAsync(firmware, shortAddr, gtin, evgMode);

        await gateway.DisconnectAsync();

        if (success)
        {
            Console.WriteLine("\nSUCCESS: Firmware update complete.");
            return 0;
        }
        else
        {
            Console.Error.WriteLine("\nFAILED: Firmware update did not complete.");
            return 2;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage: EVG_Updater <firmware.bin> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --ip <gateway_ip>    Gateway IP (default: 192.168.178.131)");
        Console.WriteLine("  --addr <0-63>        DALI short address (default: 0)");
        Console.WriteLine("  --gtin <hex>         6-byte GTIN hex (default: 3452334E0CAD)");
        Console.WriteLine("  --mode <1-8>         EVG mode ID (default: 5 = RGBW)");
        Console.WriteLine("  --help, -h           Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  EVG_Updater firmware.bin");
        Console.WriteLine("  EVG_Updater firmware.bin --addr 5 --mode 4");
        Console.WriteLine("  EVG_Updater firmware.bin --ip 10.0.0.50 --gtin 091201234567");
        Console.WriteLine();
        Console.WriteLine("Run without arguments to launch the GUI.");
    }
}
