using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using TerminalWindows.Cli.Backend;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Setup;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// <c>setup</c> (also what a double-click on terminal-windows.exe runs): takes a new user from "I have the
/// exe" to "an AI is running in its own Windows VM" by asking a few yes/no questions. Every step checks
/// whether it is already done, so the wizard can be re-run or resumed after the restart it may ask for.
/// Anything that installs software or changes host settings is asked first and done by Windows' own
/// tools (winget, an elevated host-mode); the wizard never handles passwords or sign-ins.
/// </summary>
public static class Wizard
{
    private const string Name = MachineStore.DefaultName;
    private const string RunOnceValue = "terminal-windows-setup";
    private static readonly bool Ja = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

    private static string T(string ja, string en) => Ja ? ja : en;

    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] list, uint count);

    /// <summary>True when the exe was started from Explorer (it owns a console window nobody else uses).</summary>
    public static bool StartedByDoubleClick()
    {
        try { return GetConsoleProcessList(new uint[4], 4) == 1; } catch { return false; }
    }

    /// <summary>Same place as install.ps1. Not AppData: packaged apps (the Claude desktop app) see a virtualized AppData.</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".terminal-windows", "bin");

    private static string StatePath => Path.Combine(MachineStore.Home, "setup-state.json");

    /// <summary>--dry-run: run every check and say what would happen, but install, change and create nothing.</summary>
    private static bool DryRun;

    public static async Task<int> RunAsync(ParsedArgs a, bool pauseAtEnd)
    {
        DryRun = a.Flag("dry-run");
        a.RejectUnknown();
        try
        {
            return await RunStepsAsync();
        }
        catch (Exception ex) when (ex is CliException or UsageException or IOException or InvalidDataException)
        {
            Console.WriteLine();
            Console.WriteLine(T("エラー: ", "Error: ") + ex.Message);
            Console.WriteLine(T("直したあと、もう一度 terminal-windows.exe を開くと続きから再開します。",
                                "Fix it and open terminal-windows.exe again; setup resumes where it stopped."));
            return 1;
        }
        finally
        {
            if (pauseAtEnd)
            {
                Console.WriteLine();
                Console.Write(T("Enter キーで閉じます...", "Press Enter to close..."));
                Console.ReadLine();
            }
        }
    }

    private static async Task<int> RunStepsAsync()
    {
        Title(T("terminal-windows セットアップ", "terminal-windows setup"));
        Console.WriteLine(T("AI 専用の Windows 11 VM を、この PC の中に作ります。あなたのマウスやキーボードには触れません。",
                            "This creates a Windows 11 VM for AI agents inside this PC. Your own mouse and keyboard are never touched."));
        var state = LoadState();

        // Already set up: just open it.
        if (!DryRun && MachineStore.Exists(Name) && MachineStore.Load(Name).SetupComplete && (state["done"]?.GetValue<bool>() == true || !File.Exists(StatePath)))
            return await OpenAsync(state);

        if (!Doctor.VirtualizationEnabledInFirmware())
            throw new CliException(T("CPU の仮想化機能（Intel VT-x / AMD-V）が BIOS/UEFI でオフになっています。PC の BIOS 設定でオンにしてから、もう一度開いてください（メーカー名と「仮想化 有効」で検索すると手順が見つかります）。",
                                     "CPU virtualization (Intel VT-x / AMD-V) is disabled in the BIOS/UEFI. Enable it in the firmware settings and open this again."));
        InstallSelf();
        if (DryRun) return await DryRunReportAsync();
        if (!await EnsureVirtualBoxAsync()) return 1;
        if (!await EnsureFastHostAsync()) return 0; // restarting, or the user stopped here
        CheckDiskSpace();

        if (!MachineStore.Exists(Name) || !MachineStore.Load(Name).SetupComplete)
        {
            if (MachineStore.Exists(Name))
            {
                Step(T("前回の途中の VM を作り直します", "Re-creating the VM from the interrupted run"));
                await new MachineManager().DeleteAsync(MachineStore.Load(Name));
            }
            var iso = await FindIsoAsync();
            var agent = AskAgent();
            state["agent"] = agent;
            SaveState(state);
            Step(T("Windows 11 を VM に入れます（30〜60 分。PC はつけたままにしてください）",
                   "Installing Windows 11 in the VM (30-60 minutes; keep the PC on)"));
            await Lifecycle.CreateAsync(Args(Name, "--iso", iso));
        }

        var agentId = state["agent"]?.GetValue<string>() ?? "claude";
        if (agentId != "none" && state["agentInstalled"]?.GetValue<bool>() != true)
        {
            Step(T($"VM の中に {agentId} を入れます", $"Installing {agentId} inside the VM"));
            if (await Agent.InstallAsync(Args(Name, "--agent", agentId, "--yes")) != 0) throw new CliException("agent-install failed");
            state["agentInstalled"] = true; SaveState(state);
        }
        if (state["tuned"]?.GetValue<bool>() != true)
        {
            Step(T("VM の中を軽くします", "Making the VM lighter"));
            await Tune.RunAsync(Args(Name, "--yes"));
            state["tuned"] = true; SaveState(state);
        }
        if (state["snapshot"]?.GetValue<bool>() != true)
        {
            Step(T("今の状態を保存します（スナップショット agent-ready）", "Saving this state (snapshot agent-ready)"));
            await SnapshotCommands.SnapshotAsync(Args(Name, "--name", "agent-ready"));
            state["snapshot"] = true; SaveState(state);
        }
        state["done"] = true; SaveState(state);

        Title(T("準備ができました", "Ready"));
        Console.WriteLine(T("次からは terminal-windows.exe を開くだけで、VM の中の AI につながります。",
                            "From now on, opening terminal-windows.exe connects you to the AI inside the VM."));
        return await OpenAsync(state);
    }

    private static async Task<int> DryRunReportAsync()
    {
        var info = await new VirtualBoxBackend().ProbeAsync();
        Step("VirtualBox: " + (info.Installed && info.Problem is null ? $"{info.Version} OK" : "would be installed with winget (" + (info.Problem ?? "not installed") + ")"));
        Step("Windows hypervisor: " + (Doctor.HypervisorPresent() ? "running -> would offer 'host-mode fast' (admin) and a restart, then resume via RunOnce" : "off (fast) OK"));
        var root = Path.GetPathRoot(Path.GetFullPath(MachineStore.Home))!;
        double freeGb = new DriveInfo(root).AvailableFreeSpace / 1024.0 / 1024 / 1024;
        Step($"Disk: {freeGb:0} GB free on {root}" + (freeGb >= 40 ? " OK" : freeGb >= 25 ? " (low; would warn)" : " (too low; would ask before continuing)"));
        var iso = CandidateIsos().FirstOrDefault(IsWindowsIso);
        Step("Windows 11 ISO: " + (iso ?? "none found -> would open Microsoft's download page and wait for the download"));
        Step("VM '" + Name + "': " + (MachineStore.Exists(Name) ? (MachineStore.Load(Name).SetupComplete ? "exists, set up" : "exists, unfinished -> would be re-created") : "would be created, then the chosen AI installed, tuned and snapshotted"));
        Console.WriteLine();
        Console.WriteLine("Dry run: nothing was installed, changed or created.");
        return 0;
    }

    // ---- steps ----

    private static async Task<int> OpenAsync(JsonObject state)
    {
        var agent = state["agent"]?.GetValue<string>() ?? "claude";
        if (agent == "none")
            return await Lifecycle.ViewAsync(Args(Name));
        Console.WriteLine(T($"{agent} を VM の中で開きます。初回はサインインを求められます（表示された URL をブラウザーで開いてください）。Ctrl+] で抜けても VM の中で動き続けます。",
                            $"Opening {agent} inside the VM. The first time it asks you to sign in (open the URL it shows). Ctrl+] detaches; it keeps running in the VM."));
        await Lifecycle.StartAsync(Args(Name));
        return agent switch
        {
            "codex" => await Agent.CodexAsync(Args(Name)),
            "gemini" => await Agent.GeminiAsync(Args(Name)),
            _ => await Agent.ClaudeAsync(Args(Name)),
        };
    }

    /// <summary>Copies the exe to %LOCALAPPDATA%\Programs\terminal-windows and puts it on the user PATH.</summary>
    private static void InstallSelf()
    {
        var self = Environment.ProcessPath;
        if (DryRun)
        {
            Step($"Would copy terminal-windows.exe to {InstallDir} and add it to the user PATH (unless it runs from there).");
            return;
        }
        if (self is null || !self.EndsWith("terminal-windows.exe", StringComparison.OrdinalIgnoreCase)) return;
        var target = Path.Combine(InstallDir, "terminal-windows.exe");
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(self))
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(self, target, overwrite: true);
                // A guest\ folder from an older two-file install would shadow the helper embedded in the exe.
                var oldGuest = Path.Combine(InstallDir, "guest");
                if (Directory.Exists(oldGuest)) Directory.Delete(oldGuest, recursive: true);
            }
            catch (IOException) { return; } // the installed copy is running; keep using this one
        }
        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        if (!path.Split(';').Any(p => string.Equals(p.TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.SetEnvironmentVariable("PATH", path.TrimEnd(';') + ";" + InstallDir, EnvironmentVariableTarget.User);
            Console.WriteLine(T($"  terminal-windows を {InstallDir} に入れました（新しいターミナルで `terminal-windows` と打てば使えます）。",
                                $"  Installed terminal-windows to {InstallDir} (new terminals can run `terminal-windows`)."));
        }
    }

    private static async Task<bool> EnsureVirtualBoxAsync()
    {
        var info = await new VirtualBoxBackend().ProbeAsync();
        if (info.Installed && info.Problem is null) return true;

        Step(T("VirtualBox（Oracle の無料の仮想化ソフト）が必要です", "VirtualBox (free virtualization software from Oracle) is required"));
        if (info.Problem is not null) Console.WriteLine("  " + info.Problem);
        if (!Ask(T("今すぐ winget で入れますか？（管理者の確認画面が出ます）", "Install it now with winget? (Windows asks for admin approval)"), true))
        {
            Console.WriteLine(T("  https://www.virtualbox.org/ から入れてから、もう一度開いてください。", "  Install it from https://www.virtualbox.org/ and open this again."));
            return false;
        }
        foreach (var id in new[] { "Oracle.VirtualBox" })
        {
            var ok = await RunVisibleAsync("winget", $"install --id {id} -e --source winget --accept-source-agreements --accept-package-agreements");
            if (!ok && id == "Oracle.VirtualBox")
            {
                // The VirtualBox installer refuses to run without the Visual C++ runtime on a fresh PC.
                await RunVisibleAsync("winget", "install --id Microsoft.VCRedist.2015+.x64 -e --source winget --accept-source-agreements --accept-package-agreements");
                await RunVisibleAsync("winget", $"install --id {id} -e --source winget --accept-source-agreements --accept-package-agreements");
            }
        }
        info = await new VirtualBoxBackend().ProbeAsync();
        if (info.Installed && info.Problem is null) { Console.WriteLine($"  VirtualBox {info.Version} OK"); return true; }
        Console.WriteLine(T("  VirtualBox を入れられませんでした。https://www.virtualbox.org/ から入れてから、もう一度開いてください。",
                            "  VirtualBox could not be installed. Install it from https://www.virtualbox.org/ and open this again."));
        return false;
    }

    /// <summary>
    /// With Windows' own hypervisor running, VirtualBox goes through it and Windows setup hung on a real PC.
    /// Offers host-mode fast (elevated) and a restart; the wizard resumes by itself after sign-in.
    /// Returns false when the user restarts now or chooses to stop.
    /// </summary>
    private static async Task<bool> EnsureFastHostAsync()
    {
        if (!Doctor.HypervisorPresent()) return true;
        Step(T("この PC では Windows のハイパーバイザーが動いています", "Windows' own hypervisor is running on this PC"));
        Console.WriteLine(T(
            "  メモリ整合性・VBS・WSL2 などのためです。このままだと VM がとても遅く、セットアップの途中で固まることがあります。\n" +
            "  オフにすると速くなりますが、オフの間は WSL2 / Docker / Windows サンドボックスが使えず、メモリ整合性などの保護も外れます。\n" +
            "  あとで `terminal-windows host-mode secure --yes`（管理者）でいつでも元に戻せます。",
            "  It is on for memory integrity / VBS / WSL2. VirtualBox then runs very slowly and Windows setup can hang.\n" +
            "  Turning it off makes VMs fast, but WSL2 / Docker / Windows Sandbox stop working and memory integrity protection is off.\n" +
            "  Undo any time with `terminal-windows host-mode secure --yes` (admin)."));
        foreach (var w in HostMode.ConflictsForWizard()) Console.WriteLine("  ! " + w);
        if (!Ask(T("オフにしますか？（管理者の確認画面のあと、再起動が必要です）", "Turn it off? (admin approval, then a restart)"), true))
        {
            return Ask(T("このまま続けますか？（遅く、固まることがあります）", "Continue anyway? (slow; setup may hang)"), false);
        }

        var exe = Environment.ProcessPath!;
        var installed = Path.Combine(InstallDir, "terminal-windows.exe");
        if (File.Exists(installed)) exe = installed;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "host-mode fast --yes") { UseShellExecute = true, Verb = "runas" })!;
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new CliException($"host-mode fast exited with {p.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine(T("  管理者の確認がキャンセルされました。", "  Admin approval was cancelled."));
            return false;
        }

        // Resume after the restart: RunOnce opens the wizard once at the next sign-in.
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"))
            k.SetValue(RunOnceValue, $"\"{exe}\" setup");
        Console.WriteLine(T("  設定しました。再起動してサインインすると、セットアップが自動で続きから始まります。",
                            "  Done. After a restart and sign-in, setup continues by itself."));
        if (Ask(T("今すぐ再起動しますか？（開いている作業は保存してください）", "Restart now? (save your work first)"), true))
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 10") { UseShellExecute = false, CreateNoWindow = true });
        else
            Console.WriteLine(T("  あとで再起動してください（シャットダウンではなく「再起動」）。", "  Restart later (use Restart, not Shut down)."));
        return false;
    }

    private static void CheckDiskSpace()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(MachineStore.Home))!;
        double freeGb = new DriveInfo(root).AvailableFreeSpace / 1024.0 / 1024 / 1024;
        if (freeGb >= 40) return;
        Step(T($"ディスクの空きが少なめです（{root} に {freeGb:0} GB）", $"Low disk space ({freeGb:0} GB free on {root})"));
        Console.WriteLine(T("  VM には 25 GB 以上、できれば 40 GB 以上の空きが必要です。", "  The VM needs 25 GB, preferably 40 GB, free."));
        if (freeGb < 25 && !Ask(T("このまま続けますか？", "Continue anyway?"), false))
            throw new CliException(T("ディスクの空きを増やしてから、もう一度開いてください。", "Free up some disk space and open this again."));
    }

    /// <summary>Uses a Windows 11 ISO from Downloads/Desktop, or opens Microsoft's download page and waits for one.</summary>
    private static async Task<string> FindIsoAsync()
    {
        Step(T("Windows 11 の ISO ファイルを探します", "Looking for a Windows 11 ISO"));
        var found = CandidateIsos().FirstOrDefault(IsWindowsIso);
        if (found is not null && Ask(T($"これを使いますか？ {found}", $"Use this one? {found}"), true)) return found;

        var url = Ja ? "https://www.microsoft.com/ja-jp/software-download/windows11" : "https://www.microsoft.com/software-download/windows11";
        Console.WriteLine(T(
            "  Microsoft の公式ページを開きます。「Windows 11 ディスク イメージ (ISO) をダウンロードする」で\n" +
            "  Windows 11 を選び、製品の言語（日本語）を選んで 64-bit をダウンロードしてください（無料、約 7 GB）。\n" +
            "  ダウンロードが終わると自動で次に進みます。別の場所にある ISO を使うときは、そのパスを貼り付けて Enter。",
            "  Opening Microsoft's official page. Under 'Download Windows 11 Disk Image (ISO)', pick Windows 11,\n" +
            "  your language and the 64-bit download (free, about 7 GB). Setup continues by itself when it finishes.\n" +
            "  To use an ISO stored elsewhere, paste its path and press Enter."));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var known = CandidateIsos().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var typed = Task.Run(() => Console.ReadLine());
        while (true)
        {
            if (typed.IsCompleted)
            {
                var p = (typed.Result ?? "").Trim().Trim('"');
                if (File.Exists(p) && IsWindowsIso(p)) return p;
                if (p.Length > 0) Console.WriteLine(T("  Windows のインストール ISO ではないようです: ", "  Not a Windows installation ISO: ") + p);
                typed = Task.Run(() => Console.ReadLine());
            }
            foreach (var iso in CandidateIsos().Where(i => !known.Contains(i)))
            {
                if (!await IsCompleteAsync(iso)) continue;
                known.Add(iso);
                if (IsWindowsIso(iso)) { Console.WriteLine("  -> " + iso); return iso; }
            }
            await Task.Delay(3000);
        }
    }

    private static IEnumerable<string> CandidateIsos()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[] { Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        return dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.iso"))
            .OrderByDescending(File.GetLastWriteTimeUtc);
    }

    private static async Task<bool> IsCompleteAsync(string path)
    {
        long a = new FileInfo(path).Length;
        await Task.Delay(2000);
        return a > 1_000_000_000 && a == new FileInfo(path).Length;
    }

    private static bool IsWindowsIso(string path)
    {
        try { return IsoInspector.ReadImages(path).Any(i => (i.Name + i.DisplayName).Contains("Windows 11")); }
        catch { return false; }
    }

    private static string AskAgent()
    {
        Step(T("VM の中で使う AI を選んでください", "Choose the AI to run inside the VM"));
        Console.WriteLine("  1) Claude Code   2) Codex   3) Gemini CLI   4) " + T("入れない", "none"));
        Console.Write(T("  番号 [1]: ", "  Number [1]: "));
        return (Console.ReadLine() ?? "").Trim() switch { "2" => "codex", "3" => "gemini", "4" => "none", _ => "claude" };
    }

    // ---- small helpers ----

    private static ParsedArgs Args(params string[] args) => ParsedArgs.Parse(args, Program.Flags);

    private static void Title(string text)
    {
        Console.WriteLine();
        Console.WriteLine("== " + text + " ==");
    }

    private static void Step(string text)
    {
        Console.WriteLine();
        Console.WriteLine("> " + text);
    }

    private static bool Ask(string question, bool defaultYes)
    {
        Console.Write($"  {question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        var s = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        return s.Length == 0 ? defaultYes : s is "y" or "yes" or "はい";
    }

    private static async Task<bool> RunVisibleAsync(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false })!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // winget missing
        }
    }

    private static JsonObject LoadState()
    {
        try { return JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject ?? new(); }
        catch { return new JsonObject(); }
    }

    private static void SaveState(JsonObject s)
    {
        Directory.CreateDirectory(MachineStore.Home);
        File.WriteAllText(StatePath, s.ToJsonString());
    }
}
