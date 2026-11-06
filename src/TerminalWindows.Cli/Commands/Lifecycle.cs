using System.Text.Json.Nodes;
using TerminalWindows.Cli.Backend;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Viewer;

namespace TerminalWindows.Cli.Commands;

public static class Lifecycle
{
    private static CreateOptions ReadCreateOptions(ParsedArgs a, string name, string? iso) => new(
        name,
        iso ?? "",
        Cpus: a.Int("cpus", DefaultCpus()),
        MemoryGb: a.Int("memory", DefaultMemoryGb()),
        DiskGb: a.Int("disk", 64),
        Width: a.Int("width", 1600),
        Height: a.Int("height", 900),
        Offline: a.Flag("offline"),
        Locale: a.Opt("locale") ?? "ja-JP",
        UiLanguage: a.Opt("ui-language"),
        TimeZone: a.Opt("timezone"),
        ImageIndex: a.Opt("image-index") is { } ix ? int.Parse(ix) : null,
        ProductKey: a.Opt("product-key"),
        AutoSnapshot: a.Flag("auto-snapshot"),
        Share: a.Opt("share") is { } sh ? Path.GetFullPath(sh) : null,
        ShareWritable: a.Flag("share-writable"),
        Edition: a.Opt("edition"),
        DiskBus: VirtualBoxBackend.ValidateDiskBus(a.Opt("disk-bus") ?? "sata"));

    /// <summary>8 GB for the guest on 16 GB+ hosts, 6 GB below that (the spec allows 6-8 GB).</summary>
    public static int DefaultMemoryGb() => Doctor.TotalMemoryGb() >= 15.9 ? 8 : 6;

    /// <summary>
    /// 4 vCPUs, but never more than half the host's logical CPUs. (2 vCPUs under the Windows Hypervisor
    /// Platform hung twice in the firmware's AP start-up on a real host, while 4 booted every time.)
    /// --cpus overrides.
    /// </summary>
    public static int DefaultCpus() => DefaultCpusFor(Environment.ProcessorCount);

    public static int DefaultCpusFor(int hostLogicalCpus) => Math.Max(1, Math.Min(4, hostLogicalCpus / 2));

    public static async Task<int> DeleteAsync(ParsedArgs a)
    {
        var name = a.Pos(0, "NAME");
        bool yes = a.Flag("yes");
        a.RejectUnknown();
        var m = MachineStore.Load(name);
        if (!yes)
        {
            Console.WriteLine($"This permanently deletes {m.Name} ({m.VmName}): its disk, snapshots, notes, inbox and outbox.");
            Console.WriteLine($"Nothing was changed. Re-run with --yes to delete:  terminal-windows delete {m.Name} --yes");
            return 1;
        }
        await new MachineManager().DeleteAsync(m);
        Console.WriteLine($"Deleted {m.Name}.");
        return 0;
    }

    public static readonly string[] CreateFlags = { "offline", "auto-snapshot", "share-writable", "no-view", "detach", "plan" };

    /// <summary>up: create if missing -> start -> wait for helper -> viewer. Never reinstalls an existing VM.</summary>
    public static async Task<int> UpAsync(ParsedArgs a)
    {
        var name = a.PosOrNull(0) ?? MachineStore.DefaultName;
        var iso = a.Opt("iso");
        bool noView = a.Flag("no-view"), detach = a.Flag("detach"), plan = a.Flag("plan");
        var opts = ReadCreateOptions(a, name, iso);
        var protocol = a.Opt("graphics");
        a.RejectUnknown();
        var mgr = new MachineManager();

        if (plan)
        {
            Console.WriteLine("Plan (nothing is changed):");
            foreach (var s in mgr.Plan(opts)) Console.WriteLine("  - " + s);
            return 0;
        }

        MachineConfig m;
        if (!MachineStore.Exists(name))
        {
            if (iso is null) throw new UsageException($"Machine '{name}' does not exist yet. Pass --iso <Windows 11 ISO> to create it.");
            if (detach)
            {
                m = await CreateDetachedAsync(mgr, opts);
                Console.WriteLine($"Setup is running in the background. Check progress with 'terminal-windows status {name}'; run 'terminal-windows up {name}' later to finish.");
                return 0;
            }
            m = await mgr.CreateAsync(opts);
        }
        else
        {
            m = MachineStore.Load(name);
            if (detach)
            {
                var state = await mgr.Backend.GetStateAsync(m.VmName);
                if (state is not (VmState.Running or VmState.Paused)) await mgr.Backend.StartAsync(m.VmName);
                Console.WriteLine($"{m.VmName} started (headless).");
                return 0;
            }
            await mgr.EnsureRunningAsync(m);
        }

        if (noView)
        {
            Console.WriteLine($"{name} is ready. MCP: terminal-windows mcp {name}");
            return 0;
        }
        return await ViewMachineAsync(mgr, m, protocol);
    }

    // Same as create, but return once the installer has booted; 'up'/'start'/'status' finalise setup later.
    private static Task<MachineConfig> CreateDetachedAsync(MachineManager mgr, CreateOptions opts) =>
        mgr.CreateAsync(opts, waitForSetup: false);

    public static async Task<int> CreateAsync(ParsedArgs a)
    {
        var name = a.Pos(0, "NAME");
        var iso = a.Opt("iso") ?? throw new UsageException("--iso PATH is required");
        var opts = ReadCreateOptions(a, name, iso);
        a.RejectUnknown();
        var m = await new MachineManager().CreateAsync(opts);
        Console.WriteLine($"Created {m.Name} ({m.VmName}). View it with 'terminal-windows view {m.Name}'.");
        return 0;
    }

    public static async Task<int> StartAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        a.RejectUnknown();
        await new MachineManager().EnsureRunningAsync(m);
        Console.WriteLine($"{m.Name} is running.");
        return 0;
    }

    public static async Task<int> PauseResumeAsync(ParsedArgs a, bool pause)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        a.RejectUnknown();
        var b = new VirtualBoxBackend();
        if (pause) await b.PauseAsync(m.VmName); else await b.ResumeAsync(m.VmName);
        Console.WriteLine(pause ? "paused" : "resumed");
        return 0;
    }

    public static async Task<int> StopAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        bool shutdown = a.Flag("shutdown"), force = a.Flag("force"), auto = a.Flag("auto-snapshot");
        a.RejectUnknown();
        if (shutdown && force) throw new UsageException("Use either --shutdown or --force");
        var mode = force ? "force" : shutdown ? "shutdown" : "savestate";
        await new MachineManager().StopAsync(m, mode, auto);
        Console.WriteLine(mode == "savestate"
            ? $"{m.Name} state saved (apps and windows resume with 'terminal-windows start {m.Name}')."
            : $"{m.Name} stopped.");
        return 0;
    }

    public static async Task<int> ViewAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        var protocol = a.Opt("graphics");
        a.RejectUnknown();
        var mgr = new MachineManager();
        await mgr.EnsureRunningAsync(m);
        return await ViewMachineAsync(mgr, m, protocol);
    }

    private static async Task<int> ViewMachineAsync(MachineManager mgr, MachineConfig m, string? protocol)
    {
        var caps = TerminalCaps.Detect(protocol);
        if (caps.Protocol == GraphicsProtocol.None || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.WriteLine($"{caps.TerminalName} cannot show inline graphics; opening the VirtualBox window instead.");
            await mgr.Backend.ShowWindowAsync(m.VmName);
            return 0;
        }
        await new TerminalViewer(m, caps).RunAsync();
        Console.WriteLine($"Viewer closed. {m.Name} keeps running (stop it with 'terminal-windows stop {m.Name}').");
        return 0;
    }

    public static async Task<int> ShowHideAsync(ParsedArgs a, bool show)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        a.RejectUnknown();
        var b = new VirtualBoxBackend();
        if (show) await b.ShowWindowAsync(m.VmName); else await b.HideWindowAsync(m.VmName);
        return 0;
    }

    public static async Task<int> ListAsync(ParsedArgs a)
    {
        a.RejectUnknown();
        var b = new VirtualBoxBackend();
        var arr = new JsonArray();
        foreach (var n in MachineStore.List())
        {
            var m = MachineStore.Load(n);
            arr.Add(new JsonObject
            {
                ["name"] = n, ["vm"] = m.VmName, ["state"] = (await b.GetStateAsync(m.VmName)).ToString(),
                ["setupComplete"] = m.SetupComplete, ["port"] = m.HostPort,
            });
        }
        Output.Json(arr);
        return 0;
    }

    public static async Task<int> StatusAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        a.RejectUnknown();
        var mgr = new MachineManager();
        var state = await mgr.Backend.GetStateAsync(m.VmName);
        var o = new JsonObject
        {
            ["name"] = m.Name, ["vm"] = m.VmName, ["state"] = state.ToString(), ["setupComplete"] = m.SetupComplete,
            ["hostPort"] = m.HostPort, ["cpus"] = m.Cpus, ["memoryGb"] = m.MemoryGb, ["resolution"] = $"{m.Width}x{m.Height}",
            ["offline"] = m.Offline, ["autoSnapshot"] = m.AutoSnapshot, ["dataDir"] = MachineStore.MachineDir(m.Name),
        };
        if (state == VmState.Running)
        {
            try
            {
                await using var c = await GuestClient.ConnectAsync(m, TimeSpan.FromSeconds(3));
                o["helper"] = await c.CallAsync("status");
                if (!m.SetupComplete) await mgr.FinalizeSetupAsync(m);
            }
            catch (CliException)
            {
                o["helper"] = m.SetupComplete ? "unreachable" : "setup in progress (helper not up yet)";
            }
        }
        Output.Json(o);
        return 0;
    }
}
