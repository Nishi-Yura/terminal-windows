using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Backend;

/// <summary>
/// A minimal RFB 3.8 (VNC) client: enough to read the VM's screen and press keys before any guest
/// software exists (Windows Setup). VMware Workstation exposes this per VM on 127.0.0.1 only
/// (RemoteDisplay.vnc.*), with a random password. Only the Raw encoding is requested.
/// </summary>
public sealed class VncClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _s;
    public int Width { get; private set; }
    public int Height { get; private set; }

    private VncClient(TcpClient tcp)
    {
        _tcp = tcp;
        _s = tcp.GetStream();
    }

    public static async Task<VncClient> ConnectAsync(int port, string? password, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
            tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            tcp.SendTimeout = (int)timeout.TotalMilliseconds;
            var c = new VncClient(tcp);
            await c.HandshakeAsync(password, cts.Token);
            return c;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task HandshakeAsync(string? password, CancellationToken ct)
    {
        var version = Encoding.ASCII.GetString(await ReadAsync(12, ct));
        if (!version.StartsWith("RFB ")) throw new IOException("Not a VNC server: " + version.Trim());
        await _s.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), ct);

        int n = (await ReadAsync(1, ct))[0];
        if (n == 0) throw new IOException("VNC server refused the connection: " + await ReadReasonAsync(ct));
        var types = await ReadAsync(n, ct);
        if (types.Contains((byte)2) && password is not null)
        {
            await _s.WriteAsync(new byte[] { 2 }, ct);
            var challenge = await ReadAsync(16, ct);
            await _s.WriteAsync(EncryptChallenge(challenge, password), ct);
        }
        else if (types.Contains((byte)1))
        {
            await _s.WriteAsync(new byte[] { 1 }, ct);
        }
        else throw new IOException("No supported VNC security type (offered: " + string.Join(",", types) + ")");
        if (BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, ct)) != 0)
            throw new IOException("VNC authentication failed: " + await ReadReasonAsync(ct));

        await _s.WriteAsync(new byte[] { 1 }, ct); // ClientInit: shared
        var init = await ReadAsync(24, ct);
        Width = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(0));
        Height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
        int nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        await ReadAsync(nameLen, ct);

        // SetPixelFormat: 32 bpp, depth 24, little-endian, true colour, R<<16 G<<8 B -> memory order B,G,R,X.
        var pf = new byte[20];
        pf[0] = 0; pf[4] = 32; pf[5] = 24; pf[6] = 0; pf[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(12), 255);
        pf[14] = 16; pf[15] = 8; pf[16] = 0;
        await _s.WriteAsync(pf, ct);
        // SetEncodings: Raw (0) and the DesktopSize pseudo-encoding (-223).
        var enc = new byte[12];
        enc[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(enc.AsSpan(2), 2);
        BinaryPrimitives.WriteInt32BigEndian(enc.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32BigEndian(enc.AsSpan(8), -223);
        await _s.WriteAsync(enc, ct);
    }

    /// <summary>VNC authentication: DES-encrypt the challenge with the password, each key byte bit-reversed.</summary>
    public static byte[] EncryptChallenge(byte[] challenge, string password)
    {
        var key = new byte[8];
        var pw = Encoding.ASCII.GetBytes(password);
        for (int i = 0; i < 8 && i < pw.Length; i++) key[i] = ReverseBits(pw[i]);
#pragma warning disable SYSLIB0021 // DES is what the RFB protocol specifies.
        using var des = DES.Create();
#pragma warning restore SYSLIB0021
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    private static byte ReverseBits(byte b)
    {
        byte r = 0;
        for (int i = 0; i < 8; i++) { r = (byte)((r << 1) | (b & 1)); b >>= 1; }
        return r;
    }

    /// <summary>Presses and releases one key (an X11 keysym, e.g. 0x20 for Space, 0xff0d for Enter).</summary>
    public async Task KeyAsync(uint keysym)
    {
        foreach (byte down in new byte[] { 1, 0 })
        {
            var m = new byte[8];
            m[0] = 4; m[1] = down;
            BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), keysym);
            await _s.WriteAsync(m);
        }
    }

    /// <summary>Requests the whole screen and returns it as a BGRA image.</summary>
    public async Task<BgraImage> CaptureAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var ct = cts.Token;
        await RequestAsync(ct);
        var pixels = new byte[Width * Height * 4];
        while (true)
        {
            int type = (await ReadAsync(1, ct))[0];
            switch (type)
            {
                case 0: // FramebufferUpdate
                {
                    var h = await ReadAsync(3, ct);
                    int rects = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(1));
                    bool resized = false;
                    for (int i = 0; i < rects; i++)
                    {
                        var r = await ReadAsync(12, ct);
                        int x = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(0)), y = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2));
                        int w = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4)), hh = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(6));
                        int encoding = BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(8));
                        if (encoding == -223) { Width = w; Height = hh; resized = true; continue; }
                        if (encoding != 0) throw new IOException($"Unexpected VNC encoding {encoding}");
                        var data = await ReadAsync(w * hh * 4, ct);
                        if (resized) continue;
                        for (int row = 0; row < hh; row++)
                        {
                            int dy = y + row;
                            if (dy >= Height || x >= Width) continue;
                            int len = Math.Min(w, Width - x) * 4;
                            Buffer.BlockCopy(data, row * w * 4, pixels, (dy * Width + x) * 4, len);
                        }
                    }
                    if (resized)
                    {
                        pixels = new byte[Width * Height * 4];
                        await RequestAsync(ct);
                        continue;
                    }
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    return new BgraImage(Width, Height, pixels);
                }
                case 1: // SetColourMapEntries
                {
                    var h = await ReadAsync(5, ct);
                    await ReadAsync(BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(3)) * 6, ct);
                    break;
                }
                case 2: break; // Bell
                case 3: // ServerCutText
                {
                    var h = await ReadAsync(7, ct);
                    await ReadAsync((int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(3)), ct);
                    break;
                }
                default: throw new IOException($"Unexpected VNC message {type}");
            }
        }
    }

    private async Task RequestAsync(CancellationToken ct)
    {
        var m = new byte[10];
        m[0] = 3; m[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), (ushort)Height);
        await _s.WriteAsync(m, ct);
    }

    private async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        try
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, ct));
            return Encoding.UTF8.GetString(await ReadAsync(Math.Min(len, 4096), ct));
        }
        catch { return "(no reason given)"; }
    }

    private async Task<byte[]> ReadAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        await _s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
