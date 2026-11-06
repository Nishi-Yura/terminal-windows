using System.Text;
using TerminalWindows.Cli.Commands;
using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli;

public static class Program
{
    public const string Usage = """
terminal-windows - an AI-dedicated Windows VM (VirtualBox or VMware Workstation) you can watch in the terminal and drive via MCP.
AI actions only affect the VM; the host's mouse, keyboard and clipboard are never touched.

Run 'terminal-windows' with no arguments for a menu (or first-time setup).
NAME may be left out when there is a machine called "default" or only one machine.

Usage:
  terminal-windows setup [--dry-run]        guided first-time setup (also what double-clicking the exe does)
  terminal-windows up [NAME] [--iso PATH] [--no-view] [--detach] [--plan] [create options]
  terminal-windows create NAME --iso PATH [--cpus 4] [--memory 8] [--disk 64] [--disk-bus sata|nvme] [--paravirt none|minimal|hyperv] [--backend virtualbox|vmware] [--width 1600] [--height 900]
                          [--offline] [--locale ja-JP] [--ui-language ja-JP] [--timezone TZ]
                          [--edition Pro] [--image-index N] [--product-key KEY] [--auto-snapshot] [--share PATH [--share-writable]]
  terminal-windows start|pause|resume NAME
  terminal-windows stop NAME [--shutdown | --force] [--auto-snapshot]   (default: save state)
  terminal-windows view NAME [--graphics sixel|kitty|blocks]   (always in this terminal; blocks = any truecolor terminal)
  terminal-windows view-demo [--graphics sixel|kitty]   (viewer check without a VM; input is only logged)
  terminal-windows show|hide NAME
  terminal-windows ls
  terminal-windows delete NAME --yes                   (deletes the VM, its disk, snapshots and folder)
  terminal-windows status NAME
  terminal-windows resolution NAME WIDTHxHEIGHT          (change the VM's screen size)
  terminal-windows screen NAME [--out FILE]            (screenshot taken by the hypervisor; works during setup)
  terminal-windows snapshot NAME [--name LABEL] | restore NAME LABEL | snapshots NAME | delete-snapshot NAME LABEL --yes
  terminal-windows pull NAME [GUEST_PATH] [--to HOST_DIR] [--sync-outbox]
  terminal-windows push NAME HOST_PATH [--to GUEST_DIR]
  terminal-windows notes NAME [--edit]
  terminal-windows clone NAME NEW [--from-snapshot LABEL]
  terminal-windows action NAME <screenshot|click|double_click|right_click|drag|scroll|type|key|ui_tree|status> ...
  terminal-windows mcp [NAME] [--config] [--sync-outbox]

AI inside the VM:
  terminal-windows agent-install NAME [--agent claude|codex|gemini] [--yes]   install an AI CLI + desktop MCP + instructions in the guest
  terminal-windows claude|codex|gemini NAME [--window] [-- ARGS]   (--window: in a window on the VM's desktop, shown via the viewer)
  terminal-windows claude|codex|gemini NAME [-- ARGS]  run that agent inside the VM, shown in this terminal (Ctrl+] detaches)
  terminal-windows term NAME [--cwd DIR] [-- COMMAND]  a terminal inside the VM (default PowerShell)
  terminal-windows exec NAME SCRIPT [--timeout 300]    run one PowerShell script inside the VM
  terminal-windows tune NAME [--defender] [--yes]      lighter guest: no visual effects/indexing/SysMain, high-perf power
  terminal-windows skills install [--dir PATH] | skills get
  terminal-windows connect [NAME]          let this PC's Claude Code use the VM (MCP + skill)
  terminal-windows update-helper NAME
  terminal-windows iso-info ISO [--edition NAME]      (list editions; create picks Pro by default)
  terminal-windows selftest NAME [--with-restore]      (real-VM checklist; writes logsselftest-*.md)
  terminal-windows doctor [--json]
  terminal-windows host-mode [fast|secure] [--yes]       (show / switch the host's Windows hypervisor; admin, needs a reboot)

Data: %USERPROFILE%\.terminal-windows\machines\NAME (override with TERMINAL_WINDOWS_HOME).
""";

    internal static readonly string[] Flags =
    {
        "offline", "auto-snapshot", "share-writable", "no-view", "detach", "plan", "shutdown", "force",
        "edit", "config", "sync-outbox", "json", "help", "verbose", "with-restore", "yes", "defender", "dry-run", "window",
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        // No arguments: the menu (or first-time setup), in this terminal. Double-click starts the same.
        if (args.Length == 0 && !Console.IsInputRedirected)
            return await Wizard.MenuOrSetupAsync(pauseAtEnd: Wizard.StartedByDoubleClick());
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        if (args[0] is "--version" or "version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
            return 0;
        }

        try
        {
            var parsed = ParsedArgs.Parse(args.Skip(1), Flags);
            if (parsed.Flag("verbose")) ProcessRunner.Verbose = true;
            return args[0] switch
            {
                "up" => await Lifecycle.UpAsync(parsed),
                "create" => await Lifecycle.CreateAsync(parsed),
                "start" => await Lifecycle.StartAsync(parsed),
                "pause" => await Lifecycle.PauseResumeAsync(parsed, pause: true),
                "resume" => await Lifecycle.PauseResumeAsync(parsed, pause: false),
                "stop" => await Lifecycle.StopAsync(parsed),
                "view" => await Lifecycle.ViewAsync(parsed),
                "show" => await Lifecycle.ShowHideAsync(parsed, show: true),
                "hide" => await Lifecycle.ShowHideAsync(parsed, show: false),
                "delete" => await Lifecycle.DeleteAsync(parsed),
                "ls" or "list" => await Lifecycle.ListAsync(parsed),
                "status" => await Lifecycle.StatusAsync(parsed),
                "snapshot" => await SnapshotCommands.SnapshotAsync(parsed),
                "restore" => await SnapshotCommands.RestoreAsync(parsed),
                "delete-snapshot" => await SnapshotCommands.DeleteAsync(parsed),
                "snapshots" => await SnapshotCommands.ListAsync(parsed),
                "screen" => await Lifecycle.ScreenAsync(parsed),
                "resolution" => await Lifecycle.ResolutionAsync(parsed),
                "setup-keys" => await Lifecycle.SetupKeysAsync(parsed),
                "clone" => await SnapshotCommands.CloneAsync(parsed),
                "update-helper" => await SnapshotCommands.UpdateHelperAsync(parsed),
                "refresh-payload" => await SnapshotCommands.RefreshPayloadAsync(parsed),
                "pull" => await Transfer.PullAsync(parsed),
                "push" => await Transfer.PushAsync(parsed),
                "notes" => await Notes.RunAsync(parsed),
                "action" => await ActionCommand.RunAsync(parsed),
                "mcp" => await McpCommand.RunAsync(parsed),
                "skills" => await SkillsCommand.RunAsync(parsed),
                "connect" => await ConnectCommand.RunAsync(parsed),
                "doctor" => await Doctor.RunAsync(parsed),
                "host-mode" => await HostMode.RunAsync(parsed),
                "setup" => await Wizard.RunAsync(parsed, pauseAtEnd: Wizard.StartedByDoubleClick()),
                "iso-info" => await IsoInfoCommand.RunAsync(parsed),
                "term" => await Agent.TermAsync(parsed),
                "claude" => await Agent.ClaudeAsync(parsed),
                "codex" => await Agent.CodexAsync(parsed),
                "gemini" => await Agent.GeminiAsync(parsed),
                "exec" => await Agent.ExecAsync(parsed),
                "agent-install" => await Agent.InstallAsync(parsed),
                "tune" => await Tune.RunAsync(parsed),
                "selftest" => await SelfTest.RunAsync(parsed),
                "view-demo" => await Viewer.DemoGuest.RunAsync(parsed),
                _ => throw new UsageException($"Unknown command '{args[0]}'"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine("Run 'terminal-windows --help' for usage.");
            return 2;
        }
        catch (CliException ex)
        {
            WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is Protocol.RpcException or FormatException or ArgumentException or IOException or TimeoutException)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    private static void WriteError(string message)
    {
        // Actions promise JSON output; errors are JSON on stdout too, plus a readable line on stderr.
        Console.Error.WriteLine("error: " + message);
        Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(Output.Compact));
    }
}
