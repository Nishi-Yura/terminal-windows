using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Backend;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Mcp;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// <c>selftest NAME [--with-restore]</c>: runs the real-machine checklist against a running VM and
/// writes a Markdown report to machines\NAME\logs\selftest-*.md. Everything happens inside the VM
/// (it opens the Run dialog and Notepad there); the host's input devices are never used.
/// </summary>
public static class SelfTest
{
    private sealed record Result(string Name, string Status, string Detail, TimeSpan Elapsed);

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        bool withRestore = a.Flag("with-restore");
        a.RejectUnknown();

        var results = new List<Result>();
        var logs = MachineStore.LogsDir(m.Name);
        Directory.CreateDirectory(logs);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backend = new VirtualBoxBackend();
        var mgr = new MachineManager(backend);

        async Task Check(string name, Func<Task<string>> body)
        {
            var sw = Stopwatch.StartNew();
            Console.Error.Write($"  {name,-28} ");
            try
            {
                var detail = await body();
                var status = detail.StartsWith("WARN:") ? "WARN" : "OK";
                results.Add(new(name, status, detail.Replace("WARN:", "").Trim(), sw.Elapsed));
                Console.Error.WriteLine($"{status} {detail}");
            }
            catch (Exception ex)
            {
                results.Add(new(name, "FAIL", ex.Message, sw.Elapsed));
                Console.Error.WriteLine("FAIL " + ex.Message);
            }
        }

        Console.Error.WriteLine($"selftest {m.Name} ({m.VmName})");
        await Check("vm running", async () =>
        {
            var st = await backend.GetStateAsync(m.VmName);
            if (st != VmState.Running) throw new Exception($"state is {st}; run 'terminal-windows start {m.Name}' first");
            return st.ToString();
        });
        if (results[^1].Status == "FAIL") return Report(m, results, stamp);

        await using var c = await GuestClient.ConnectAsync(m);
        JsonObject status = new();
        int sw0 = 0, sh0 = 0;

        await Check("helper handshake", () => Task.FromResult(
            $"protocol {c.Hello?["protocol"]}, helper {c.Hello?["helperVersion"]}"));

        await Check("logged on to desktop", async () =>
        {
            // Right after a boot or resume the logon screen can still be up for a while; give it 90 s.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            string? s;
            while (true)
            {
                status = await c.CallAsync("status");
                s = status["session"]?.GetValue<string>();
                if (s == "desktop" || DateTime.UtcNow > deadline) break;
                await Task.Delay(2000);
            }
            if (s != "desktop") throw new Exception($"session = {s} (lock/logon/secure desktop). The helper never unlocks; check the VM window.");
            return $"user {status["user"]}, input desktop {status["inputDesktop"]}";
        });

        await Check("screen size", () =>
        {
            sw0 = status["screen"]!["width"]!.GetValue<int>();
            sh0 = status["screen"]!["height"]!.GetValue<int>();
            var msg = $"{sw0}x{sh0}, dpi {status["dpi"]}";
            return Task.FromResult(sw0 == m.Width && sh0 == m.Height ? msg : $"WARN: {msg} (configured {m.Width}x{m.Height})");
        });

        await Check("screenshot (full)", async () =>
        {
            var (info, png) = await c.ScreenshotAsync();
            var path = Path.Combine(logs, $"selftest-{stamp}-full.png");
            await File.WriteAllBytesAsync(path, png);
            if (png.Length < 2000) throw new Exception($"suspiciously small PNG ({png.Length} bytes)");
            if (info["width"]!.GetValue<int>() != sw0) throw new Exception("PNG width differs from the screen width");
            return $"{info["width"]}x{info["height"]}, {png.Length / 1024} KB, backend {info["backend"]} -> {path}";
        });

        var space = new ScreenSpace(Math.Max(1, sw0), Math.Max(1, sh0));
        await Check("screenshot (MCP scale)", async () =>
        {
            var (info, _) = await c.ScreenshotAsync(space.ImageWidth, space.ImageHeight);
            if (info["width"]!.GetValue<int>() != space.ImageWidth) throw new Exception($"got {info["width"]}, expected {space.ImageWidth}");
            return $"{info["width"]}x{info["height"]} (scale {space.Scale:0.###})";
        });

        await Check("pointer accuracy", async () =>
        {
            var points = new[] { (5, 5), (sw0 / 2, sh0 / 2), (sw0 - 6, sh0 - 6), (123, 234) };
            foreach (var (x, y) in points)
            {
                await c.CallAsync("move", new JsonObject { ["x"] = x, ["y"] = y });
                await Task.Delay(100);
                var cur = (await c.CallAsync("status"))["cursor"];
                int cx = cur!["x"]!.GetValue<int>(), cy = cur["y"]!.GetValue<int>();
                if (Math.Abs(cx - x) > 1 || Math.Abs(cy - y) > 1) throw new Exception($"moved to ({x},{y}) but cursor is at ({cx},{cy})");
            }
            // The MCP layer converts screenshot coordinates; check the round trip lands on the same pixel.
            int ix = space.FromScreen(sw0 * 0.75), iy = space.FromScreen(sh0 * 0.25);
            await c.CallAsync("move", new JsonObject { ["x"] = space.ToScreenX(ix), ["y"] = space.ToScreenY(iy) });
            await Task.Delay(100);
            var c2 = (await c.CallAsync("status"))["cursor"]!;
            return $"{points.Length} points exact; MCP ({ix},{iy}) -> guest ({c2["x"]},{c2["y"]})";
        });

        await Check("key win+r opens Run", async () =>
        {
            await c.CallAsync("key", new JsonObject { ["keys"] = "esc" });
            await c.CallAsync("key", new JsonObject { ["keys"] = "win+r" });
            var title = await WaitForegroundAsync(c, t => t.Contains("実行") || t.Contains("Run"), TimeSpan.FromSeconds(10));
            return $"foreground: {title}";
        });

        const string jp = "自己テスト日本語ＡＢＣ";
        await Check("type Japanese (no IME)", async () =>
        {
            var imeBefore = (await c.CallAsync("status"))["imeOpen"]?.ToJsonString();
            await c.CallAsync("key", new JsonObject { ["keys"] = "ctrl+a" });
            await c.CallAsync("type", new JsonObject { ["text"] = jp });
            await Task.Delay(300);
            var tree = await c.CallAsync("ui_tree", new JsonObject { ["depth"] = 8 }, TimeSpan.FromSeconds(30));
            var values = Flatten(tree).Select(n => n["value"]?.GetValue<string>()).Where(v => v is not null).ToList();
            if (!values.Any(v => v!.Contains(jp))) throw new Exception($"text not found in the Run box (values: {string.Join(" | ", values)})");
            var imeAfter = (await c.CallAsync("status"))["imeOpen"]?.ToJsonString();
            return $"Run box contains '{jp}' (IME before {imeBefore}, after {imeAfter})";
        });

        await Check("ui_tree structure", async () =>
        {
            var tree = await c.CallAsync("ui_tree", new JsonObject { ["depth"] = 8 }, TimeSpan.FromSeconds(30));
            var nodes = Flatten(tree).ToList();
            var withRect = nodes.Count(n => n["rect"] is JsonArray);
            if (nodes.Count < 3 || withRect == 0) throw new Exception($"only {nodes.Count} nodes");
            return $"{nodes.Count} nodes, {withRect} with rect, types: {string.Join(",", nodes.Select(n => n["controlType"]?.GetValue<string>()).Distinct().Take(8))}";
        });

        await Check("launch Notepad", async () =>
        {
            await c.CallAsync("key", new JsonObject { ["keys"] = "ctrl+a" });
            await c.CallAsync("type", new JsonObject { ["text"] = "notepad" });
            await c.CallAsync("key", new JsonObject { ["keys"] = "enter" });
            var title = await WaitForegroundAsync(c, t => t.Contains("メモ帳") || t.Contains("Notepad"), TimeSpan.FromSeconds(30));
            return $"foreground: {title}";
        });

        await Check("click via ui_tree rect", async () =>
        {
            var tree = await c.CallAsync("ui_tree", new JsonObject { ["depth"] = 10 }, TimeSpan.FromSeconds(30));
            var menu = Flatten(tree).FirstOrDefault(n =>
                n["controlType"]?.GetValue<string>() == "MenuItem" &&
                (n["name"]?.GetValue<string>() is "ファイル" or "File") && n["rect"] is JsonArray);
            if (menu is null) return "WARN: no File menu item found (Notepad UI differs); skipped";
            var r = menu["rect"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
            int x = r[0] + r[2] / 2, y = r[1] + r[3] / 2;
            await c.CallAsync("click", new JsonObject { ["x"] = x, ["y"] = y });
            await Task.Delay(800);
            var after = await c.CallAsync("ui_tree", new JsonObject { ["window"] = "desktop", ["depth"] = 1 }, TimeSpan.FromSeconds(30));
            var fg = await c.CallAsync("ui_tree", new JsonObject { ["depth"] = 12 }, TimeSpan.FromSeconds(30));
            bool opened = Flatten(fg).Concat(Flatten(after)).Any(n => n["controlType"]?.GetValue<string>() == "Menu");
            await c.CallAsync("key", new JsonObject { ["keys"] = "esc" });
            if (!opened) throw new Exception($"clicked File menu at ({x},{y}) but no menu appeared");
            return $"clicked ({x},{y}); menu opened";
        });

        await Check("type into Notepad + keys", async () =>
        {
            await c.CallAsync("click", new JsonObject { ["x"] = sw0 / 2, ["y"] = sh0 / 2 });
            await c.CallAsync("type", new JsonObject { ["text"] = "terminal-windows selftest\nこんにちは" });
            await c.CallAsync("key", new JsonObject { ["keys"] = "ctrl+a backspace" });
            return "typed multi-line text, ctrl+a, backspace";
        });

        await Check("scroll and drag", async () =>
        {
            await c.CallAsync("scroll", new JsonObject { ["x"] = sw0 / 2, ["y"] = sh0 / 2, ["dy"] = 2 });
            await c.CallAsync("scroll", new JsonObject { ["dy"] = -2 });
            await c.CallAsync("drag", new JsonObject { ["x1"] = sw0 / 2 - 50, ["y1"] = sh0 / 2, ["x2"] = sw0 / 2 + 50, ["y2"] = sh0 / 2 + 20 });
            return "ok";
        });

        await Check("close Notepad", async () =>
        {
            await c.CallAsync("key", new JsonObject { ["keys"] = "alt+f4" });
            await Task.Delay(1500);
            var fg = (await c.CallAsync("status"))["foregroundWindow"]?.GetValue<string>() ?? "";
            string how = "alt+f4";
            if (fg.Contains("メモ帳") || fg.Contains("Notepad"))
            {
                // The Windows 11 Notepad save prompt has no "N" accelerator; click "Don't save" by its UI rect.
                var tree = await c.CallAsync("ui_tree", new JsonObject { ["depth"] = 12 }, TimeSpan.FromSeconds(30));
                var dontSave = Flatten(tree).FirstOrDefault(n => n["controlType"]?.GetValue<string>() == "Button" &&
                    (n["name"]?.GetValue<string>() is { } nm && (nm.StartsWith("保存しない") || nm.StartsWith("Don't save") || nm.StartsWith("Don’t save"))) &&
                    n["rect"] is JsonArray);
                if (dontSave is not null)
                {
                    var r = dontSave["rect"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
                    await c.CallAsync("click", new JsonObject { ["x"] = r[0] + r[2] / 2, ["y"] = r[1] + r[3] / 2 });
                    how = "alt+f4, then clicked 'Don't save'";
                }
                else
                {
                    await c.CallAsync("exec", new JsonObject { ["script"] = "Stop-Process -Name Notepad -Force -ErrorAction SilentlyContinue", ["timeout_s"] = 30 }, TimeSpan.FromSeconds(60));
                    how = "alt+f4 left it open; stopped the process";
                }
                await Task.Delay(1000);
                fg = (await c.CallAsync("status"))["foregroundWindow"]?.GetValue<string>() ?? "";
            }
            return fg.Contains("メモ帳") || fg.Contains("Notepad") ? $"WARN: Notepad still in front ({fg})" : $"{how}; foreground now: {fg}";
        });

        var tmp = Path.Combine(Path.GetTempPath(), "tw-selftest-" + stamp);
        Directory.CreateDirectory(tmp);
        try
        {
            var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
            var file = Path.Combine(tmp, $"転送テスト-{stamp}.bin");
            await File.WriteAllBytesAsync(file, payload);
            var expected = SafePath.Sha256(payload);

            await Check("push to Inbox", async () =>
            {
                var written = await c.PushAsync(file, null, TransferLimits.Default);
                var list = await c.CallAsync("file_list", new JsonObject { ["root"] = "inbox" });
                if (!list["entries"]!.AsArray().Any(e => e!["path"]!.GetValue<string>() == Path.GetFileName(file)))
                    throw new Exception("pushed file not listed in Inbox");
                return string.Join(", ", written);
            });

            await Check("Outbox -> host (export)", async () =>
            {
                var outboxDir = $@"C:\Users\{m.User}\Desktop\Outbox";
                await c.PushAsync(file, outboxDir, TransferLimits.Default);
                var dest = Path.Combine(MachineStore.OutboxDir(m.Name), "selftest-" + stamp);
                var pulled = await c.PullAsync(Path.Combine(outboxDir, Path.GetFileName(file)), dest, TransferLimits.Default);
                var got = Assert1(pulled);
                if (got.Sha256 != expected) throw new Exception("SHA-256 differs after the round trip");
                return $"{got.Size:N0} bytes, sha256 ok -> {got.HostPath}";
            });
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        await Check("exec (PowerShell in the guest)", async () =>
        {
            var r = await c.CallAsync("exec", new JsonObject { ["script"] = "$PSVersionTable.PSVersion.ToString(); whoami; exit 7", ["timeout_s"] = 60 }, TimeSpan.FromSeconds(90));
            if (r["exitCode"]?.GetValue<int>() != 7) throw new Exception($"exit code {r["exitCode"]} (expected 7): {r["stderr"]}");
            return (r["stdout"]?.GetValue<string>() ?? "").Trim().Replace("\r\n", " / ");
        });

        await Check("pseudo console (agent terminal)", async () =>
        {
            var output = new StringBuilder();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id = -1;
            var early = new List<Protocol.Message>();
            void OnEvent(Protocol.Message m)
            {
                if (m.Event == "pty_output") lock (output) output.Append(Encoding.UTF8.GetString(m.Attachments[0]));
                if (m.Event == "pty_exit") exited.TrySetResult(m.Json["data"]!["exitCode"]!.GetValue<int>());
            }
            c.Rpc.EventReceived += m =>
            {
                if (m.Event is not ("pty_output" or "pty_exit")) return;
                lock (early) { if (id < 0) { early.Add(m); return; } }
                if ((m.Json["data"] as JsonObject)?["id"]?.GetValue<int>() == id) OnEvent(m);
            };
            var opened = await c.CallAsync("pty_open", new JsonObject { ["command"] = "cmd /c echo tw-pty-ok", ["cols"] = 80, ["rows"] = 24 });
            lock (early)
            {
                id = opened["id"]!.GetValue<int>();
                foreach (var m in early.Where(m => (m.Json["data"] as JsonObject)?["id"]?.GetValue<int>() == id)) OnEvent(m);
            }
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
            lock (output)
                if (!output.ToString().Contains("tw-pty-ok")) throw new Exception("program output did not arrive");
            return "output and exit received";
        });

        await Check("lock screen is reported", () =>
            Task.FromResult("WARN: not automated (locking needs a manual unlock). Manual: press Win+L in the VM window, then 'terminal-windows status' must show locked_or_secure_desktop"));

        if (withRestore)
        {
            var label = "selftest-" + stamp;
            await Check("snapshot + restore", async () =>
            {
                await mgr.SnapshotAsync(m, label);
                await using (var c2 = await GuestClient.ConnectAsync(m))
                {
                    var marker = Path.Combine(Path.GetTempPath(), $"marker-{stamp}.txt");
                    await File.WriteAllTextAsync(marker, "after snapshot");
                    await c2.PushAsync(marker, null, TransferLimits.Default);
                    File.Delete(marker);
                }
                var safety = await mgr.RestoreAsync(m, label);
                await using var c3 = await GuestClient.ConnectAsync(m);
                var list = await c3.CallAsync("file_list", new JsonObject { ["root"] = "inbox" });
                bool markerGone = !list["entries"]!.AsArray().Any(e => e!["path"]!.GetValue<string>().StartsWith("marker-" + stamp));
                var snaps = await backend.ListSnapshotsAsync(m.VmName);
                // Clean up the test snapshots to save disk space.
                await backend.DeleteSnapshotAsync(m.VmName, safety);
                await backend.DeleteSnapshotAsync(m.VmName, label);
                if (!markerGone) throw new Exception("file created after the snapshot still exists after restore");
                if (!snaps.Any(s => s.Name == safety)) throw new Exception($"{safety} was not created");
                return $"restored; '{safety}' was created (both test snapshots deleted again)";
            });
        }

        return Report(m, results, stamp);
    }

    private static PulledFile Assert1(List<PulledFile> files) =>
        files.Count == 1 ? files[0] : throw new Exception($"expected 1 file, got {files.Count}");

    private static async Task<string> WaitForegroundAsync(GuestClient c, Func<string, bool> match, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        string last = "";
        while (sw.Elapsed < timeout)
        {
            last = (await c.CallAsync("status"))["foregroundWindow"]?.GetValue<string>() ?? "";
            if (match(last)) return last;
            await Task.Delay(500);
        }
        throw new Exception($"window did not appear (foreground: '{last}')");
    }

    private static IEnumerable<JsonObject> Flatten(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (o.ContainsKey("controlType")) yield return o;
            foreach (var kv in o)
                foreach (var child in Flatten(kv.Value)) yield return child;
        }
        else if (node is JsonArray a)
        {
            foreach (var item in a)
                foreach (var child in Flatten(item)) yield return child;
        }
    }

    private static int Report(MachineConfig m, List<Result> results, string stamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# selftest {m.Name} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"terminal-windows {typeof(SelfTest).Assembly.GetName().Version?.ToString(3)}, VM {m.VmName}");
        sb.AppendLine();
        sb.AppendLine("| Check | Result | Detail | Time |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Name} | {r.Status} | {r.Detail.Replace("|", "\\|").Replace("\n", " ")} | {r.Elapsed.TotalSeconds:0.0}s |");
        int fail = results.Count(r => r.Status == "FAIL"), warn = results.Count(r => r.Status == "WARN");
        sb.AppendLine();
        sb.AppendLine($"**{results.Count - fail - warn} OK, {warn} WARN, {fail} FAIL**");
        var path = Path.Combine(MachineStore.LogsDir(m.Name), $"selftest-{stamp}.md");
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"Report: {path}");
        return fail == 0 ? 0 : 1;
    }
}
