using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// <c>tune NAME [--defender] [--yes]</c>: makes the GUEST lighter for AI work (visual effects off,
/// search indexing / SysMain / background apps off, high-performance power plan). With --defender it
/// also excludes the whole system drive from Microsoft Defender scanning, which lowers the VM's own
/// protection: acceptable only because the VM is disposable. Nothing on the host is changed.
/// </summary>
public static class Tune
{
    public static IReadOnlyList<(string Title, string Script)> Steps(bool defender)
    {
        var steps = new List<(string, string)>
        {
            ("Visual effects: best performance (no animations, no transparency)",
                """
                $ve = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects'
                New-Item -Path $ve -Force | Out-Null
                Set-ItemProperty $ve -Name VisualFXSetting -Value 2 -Type DWord
                Set-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name MinAnimate -Value '0'
                Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name UserPreferencesMask -Value ([byte[]](0x90,0x12,0x03,0x80,0x10,0x00,0x00,0x00)) -Type Binary
                $p = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
                New-Item -Path $p -Force | Out-Null
                Set-ItemProperty $p -Name EnableTransparency -Value 0 -Type DWord
                $a = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                Set-ItemProperty $a -Name TaskbarAnimations -Value 0 -Type DWord
                'ok (applies fully after the next sign-in)'
                """),
            ("Windows Search indexing: stop and disable",
                """
                Stop-Service WSearch -Force -ErrorAction SilentlyContinue
                Set-Service WSearch -StartupType Disabled
                'ok'
                """),
            ("SysMain (Superfetch): stop and disable",
                """
                Stop-Service SysMain -Force -ErrorAction SilentlyContinue
                Set-Service SysMain -StartupType Disabled
                'ok'
                """),
            ("Background apps: off",
                """
                $b = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications'
                New-Item -Path $b -Force | Out-Null
                Set-ItemProperty $b -Name GlobalUserDisabled -Value 1 -Type DWord
                $pol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy'
                New-Item -Path $pol -Force | Out-Null
                Set-ItemProperty $pol -Name LetAppsRunInBackground -Value 2 -Type DWord
                'ok'
                """),
            ("Power plan: high performance",
                """
                powercfg /setactive SCHEME_MIN
                if ($LASTEXITCODE -ne 0) { throw "powercfg failed ($LASTEXITCODE)" }
                'ok'
                """),
            ("Plain desktop: solid background, no Spotlight icon, widgets or Copilot button (smaller, calmer screenshots)",
                """
                $pol = 'HKCU:\Software\Policies\Microsoft\Windows\CloudContent'
                New-Item -Path $pol -Force | Out-Null
                Set-ItemProperty $pol -Name DisableSpotlightCollectionOnDesktop -Value 1 -Type DWord
                Set-ItemProperty $pol -Name DisableWindowsSpotlightFeatures -Value 1 -Type DWord
                $dsh = 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh'
                New-Item -Path $dsh -Force | Out-Null
                Set-ItemProperty $dsh -Name AllowNewsAndInterests -Value 0 -Type DWord
                $a = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
                Set-ItemProperty $a -Name ShowCopilotButton -Value 0 -Type DWord
                Set-ItemProperty $a -Name ShowTaskViewButton -Value 0 -Type DWord
                Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name SearchboxTaskbarMode -Value 1 -Type DWord
                # Solid dark-blue background instead of the photo (Spotlight off first, or it puts the photo back).
                $d = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers'
                New-Item -Path $d -Force | Out-Null
                Set-ItemProperty $d -Name BackgroundType -Value 1 -Type DWord
                Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name WallPaper -Value ''
                Set-ItemProperty 'HKCU:\Control Panel\Colors' -Name Background -Value '24 48 72'
                Add-Type -Namespace TW -Name W -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(int a, int b, string c, int d);'
                [TW.W]::SystemParametersInfo(0x14, 0, '', 3) | Out-Null
                Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
                'ok'
                """),
        };
        if (defender)
        {
            steps.Add(("Microsoft Defender: exclude the system drive from scanning (GUEST ONLY; lowers the VM's protection)",
                """
                # Tamper Protection blocks turning real-time protection off, but administrators may add exclusions.
                Add-MpPreference -ExclusionPath "$env:SystemDrive\"
                Add-MpPreference -ExclusionProcess 'powershell.exe','node.exe','claude.exe','git.exe'
                'excluded: ' + ((Get-MpPreference).ExclusionPath -join ', ')
                """));
        }
        return steps;
    }

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        bool defender = a.Flag("defender"), yes = a.Flag("yes");
        a.RejectUnknown();
        var steps = Steps(defender);
        if (!yes)
        {
            Console.WriteLine($"tune will change these settings INSIDE {m.Name} only (nothing on this PC):");
            foreach (var (title, _) in steps) Console.WriteLine("  - " + title);
            if (!defender) Console.WriteLine("  (add --defender to also exclude the system drive from Defender scanning; lowers the VM's protection)");
            Console.WriteLine($"Tip: take a snapshot first (terminal-windows snapshot {m.Name} --name before-tune), then run with --yes.");
            return 2;
        }

        await using var c = await GuestClient.ConnectAsync(m);
        int failed = 0;
        foreach (var (title, script) in steps)
        {
            Console.Error.WriteLine($"==> {title}");
            JsonObject r;
            try
            {
                r = await c.CallAsync("exec", new JsonObject { ["script"] = script, ["timeout_s"] = 120 }, TimeSpan.FromSeconds(150));
            }
            catch (RpcException ex) when (ex.Message.Contains("Unknown op"))
            {
                throw new CliException($"The guest helper is too old for this. Run 'terminal-windows update-helper {m.Name}' first.");
            }
            var outText = (r["stdout"]?.GetValue<string>() ?? "").Trim();
            if (outText.Length > 0) Console.Error.WriteLine("    " + outText.Replace("\n", "\n    "));
            if (r["exitCode"]?.GetValue<int>() != 0)
            {
                failed++;
                Console.Error.WriteLine("    FAILED: " + (r["stderr"]?.GetValue<string>() ?? "").Trim());
            }
        }
        Console.WriteLine(failed == 0
            ? $"Done. Restart the VM for everything to take effect: terminal-windows stop {m.Name} --shutdown, then start {m.Name}."
            : $"{failed} step(s) failed; see the messages above.");
        return failed == 0 ? 0 : 1;
    }
}
