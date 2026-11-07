using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// <c>display NAME --hidpi | --standard</c>: a 2x display. HiDPI gives the guest twice the
/// pixels (2560x1440 for a 1280x720 layout) at 200% scaling, so the viewer shrinks a finely drawn picture and
/// text stays sharp. Applied live: no restart (apps that were already open may need reopening to be crisp).
/// </summary>
public static class Display
{
    /// <summary>Sets the guest's display scale now, without signing out (what Settings > Display does).
    /// SPI_GETLOGICALDPIOVERRIDE / SPI_SETLOGICALDPIOVERRIDE work in steps relative to the recommended scale.</summary>
    internal static string ScaleScript(int percent) => $$"""
        $Percent = {{percent}}
        Add-Type -Namespace TW -Name DpiScale -MemberDefinition '[DllImport("user32.dll", EntryPoint="SystemParametersInfoW")] public static extern bool GetRel(uint a, uint b, ref int c, uint d); [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")] public static extern bool SetRel(uint a, int b, System.IntPtr c, uint d);'
        $steps = @(100,125,150,175,200,225,250,300,350,400,450,500)
        $rel = 0
        [void][TW.DpiScale]::GetRel(0x9E, 0, [ref]$rel, 0)
        $cur = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction SilentlyContinue).AppliedDPI
        if (-not $cur) { $cur = 96 }
        $curIdx = [array]::IndexOf($steps, [int]([math]::Round($cur * 100 / 96)))
        if ($curIdx -lt 0) { $curIdx = 0 }
        $recIdx = [math]::Max(0, $curIdx - $rel)
        $target = [array]::IndexOf($steps, $Percent)
        if (-not [TW.DpiScale]::SetRel(0x9F, $target - $recIdx, [IntPtr]::Zero, 1)) { throw 'could not change the display scale' }
        "scale $Percent%"
        """;

    /// <summary>
    /// The screen size to ask for. machine.json records the real pixels (Width/Height) and the scale, so the
    /// layout size is Width/Height divided by the scale; HiDPI asks for twice that (the driver picks its closest mode).
    /// </summary>
    public static (int W, int H) Target(MachineConfig m, bool hidpi)
    {
        int lw = m.Width * 100 / Math.Max(100, m.Scale), lh = m.Height * 100 / Math.Max(100, m.Scale);
        return hidpi ? (lw * 2, lh * 2) : (lw, lh);
    }

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        bool hidpi = a.Flag("hidpi"), standard = a.Flag("standard");
        a.RejectUnknown();
        if (hidpi == standard)
        {
            Console.WriteLine(Ui.T($"{m.Name}: {(m.Scale >= 200 ? "HiDPI（2 倍）" : "標準")}。変えるときは --hidpi か --standard を付けます。",
                                   $"{m.Name}: {(m.Scale >= 200 ? "HiDPI (2x)" : "standard")}. Change it with --hidpi or --standard."));
            return 0;
        }
        await new MachineManager().EnsureRunningAsync(m);
        await using var c = await GuestClient.ConnectAsync(m);
        var r = await ApplyAsync(c, m, hidpi);
        Console.WriteLine(Ui.T($"{m.Name}: {(hidpi ? "HiDPI（2 倍）" : "標準")} — {r}", $"{m.Name}: {(hidpi ? "HiDPI (2x)" : "standard")} — {r}"));
        return 0;
    }

    /// <summary>Changes resolution and scale and records them; returns e.g. "2560x1440, 200%".</summary>
    public static async Task<string> ApplyAsync(GuestClient c, MachineConfig m, bool hidpi)
    {
        var (w, h) = Target(m, hidpi);
        var res = await c.CallAsync("set_resolution", new JsonObject { ["width"] = w, ["height"] = h }, TimeSpan.FromSeconds(30));
        if (res["ok"]?.GetValue<bool>() != true) throw new CliException($"The display driver refused {w}x{h}.");
        int gotW = res["width"]!.GetValue<int>(), gotH = res["height"]!.GetValue<int>();
        int percent = hidpi ? 200 : 100;
        JsonObject r;
        try
        {
            r = await c.CallAsync("exec", new JsonObject { ["script"] = ScaleScript(percent), ["timeout_s"] = 60 }, TimeSpan.FromSeconds(90));
        }
        catch (RpcException ex) when (ex.Message.Contains("Unknown op"))
        {
            throw new CliException($"The guest helper is too old for this. Run 'terminal-windows update-helper {m.Name}' first.");
        }
        if (r["exitCode"]?.GetValue<int>() != 0) throw new CliException("Scale change failed: " + (r["stderr"]?.GetValue<string>() ?? "").Trim());
        m.Scale = percent;
        (m.Width, m.Height) = (gotW, gotH);
        MachineStore.Save(m);
        return $"{gotW}x{gotH}, {percent}%";
    }
}
