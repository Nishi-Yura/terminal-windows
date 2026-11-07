using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using TerminalWindows.Cli.Backend;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Setup;
using TerminalWindows.Cli.Viewer;
using TerminalWindows.Protocol;

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

    /// <summary>Same place as install.ps1. Not AppData: packaged apps (Store-style desktop apps) see a virtualized AppData.</summary>
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
        var state = LoadState();

        // Already set up: the everyday menu.
        if (!DryRun && MachineStore.ImplicitMachine() is { } existing && MachineStore.Load(existing).SetupComplete
            && (state["done"]?.GetValue<bool>() == true || !File.Exists(StatePath)))
            return await OpenAsync(state);

        Title(T("terminal-windows セットアップ", "terminal-windows setup"));
        Console.WriteLine(T("AI 専用の Windows 11 VM を、この PC の中に作ります。あなたのマウスやキーボードには触れません。",
                            "This creates a Windows 11 VM for AI agents inside this PC. Your own mouse and keyboard are never touched."));

        if (!Doctor.VirtualizationEnabledInFirmware())
            throw new CliException(T("CPU の仮想化機能（Intel VT-x / AMD-V）が BIOS/UEFI でオフになっています。PC の BIOS 設定でオンにしてから、もう一度開いてください（メーカー名と「仮想化 有効」で検索すると手順が見つかります）。",
                                     "CPU virtualization (Intel VT-x / AMD-V) is disabled in the BIOS/UEFI. Enable it in the firmware settings and open this again."));
        InstallSelf();
        if (DryRun) return await DryRunReportAsync();
        var backend = await ChooseBackendAsync();
        if (backend is null) return 0; // restarting, installing VMware, or the user stopped here
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
            await Lifecycle.CreateAsync(Args(Name, "--iso", iso, "--backend", backend));
        }

        var agentId = state["agent"]?.GetValue<string>() ?? AskAgent();
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
        if (state["display"]?.GetValue<bool>() != true)
        {
            Step(T("画面を HiDPI（2 倍の細かさ）にします", "Switching the screen to HiDPI (twice the pixels)"));
            try
            {
                var dm = MachineStore.Load(Name);
                await using var dc = await GuestClient.ConnectAsync(dm);
                Console.WriteLine("  " + await Display.ApplyAsync(dc, dm, hidpi: true));
            }
            catch (Exception ex) when (ex is CliException or RpcException or IOException or TimeoutException)
            {
                Console.WriteLine(T("  できませんでした（標準のまま使えます）: ", "  Skipped (standard works too): ") + ex.Message);
            }
            state["display"] = true; SaveState(state);
        }
        if (state["snapshot"]?.GetValue<bool>() != true)
        {
            // Powered off: no RAM copy in the snapshot (several GB less), and the restart applies tune's settings.
            Step(T("VM を再起動して、今の状態を保存します（スナップショット agent-ready）", "Restarting the VM and saving this state (snapshot agent-ready)"));
            await new MachineManager(log: s => Console.WriteLine("  " + s)).SnapshotPoweredOffAsync(MachineStore.Load(Name), "agent-ready", "terminal-windows: AI installed and tuned");
            state["snapshot"] = true; SaveState(state);
        }
        if (state["connected"] is null)
        {
            var tools = ConnectCommand.Targets().Where(t => t.Installed()).Select(t => t.Name).ToList();
            if (tools.Count > 0)
            {
                Step(T($"この PC の AI ツール（{string.Join("、", tools)}）からも VM を使えるようにできます",
                       $"The AI tools on this PC ({string.Join(", ", tools)}) can use the VM too"));
                if (Ask(T("登録しますか？（設定は後から terminal-windows connect --remove で外せます）", "Register them? (undo later with terminal-windows connect --remove)"), defaultYes: true))
                    await ConnectCommand.RunAsync(Args(Name));
            }
            state["connected"] = true; SaveState(state);
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

    /// <summary>
    /// The everyday entry point once a VM exists (a double-click or a bare 'terminal-windows'). It goes
    /// straight to the configured start screen: the AI's window on the VM desktop, shown in this terminal
    /// (or just the screen, or the menu). In the screen Ctrl+] closes it (back to this menu) and F12 opens the command bar; "close"
    /// lands in the numbered menu below, which loops until the user quits.
    /// </summary>
    private static async Task<int> OpenAsync(JsonObject state)
    {
        var name = MachineStore.ImplicitMachine() ?? Name;
        var first = MachineStore.Load(name);
        var agent = first.Agent ?? state["agent"]?.GetValue<string>() ?? InstalledAgent();
        var startup = first.Startup ?? "menu";

        if (startup != "menu")
        {
            Console.WriteLine(Ui.T($"{name} を開いています…（Ctrl+] で閉じるとメニュー。画面の中のメニューは F12）", $"Opening {name}… (Ctrl+] closes it and shows the menu; F12: menu inside the screen)"));
            int rc = startup == "agent" && agent is not null and not "none"
                ? await RunAgentWindowAsync(name, agent)
                : await Lifecycle.ViewAsync(Args(name));
            if (Lifecycle.LastViewerExit != ViewerExit.Menu) return rc;
        }

        while (true)
        {
            var m = MachineStore.Load(name);
            agent = m.Agent ?? agent;
            var vmState = await new RoutedBackend().GetStateAsync(m.VmName);
            Title($"terminal-windows: {name} ({m.Backend}, {(vmState == VmState.Running ? T("起動中", "running") : T("停止中", "stopped"))})");
            var items = new List<(string Label, Func<Task<int>> Run)>();
            if (agent is not null and not "none")
                items.Add((T($"{agent} を開く（VM のデスクトップの窓で。画面ごと見えます）", $"Open {agent} in a window on the VM's desktop, shown here"),
                    () => RunAgentWindowAsync(name, agent)));
            items.Add((T("画面を見る・操作する", "Watch and use the screen"), () => Lifecycle.ViewAsync(Args(name))));
            items.Add((T("VM の中のターミナル（PowerShell、文字だけ）", "A terminal inside the VM (PowerShell, text only)"),
                async () => { await Lifecycle.StartAsync(Args(name)); return await Agent.TermAsync(Args(name)); }));
            items.Add((T("AI の成果物（Outbox）を この PC に取り込む", "Fetch the AI's results (Outbox) to this PC"), () => FetchOutboxAsync(m)));
            items.Add((T("この PC の AI ツール（MCP に対応したもの）から VM を使えるようにする", "Let this PC's AI tools (any MCP client) use the VM"),
                () => ConnectCommand.RunAsync(Args(name))));
            items.Add((T("スナップショット（撮る・戻す・消す）", "Snapshots (take / restore / delete)"), () => SnapshotMenuAsync(m)));
            items.Add((vmState == VmState.Running ? T("VM を止める（状態を保存。次回そのまま再開）", "Stop the VM (state is saved; resumes as it was)") : T("VM を起動する", "Start the VM"),
                () => vmState == VmState.Running ? Lifecycle.StopAsync(Args(name)) : Lifecycle.StartAsync(Args(name))));
            var current = m.Startup ?? "menu";
            items.Add((T($"開いたときに最初に出すもの: {StartupLabel(current, agent)}（変更）", $"Shown first when opened: {StartupLabel(current, agent)} (change)"),
                () => { ChangeStartup(m, agent); return Task.FromResult(0); }));
            var freeGb = FreeGb();
            if (freeGb < 10)
                Console.WriteLine(T($"  ! ディスクの空きが少なくなっています（{freeGb:0} GB）。古いスナップショットを消すと空きます。",
                                    $"  ! Low disk space ({freeGb:0} GB free). Deleting old snapshots frees space."));
            Console.WriteLine(T("  （画面を見ている間: Ctrl+] で閉じてこのメニューへ、F12 でスナップショット・停止など、Ctrl+G で Windows キー）",
                                "  (while watching the screen: Ctrl+] closes it and comes back here, F12 for snapshot / stop, Ctrl+G for Windows keys)"));
            for (int i = 0; i < items.Count; i++) Console.WriteLine($"  {i + 1}) {items[i].Label}");
            Console.WriteLine("  q) " + T("終わる（VM はそのまま）", "Quit (the VM stays as it is)"));
            Console.Write(T("  番号 [1]: ", "  Number [1]: "));
            var input = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
            if (input is "q" or "quit" or "exit") return 0;
            int pick = input.Length == 0 ? 1 : int.TryParse(input, out var n) ? n : -1;
            if (pick < 1 || pick > items.Count) continue;
            try { await items[pick - 1].Run(); }
            catch (Exception ex) when (ex is CliException or UsageException or IOException)
            {
                Console.WriteLine(T("エラー: ", "Error: ") + ex.Message);
            }
        }
    }

    private static double FreeGb()
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(MachineStore.Home))!).AvailableFreeSpace / 1024.0 / 1024 / 1024; }
        catch (IOException) { return double.MaxValue; }
    }

    /// <summary>Copies the VM's Outbox to Downloads\terminal-windows\(time) and opens that folder.</summary>
    private static async Task<int> FetchOutboxAsync(MachineConfig m)
    {
        await new MachineManager().EnsureRunningAsync(m);
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "terminal-windows", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        await using var c = await GuestClient.ConnectAsync(m);
        var files = await c.PullAsync(null, dest, TransferLimits.Default, s => Console.WriteLine("  " + s));
        if (files.Count == 0)
        {
            Console.WriteLine(T("  Outbox は空でした（AI には成果物を Desktop\\Outbox に置くよう伝えてあります）。", "  The Outbox is empty (the AI is told to put results in Desktop\\Outbox)."));
            return 0;
        }
        Console.WriteLine(T($"  {files.Count} 個のファイルを {dest} に取り込みました。", $"  Copied {files.Count} file(s) to {dest}."));
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dest}\"") { UseShellExecute = true });
        return 0;
    }

    private static async Task<int> SnapshotMenuAsync(MachineConfig m)
    {
        var mgr = new MachineManager();
        var snaps = await mgr.Backend.ListSnapshotsAsync(m.VmName);
        Console.WriteLine(T("    今あるスナップショット:", "    Snapshots:"));
        if (snaps.Count == 0) Console.WriteLine(T("      （なし）", "      (none)"));
        for (int i = 0; i < snaps.Count; i++) Console.WriteLine($"      {i + 1}) {snaps[i].Name}");
        Console.WriteLine(T("    t) 今の状態を撮る   r) 戻す   d) 消す   Enter) 戻る", "    t) take now   r) restore   d) delete   Enter) back"));
        Console.Write("    > ");
        var cmd = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (cmd == "t")
        {
            Console.Write(T("    名前（空なら日時）: ", "    Name (empty: date and time): "));
            var label = (Console.ReadLine() ?? "").Trim();
            Console.WriteLine(T("    撮っています（1〜3 分）…", "    Taking it (1-3 min)…"));
            label = await mgr.SnapshotAsync(m, label.Length == 0 ? null : label);
            Console.WriteLine(T($"    「{label}」を保存しました。", $"    Saved '{label}'."));
            return 0;
        }
        if (cmd is not ("r" or "d") || snaps.Count == 0) return 0;
        Console.Write(T("    どれ？ 番号: ", "    Which one? Number: "));
        if (!int.TryParse((Console.ReadLine() ?? "").Trim(), out var k) || k < 1 || k > snaps.Count) return 0;
        var target = snaps[k - 1].Name;
        if (cmd == "r")
        {
            Console.WriteLine(T($"    「{target}」に戻します。今の状態は自動で別のスナップショットに保存します…", $"    Restoring '{target}'. The current state is saved as another snapshot first…"));
            var safety = await mgr.RestoreAsync(m, target);
            Console.WriteLine(T($"    戻しました（直前の状態は「{safety}」）。", $"    Restored (the previous state is '{safety}')."));
        }
        else
        {
            Console.Write(T($"    「{target}」を消します。元に戻せません。よろしいですか？ [y/N]: ", $"    Delete '{target}'? This cannot be undone. [y/N]: "));
            if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() is not ("y" or "yes")) return 0;
            await mgr.Backend.DeleteSnapshotAsync(m.VmName, target);
            Console.WriteLine(T($"    「{target}」を消しました。", $"    Deleted '{target}'."));
        }
        return 0;
    }

    private static Task<int> RunAgentWindowAsync(string name, string agent)
    {
        var args = Args(name, "--window");
        return agent switch
        {
            "codex" => Agent.CodexAsync(args),
            "gemini" => Agent.GeminiAsync(args),
            _ => Agent.ClaudeAsync(args),
        };
    }

    private static string StartupLabel(string startup, string? agent) => startup switch
    {
        "agent" => T($"{agent ?? "AI"} の窓", $"the {agent ?? "AI"} window"),
        "view" => T("VM の画面", "the VM screen"),
        _ => T("このメニュー", "this menu"),
    };

    private static void ChangeStartup(MachineConfig m, string? agent)
    {
        var options = new List<string>();
        if (agent is not null and not "none") options.Add("agent");
        options.Add("view");
        options.Add("menu");
        for (int i = 0; i < options.Count; i++) Console.WriteLine($"    {i + 1}) {StartupLabel(options[i], agent)}");
        Console.Write(T("    番号: ", "    Number: "));
        if (int.TryParse((Console.ReadLine() ?? "").Trim(), out var k) && k >= 1 && k <= options.Count)
        {
            m.Startup = options[k - 1];
            MachineStore.Save(m);
            Console.WriteLine(T($"    次から最初に「{StartupLabel(m.Startup, agent)}」を出します。", $"    From now on it opens with {StartupLabel(m.Startup, agent)}."));
        }
    }

    /// <summary>The AI CLI the wizard installed earlier, if the state file says so.</summary>
    private static string? InstalledAgent() => LoadState()["agent"]?.GetValue<string>();

    /// <summary>A bare 'terminal-windows' in a terminal: the menu when a VM exists, otherwise the setup wizard.</summary>
    public static Task<int> MenuOrSetupAsync(bool pauseAtEnd) =>
        RunAsync(ParsedArgs.Parse(Array.Empty<string>(), Program.Flags), pauseAtEnd);

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
                foreach (var old in Directory.GetFiles(InstallDir, "terminal-windows.old-*.exe"))
                    try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                try { File.Copy(self, target, overwrite: true); }
                catch (IOException) when (File.Exists(target))
                {
                    // The installed copy is running (an MCP server of an open AI app, say): Windows lets us rename it.
                    File.Move(target, Path.Combine(InstallDir, $"terminal-windows.old-{DateTime.Now:yyyyMMddHHmmss}.exe"));
                    File.Copy(self, target);
                    Console.WriteLine(T("  古い terminal-windows が動いていたので入れ替えました。使っている AI アプリを開き直すと新しい版になります。",
                                        "  The old terminal-windows was running and has been replaced; restart the AI apps that use it."));
                }
                // A guest\ folder from an older two-file install would shadow the helper embedded in the exe.
                var oldGuest = Path.Combine(InstallDir, "guest");
                if (Directory.Exists(oldGuest)) Directory.Delete(oldGuest, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // keep using this copy
        }
        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        if (!path.Split(';').Any(p => string.Equals(p.TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.SetEnvironmentVariable("PATH", path.TrimEnd(';') + ";" + InstallDir, EnvironmentVariableTarget.User);
            Console.WriteLine(T($"  terminal-windows を {InstallDir} に入れました（新しいターミナルで `terminal-windows` と打てば使えます）。",
                                $"  Installed terminal-windows to {InstallDir} (new terminals can run `terminal-windows`)."));
        }
    }

    /// <summary>
    /// VMware Workstation if it is installed and needed (Windows' hypervisor on) or the only one present;
    /// otherwise VirtualBox, installing it and turning the hypervisor off when the user agrees. With the
    /// hypervisor on and no VMware, the user picks: turn it off (VirtualBox) or install VMware (keeps WSL2/Docker).
    /// </summary>
    private static async Task<string?> ChooseBackendAsync()
    {
        bool hv = Doctor.HypervisorPresent();
        if (VmwareBackend.IsInstalled() && (hv || VirtualBoxBackend.InstallDir() is null))
        {
            Step(T("VMware Workstation を使います", "Using VMware Workstation"));
            return Backends.Vmware;
        }
        if (hv)
        {
            Step(T("この PC では Windows のハイパーバイザーが動いています", "Windows' own hypervisor is running on this PC"));
            Console.WriteLine(T(
                "  （メモリ整合性・VBS・WSL2 などのため）。このままでは VirtualBox がとても遅く、固まることがあります。次のどちらかを選んでください。\n" +
                "  1) ハイパーバイザーをオフにして VirtualBox を使う（速い。WSL2 / Docker は使えなくなり、メモリ整合性などの保護も外れます。管理者の確認と再起動が必要）\n" +
                "  2) VMware Workstation Pro を入れて使う（オンのままで動く。個人利用は無料ですが、Broadcom のアカウント登録が必要）",
                "  (for memory integrity / VBS / WSL2). VirtualBox is very slow and can hang like this. Choose one:\n" +
                "  1) Turn the hypervisor off and use VirtualBox (fast; WSL2 / Docker stop working and memory integrity is off; admin approval and a restart)\n" +
                "  2) Install VMware Workstation Pro and use it (works with the hypervisor on; free for personal use, needs a Broadcom account)"));
            foreach (var w in HostMode.ConflictsForWizard()) Console.WriteLine("  ! " + w);
            Console.Write(T("  番号 [1]: ", "  Number [1]: "));
            if ((Console.ReadLine() ?? "").Trim() == "2")
                return await InstallVmwareAsync() ? Backends.Vmware : null;
            if (!await EnsureVirtualBoxAsync()) return null;
            if (!await EnsureFastHostAsync()) return null;
            return Backends.VirtualBox;
        }
        if (!await EnsureVirtualBoxAsync()) return null;
        return Backends.VirtualBox;
    }

    /// <summary>
    /// VMware Workstation Pro: Broadcom only offers it after sign-in, so the user downloads it; the wizard
    /// waits for the installer in Downloads, starts it (the user goes through its screens and licence),
    /// and continues as soon as VMware is installed, without reopening anything.
    /// </summary>
    private static async Task<bool> InstallVmwareAsync()
    {
        Console.WriteLine(T(
            "  Broadcom のサポートポータルを開きます。サインイン（またはアカウント登録）して「VMware Workstation Pro」の\n" +
            "  Windows 版をダウンロードしてください（個人利用は無料）。ダウンロードが終わると、インストーラーを自動で開きます。\n" +
            "  やめるときは Enter。",
            "  Opening Broadcom's support portal. Sign in (or register) and download VMware Workstation Pro for Windows\n" +
            "  (free for personal use). The installer opens by itself when the download finishes. Press Enter to stop."));
        Process.Start(new ProcessStartInfo("https://support.broadcom.com/") { UseShellExecute = true });
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        IEnumerable<string> Installers() => Directory.Exists(downloads)
            ? Directory.EnumerateFiles(downloads, "VMware-workstation*.exe").OrderByDescending(File.GetLastWriteTimeUtc)
            : Enumerable.Empty<string>();
        var typed = Task.Run(() => Console.ReadLine());
        string? installer = Installers().FirstOrDefault();
        while (installer is null || !await IsCompleteAsync(installer, 100_000_000))
        {
            if (VmwareBackend.IsInstalled()) return true; // installed some other way meanwhile
            if (typed.IsCompleted)
            {
                Console.WriteLine(T("  VMware を入れたら terminal-windows.exe をもう一度開くと続きから進みます。",
                                    "  After installing VMware, open terminal-windows.exe again to continue."));
                return false;
            }
            await Task.Delay(3000);
            installer = Installers().FirstOrDefault();
        }
        Console.WriteLine("  -> " + installer);
        Console.WriteLine(T("  インストーラーを開きます（管理者の確認画面が出ます）。画面に沿って進めてください。",
                            "  Starting the installer (Windows asks for admin approval). Follow its screens."));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true })!;
            await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine(T("  管理者の確認がキャンセルされました。", "  Admin approval was cancelled."));
            return false;
        }
        for (int i = 0; i < 20 && !VmwareBackend.IsInstalled(); i++) await Task.Delay(1000);
        if (VmwareBackend.IsInstalled()) { Console.WriteLine(T("  VMware Workstation OK", "  VMware Workstation OK")); return true; }
        Console.WriteLine(T("  VMware Workstation が見つかりません。インストールが終わったら、もう一度開いてください（再起動を求められたら再起動してから）。",
                            "  VMware Workstation not found. When it is installed (restart first if it asked), open this again."));
        return false;
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
        Console.WriteLine(T("  元に戻すときは、管理者のターミナルで `terminal-windows host-mode secure --yes` を実行して再起動します。",
                            "  To undo later: `terminal-windows host-mode secure --yes` in an admin terminal, then restart."));
        if (!Ask(T("オフにしますか？（管理者の確認画面のあと、再起動が必要です）", "Turn it off? (admin approval, then a restart)"), true))
        {
            Console.WriteLine(T("  オンのままにしました。オンのまま使うには、もう一度開いて 2（VMware Workstation Pro）を選んでください。",
                                "  Left it on. To use the VM with it on, open this again and choose 2 (VMware Workstation Pro)."));
            return false;
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

    /// <summary>The download is finished: big enough and no longer growing (browsers write to a temp name first).</summary>
    private static async Task<bool> IsCompleteAsync(string path, long minBytes = 1_000_000_000)
    {
        try
        {
            long a = new FileInfo(path).Length;
            await Task.Delay(2000);
            return a > minBytes && a == new FileInfo(path).Length;
        }
        catch (IOException) { return false; }
    }

    private static bool IsWindowsIso(string path)
    {
        try { return IsoInspector.ReadImages(path).Any(i => (i.Name + i.DisplayName).Contains("Windows 11")); }
        catch { return false; }
    }

    private static string AskAgent()
    {
        Step(T("VM の中で使う AI を選んでください", "Choose the AI to run inside the VM"));
        Console.WriteLine("  1) Claude Code (Anthropic)   2) Codex CLI (OpenAI)   3) Gemini CLI (Google)   4) " + T("入れない", "none"));
        // No default: the choice is the user's.
        while (true)
        {
            Console.Write(T("  番号: ", "  Number: "));
            var line = Console.ReadLine();
            if (line is null) return "none"; // no input at all (piped): install nothing
            switch (line.Trim())
            {
                case "1": return "claude";
                case "2": return "codex";
                case "3": return "gemini";
                case "4": return "none";
            }
            Console.WriteLine(T("  1〜4 の番号を入れてください。", "  Enter a number from 1 to 4."));
        }
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
