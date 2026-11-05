using System.Text.Json.Nodes;
using TerminalWindows.Guest;

// Usage (inside the VM only):
//   TerminalWindowsGuest.exe install --source <dir>   (run once from autounattend FirstLogonCommands, elevated)
//   TerminalWindowsGuest.exe run                      (started at logon by Task Scheduler, highest privileges)
//   TerminalWindowsGuest.exe --version

if (args.Length > 0 && args[0] is "--version" or "version")
{
    Console.WriteLine(typeof(HelperServer).Assembly.GetName().Version);
    return 0;
}

var (isVBox, manufacturer, model) = VmCheck.Detect();
if (!isVBox)
{
    // Guard against running on the host by mistake. NOT a security guarantee (SMBIOS can be spoofed).
    Console.Error.WriteLine($"TerminalWindowsGuest runs only inside a VirtualBox VM (found '{manufacturer}' / '{model}'). Exiting.");
    return 2;
}

Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

string mode = args.Length > 0 ? args[0] : "run";
switch (mode)
{
    case "install":
    {
        int i = Array.IndexOf(args, "--source");
        var source = i >= 0 && i + 1 < args.Length ? args[i + 1] : AppContext.BaseDirectory;
        return Installer.Run(source);
    }

    case "run":
    {
        Directory.CreateDirectory(GuestPaths.LogDir);
        Log.FilePath = Path.Combine(GuestPaths.LogDir, "helper.log");
        if (SelfUpdater.UpdateInProgress())
        {
            Log.Write("helper update in progress; exiting");
            return 0;
        }
        using var mutex = new Mutex(true, @"Local\terminal-windows-guest", out bool first);
        if (!first)
        {
            Log.Write("another helper instance is running; exiting");
            return 0;
        }
        GuestConfig config;
        try { config = GuestConfig.Load(GuestPaths.ConfigFile); }
        catch (Exception ex)
        {
            Log.Write("cannot load config: " + ex.Message);
            return 3;
        }

        string? stateJson = null;
        try { stateJson = File.ReadAllText(GuestPaths.StateFile); } catch { }
        var bootTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var gate = SetupGate.Decide(stateJson, bootTime);
        if (gate == SetupGateDecision.CompleteNowAndServe)
        {
            // First start after the post-install reboot: apply the requested resolution once.
            try { HelperServer.SetResolution(config.Width, config.Height); } catch (Exception ex) { Log.Write("resolution: " + ex.Message); }
            Installer.WriteState("complete");
        }
        else if (gate == SetupGateDecision.Wait)
        {
            Log.Write($"setup not finished (state {stateJson ?? "missing"}, boot {bootTime:o}); not serving yet");
            return 0;
        }

        while (true)
        {
            try
            {
                await new HelperServer(config).RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // HelperServer stops its listener in a finally block, so the port is free again here.
                Log.Write("server crashed, restarting in 3s: " + ex);
                await Task.Delay(3000);
            }
        }
    }

    default:
        Console.Error.WriteLine($"Unknown mode '{mode}'");
        return 1;
}
