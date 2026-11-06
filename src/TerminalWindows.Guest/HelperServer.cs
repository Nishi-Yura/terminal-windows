using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerminalWindows.Guest.Capture;
using TerminalWindows.Protocol;

namespace TerminalWindows.Guest;

/// <summary>
/// TCP control server inside the guest. Reachable from the host only through VirtualBox NAT port
/// forwarding (bound to 127.0.0.1 on the host). Every connection must authenticate with the token.
/// </summary>
public sealed class HelperServer
{
    private readonly GuestConfig _config;
    private readonly CaptureManager _capture = new();
    private readonly GuestFiles _files;
    private readonly SelfUpdater _updater = new();
    private readonly GuestTerminals _terminals = new();
    private readonly ConcurrentDictionary<RpcServerSession, CancellationTokenSource> _streams = new();
    private readonly ConcurrentDictionary<RpcServerSession, ConcurrentDictionary<string, byte>> _heldButtons = new();
    private readonly string _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
    private readonly string _selfSha;

    /// <param name="desktopOverride">Tests only: where Inbox/Outbox live.</param>
    public HelperServer(GuestConfig config, string? desktopOverride = null)
    {
        _config = config;
        _files = new GuestFiles(desktopOverride ?? GuestPaths.UserDesktop(config.User));
        try { _selfSha = SafePath.Sha256File(Environment.ProcessPath!); } catch { _selfSha = ""; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Parse(_config.BindAddress), _config.Port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _started.TrySetResult(BoundPort);
        await using var stop = ct.Register(listener.Stop);
        Log.Write($"helper {_version} listening on {BoundPort}");
        var allowed = _config.AllowedRemote;
        var seen = new HashSet<string>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (SocketException ex)
                {
                    // e.g. a connection reset before accept completed: keep listening.
                    Log.Write("accept failed: " + ex.Message);
                    continue;
                }
                Accept(client, allowed, seen, ct);
            }
        }
        finally
        {
            // Free the port for the restart loop in Program, and release the capture session.
            listener.Stop();
            foreach (var kv in _streams) kv.Value.Cancel();
            _capture.Dispose();
        }
    }

    private void Accept(TcpClient client, string allowed, HashSet<string> seen, CancellationToken ct)
    {
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            // Defence in depth on top of the guest firewall rule: only the NAT gateway may connect.
            if (remote is not null && seen.Add(remote.ToString()))
                Log.Write($"first connection from {remote}"); // records the real NAT source address for validation
            if (remote is null || !ProtocolConstants.AddressAllowed(remote, allowed))
            {
                Log.Write($"rejected connection from {remote}");
                client.Dispose();
                return;
            }
            client.NoDelay = true;
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var session = new RpcServerSession(client.GetStream(), _config.Token, Hello);
                    try { await session.RunAsync(HandleAsync, ct); }
                    catch (Exception ex) { Log.Write("session error: " + ex.Message); }
                    finally
                    {
                        if (_streams.TryRemove(session, out var cts)) cts.Cancel();
                        _terminals.CloseAll(session);
                        if (_heldButtons.TryRemove(session, out var held))
                        {
                            // Never leave a button pressed in the guest because the viewer went away.
                            foreach (var b in held.Keys)
                            {
                                try
                                {
                                    Native.GetCursorPos(out var p);
                                    InputInjector.Button(p.X, p.Y, b, down: false);
                                }
                                catch (Exception ex) { Log.Write("release held button: " + ex.Message); }
                            }
                        }
                        if (!session.Authenticated) Log.Write($"dropped unauthenticated connection from {remote}");
                    }
                }
            }, ct);
        }
    }

    private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int BoundPort { get; private set; }
    public Task<int> Started => _started.Task;

    private JsonObject Hello() => new()
    {
        ["protocol"] = ProtocolConstants.Version,
        ["helperVersion"] = _version,
        ["helperSha256"] = _selfSha,
    };

    private static int I(JsonObject a, string k, int? def = null) =>
        a[k] is JsonNode n ? n.GetValue<int>() : def ?? throw new ArgumentException($"Missing argument '{k}'");

    private static int? OptI(JsonObject a, string k) => a[k] is JsonNode n ? n.GetValue<int>() : null;
    private static string S(JsonObject a, string k, string? def = null) =>
        a[k]?.GetValue<string>() ?? def ?? throw new ArgumentException($"Missing argument '{k}'");

    private async Task<Reply> HandleAsync(Message req, RpcServerSession session, CancellationToken ct)
    {
        var a = req.Args;
        switch (req.Op)
        {
            case "status":
                return Reply.Of(Status());

            case "screenshot":
            {
                var img = _capture.Capture();
                int fullW = img.Width, fullH = img.Height;
                if (OptI(a, "max_width") is int mw && OptI(a, "max_height") is int mh)
                {
                    var (w, h) = Imaging.Fit(img.Width, img.Height, mw, mh);
                    img = Imaging.Resize(img, w, h);
                }
                var png = Imaging.EncodePng(img);
                return new Reply(new JsonObject
                {
                    ["width"] = img.Width, ["height"] = img.Height,
                    ["screenWidth"] = fullW, ["screenHeight"] = fullH,
                    ["format"] = "png", ["backend"] = _capture.Backend,
                }, new ReadOnlyMemory<byte>[] { png });
            }

            case "click":
                RequireDesktop();
                await InputInjector.ClickAsync(I(a, "x"), I(a, "y"), S(a, "button", "left"), I(a, "count", 1));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "move":
                RequireDesktop();
                InputInjector.Move(I(a, "x"), I(a, "y"));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "mouse":
            {
                // Real-time button events from the viewer. Buttons still held when the session ends
                // are released automatically (see the session cleanup in Accept).
                RequireDesktop();
                var action = S(a, "action");
                var button = S(a, "button", "left");
                int x = I(a, "x"), y = I(a, "y");
                var held = _heldButtons.GetOrAdd(session, _ => new ConcurrentDictionary<string, byte>());
                switch (action)
                {
                    case "down":
                        InputInjector.Button(x, y, button, down: true);
                        held[button] = 0;
                        break;
                    case "up":
                        InputInjector.Button(x, y, button, down: false);
                        held.TryRemove(button, out _);
                        break;
                    case "move":
                        InputInjector.Move(x, y);
                        break;
                    default:
                        throw new ArgumentException($"Unknown mouse action '{action}'");
                }
                return Reply.Of(new JsonObject { ["done"] = true });
            }

            case "drag":
                RequireDesktop();
                await InputInjector.DragAsync(I(a, "x1"), I(a, "y1"), I(a, "x2"), I(a, "y2"), S(a, "button", "left"));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "scroll":
                RequireDesktop();
                InputInjector.Scroll(OptI(a, "x"), OptI(a, "y"), I(a, "dx", 0), I(a, "dy", 0));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "type":
                RequireDesktop();
                await InputInjector.TypeAsync(S(a, "text"));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "key":
                RequireDesktop();
                await InputInjector.KeyAsync(S(a, "keys"));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "ui_tree":
                return Reply.Of(await UiaTree.GetAsync(a["window"]?.GetValue<string>(), I(a, "depth", 6), I(a, "max_nodes", 1500), TimeSpan.FromSeconds(20)));

            case "set_resolution":
                return Reply.Of(SetResolution(I(a, "width"), I(a, "height")));

            case "file_list":
                return Reply.Of(_files.List(a["root"]?.GetValue<string>()));

            case "file_read":
            {
                var data = _files.Read(S(a, "base"), S(a, "path"), a["offset"]!.GetValue<long>(), I(a, "length", ProtocolConstants.FileChunkBytes));
                return new Reply(new JsonObject { ["length"] = data.Length }, new ReadOnlyMemory<byte>[] { data });
            }

            case "file_hash":
                return Reply.Of(new JsonObject { ["sha256"] = _files.Hash(S(a, "base"), S(a, "path")) });

            case "file_write":
            {
                if (req.Attachments.Count != 1) throw new ArgumentException("file_write needs exactly one attachment");
                var dest = _files.ResolveRoot(a["dest"]?.GetValue<string>() ?? "inbox");
                _files.WriteChunk(dest, S(a, "path"), a["offset"]!.GetValue<long>(), a["total"]!.GetValue<long>(), req.Attachments[0]);
                return Reply.Of(new JsonObject { ["done"] = true });
            }

            case "file_commit":
            {
                var dest = _files.ResolveRoot(a["dest"]?.GetValue<string>() ?? "inbox");
                Directory.CreateDirectory(dest);
                var final = _files.Commit(dest, S(a, "path"), S(a, "sha256"));
                return Reply.Of(new JsonObject { ["path"] = final });
            }

            case "stream_start":
            {
                var w = I(a, "width"); var h = I(a, "height"); var fps = Math.Clamp(I(a, "fps", 30), 1, 30);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (_streams.TryRemove(session, out var old)) old.Cancel();
                _streams[session] = cts;
                _ = Task.Run(() => StreamLoopAsync(session, w, h, fps, cts.Token));
                return Reply.Of(new JsonObject { ["started"] = true });
            }

            case "stream_stop":
                if (_streams.TryRemove(session, out var s)) s.Cancel();
                return Reply.Of(new JsonObject { ["stopped"] = true });

            case "exec":
                return Reply.Of(await GuestTerminals.ExecAsync(S(a, "script"), I(a, "timeout_s", 300), ct));

            case "pty_open":
                return Reply.Of(_terminals.Open(session, S(a, "command"), (short)I(a, "cols", 120), (short)I(a, "rows", 30), a["cwd"]?.GetValue<string>()));

            case "pty_input":
                if (req.Attachments.Count != 1) throw new ArgumentException("pty_input needs one attachment");
                _terminals.Input(session, I(a, "id"), req.Attachments[0]);
                return Reply.Of(new JsonObject { ["done"] = true });

            case "pty_resize":
                _terminals.Resize(session, I(a, "id"), (short)I(a, "cols"), (short)I(a, "rows"));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "pty_close":
                _terminals.Close(session, I(a, "id"));
                return Reply.Of(new JsonObject { ["done"] = true });

            case "update_begin":
                _updater.Begin(a["size"]!.GetValue<long>(), S(a, "sha256"));
                return Reply.Of(new JsonObject { ["ready"] = true });

            case "update_chunk":
                if (req.Attachments.Count != 1) throw new ArgumentException("update_chunk needs one attachment");
                _updater.Chunk(a["offset"]!.GetValue<long>(), req.Attachments[0]);
                return Reply.Of(new JsonObject { ["done"] = true });

            case "update_commit":
                return Reply.Of(new JsonObject { ["accepted"] = _updater.Commit() });

            default:
                throw new ArgumentException($"Unknown op '{req.Op}'");
        }
    }

    private static void RequireDesktop()
    {
        if (SessionState.Describe() != "desktop")
            throw new InvalidOperationException("The guest is on the lock/logon screen or a secure desktop. The helper does not unlock it; ask the user.");
    }

    private JsonObject Status()
    {
        var (vx, vy, vw, vh) = InputInjector.VirtualScreen();
        var fg = Native.GetForegroundWindow();
        JsonNode? setup = null;
        try { setup = JsonNode.Parse(File.ReadAllText(GuestPaths.StateFile)); } catch { }
        return new JsonObject
        {
            ["helperVersion"] = _version,
            ["helperSha256"] = _selfSha,
            ["session"] = SessionState.Describe(),
            ["inputDesktop"] = SessionState.InputDesktop(),
            ["user"] = Environment.UserName,
            ["screen"] = new JsonObject { ["x"] = vx, ["y"] = vy, ["width"] = vw, ["height"] = vh },
            ["dpi"] = Native.GetDpiForSystem(),
            ["foregroundWindow"] = fg == IntPtr.Zero ? null : Native.WindowTitle(fg),
            ["imeOpen"] = InputInjector.ImeOpen(),
            ["cursor"] = Native.GetCursorPos(out var cur) ? new JsonObject { ["x"] = cur.X, ["y"] = cur.Y } : null,
            ["captureBackend"] = _capture.Backend,
            ["setup"] = setup,
            ["offline"] = _config.Offline,
        };
    }

    /// <summary>
    /// Sets the screen size. If the display driver has no such mode (VMware's SVGA driver lists 1366x768
    /// but not 1600x900), the largest listed mode that fits and has about the same shape is used instead.
    /// The reply says which size was set.
    /// </summary>
    public static JsonObject SetResolution(int width, int height)
    {
        int r = TryMode(width, height);
        if (r == Native.DISP_CHANGE_BADMODE && ClosestMode(ListModes(), width, height) is { } alt)
        {
            r = TryMode(alt.W, alt.H);
            if (r == 0) { width = alt.W; height = alt.H; }
        }
        return new JsonObject { ["result"] = r, ["ok"] = r == 0, ["width"] = width, ["height"] = height };
    }

    private static int TryMode(int width, int height)
    {
        var dm = new Native.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
        if (!Native.EnumDisplaySettingsW(null, Native.ENUM_CURRENT_SETTINGS, ref dm)) throw new InvalidOperationException("EnumDisplaySettings failed");
        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT;
        return Native.ChangeDisplaySettingsW(ref dm, Native.CDS_UPDATEREGISTRY);
    }

    private static List<(int W, int H)> ListModes()
    {
        var list = new List<(int, int)>();
        var dm = new Native.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
        for (int i = 0; Native.EnumDisplaySettingsW(null, i, ref dm); i++) list.Add((dm.dmPelsWidth, dm.dmPelsHeight));
        return list.Distinct().ToList();
    }

    /// <summary>The largest mode no bigger than the request whose aspect ratio is within 2%; else the largest that fits.</summary>
    public static (int W, int H)? ClosestMode(IEnumerable<(int W, int H)> modes, int width, int height)
    {
        var fits = modes.Where(m => m.W <= width && m.H <= height).ToList();
        double want = (double)width / height;
        var sameShape = fits.Where(m => Math.Abs((double)m.W / m.H - want) / want < 0.02).ToList();
        var pool = sameShape.Count > 0 ? sameShape : fits;
        return pool.Count == 0 ? null : pool.OrderByDescending(m => (long)m.W * m.H).First();
    }

    private async Task StreamLoopAsync(RpcServerSession session, int width, int height, int fps, CancellationToken ct)
    {
        ulong last = 0;
        BgraImage? lastSource = null; // the capture backends return the same object while the screen is unchanged
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                var img = _capture.Capture();
                if (ReferenceEquals(img, lastSource)) { await Task.Delay(interval, ct).ContinueWith(_ => { }); continue; }
                lastSource = img;
                var (w, h) = Imaging.Fit(img.Width, img.Height, width, height);
                var small = Imaging.Resize(img, w, h);
                var fp = Imaging.Fingerprint(small);
                if (fp != last)
                {
                    last = fp;
                    await session.SendEventAsync("frame", new JsonObject
                    {
                        ["screenWidth"] = img.Width, ["screenHeight"] = img.Height,
                        ["encoding"] = "bgra-lz4",
                    }, new ReadOnlyMemory<byte>[] { Imaging.CompressFrame(small) }, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            catch (Exception ex)
            {
                Log.Write("stream error: " + ex.Message);
                await Task.Delay(500, ct).ContinueWith(_ => { });
            }
            var wait = interval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ContinueWith(_ => { });
        }
    }
}
