using System.Text;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Viewer;

/// <summary>
/// Draws an image with truecolor half blocks ("▀": foreground = upper pixel, background = lower pixel),
/// so every terminal that understands 24-bit colour can show the VM, without Sixel or kitty graphics.
/// One cell is 1x2 image pixels. Only cells that changed since the previous frame are written.
/// </summary>
public sealed class BlockEncoder
{
    private int[] _prev = Array.Empty<int>();
    private int _w, _h;

    /// <summary>Forget the previous frame (after a resize or a full-screen clear).</summary>
    public void Reset() => _prev = Array.Empty<int>();

    public string Encode(BgraImage img)
    {
        int w = img.Width, rows = img.Height / 2;
        if (w != _w || rows != _h || _prev.Length != w * rows * 2)
        {
            _w = w; _h = rows;
            _prev = Enumerable.Repeat(-1, w * rows * 2).ToArray();
        }
        var px = img.Pixels;
        var sb = new StringBuilder(w * rows * 4);
        int curFg = -1, curBg = -1;
        for (int y = 0; y < rows; y++)
        {
            int lastWritten = -2; // column after the last written cell on this row
            for (int x = 0; x < w; x++)
            {
                int top = Rgb(px, (2 * y * w + x) * 4);
                int bottom = Rgb(px, ((2 * y + 1) * w + x) * 4);
                int idx = (y * w + x) * 2;
                if (_prev[idx] == top && _prev[idx + 1] == bottom) continue;
                _prev[idx] = top; _prev[idx + 1] = bottom;
                if (lastWritten != x) sb.Append("\u001b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
                if (top != curFg) { AppendColor(sb, 38, top); curFg = top; }
                if (bottom != curBg) { AppendColor(sb, 48, bottom); curBg = bottom; }
                sb.Append('▀');
                lastWritten = x + 1;
            }
        }
        if (sb.Length > 0) sb.Append("\u001b[0m");
        return sb.ToString();
    }

    private static int Rgb(byte[] p, int i) => i + 2 < p.Length ? (p[i + 2] << 16) | (p[i + 1] << 8) | p[i] : 0;

    private static void AppendColor(StringBuilder sb, int kind, int rgb) =>
        sb.Append("\u001b[").Append(kind).Append(";2;").Append((rgb >> 16) & 255).Append(';').Append((rgb >> 8) & 255).Append(';').Append(rgb & 255).Append('m');
}
