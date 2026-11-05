using System.Text;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Viewer;

/// <summary>Sixel encoder with a fixed 6x7x6 colour cube (252 colours) and a 15-bit lookup table.</summary>
public static class SixelEncoder
{
    private const int RLevels = 6, GLevels = 7, BLevels = 6;
    private static readonly byte[] Lut = BuildLut();
    private static readonly string PaletteHeader = BuildPalette();

    private static byte[] BuildLut()
    {
        var lut = new byte[32768];
        for (int i = 0; i < lut.Length; i++)
        {
            int r = (i >> 10) & 31, g = (i >> 5) & 31, b = i & 31;
            int ri = (r * (RLevels - 1) + 15) / 31, gi = (g * (GLevels - 1) + 15) / 31, bi = (b * (BLevels - 1) + 15) / 31;
            lut[i] = (byte)((ri * GLevels + gi) * BLevels + bi);
        }
        return lut;
    }

    private static string BuildPalette()
    {
        var sb = new StringBuilder();
        for (int ri = 0; ri < RLevels; ri++)
        for (int gi = 0; gi < GLevels; gi++)
        for (int bi = 0; bi < BLevels; bi++)
        {
            int idx = (ri * GLevels + gi) * BLevels + bi;
            sb.Append('#').Append(idx).Append(";2;")
              .Append(ri * 100 / (RLevels - 1)).Append(';')
              .Append(gi * 100 / (GLevels - 1)).Append(';')
              .Append(bi * 100 / (BLevels - 1));
        }
        return sb.ToString();
    }

    public static string Encode(BgraImage img)
    {
        int w = img.Width, h = img.Height;
        var p = img.Pixels;
        var idx = new byte[w * h];
        for (int i = 0, o = 0; i < idx.Length; i++, o += 4)
            idx[i] = Lut[((p[o + 2] >> 3) << 10) | ((p[o + 1] >> 3) << 5) | (p[o] >> 3)];

        var sb = new StringBuilder(w * h / 2);
        sb.Append("\u001bP0;1;0q\"1;1;").Append(w).Append(';').Append(h);
        sb.Append(PaletteHeader);

        var used = new bool[256];
        var row = new byte[w];
        for (int y0 = 0; y0 < h; y0 += 6)
        {
            int bandH = Math.Min(6, h - y0);
            Array.Clear(used);
            for (int y = 0; y < bandH; y++)
            {
                int off = (y0 + y) * w;
                for (int x = 0; x < w; x++) used[idx[off + x]] = true;
            }
            bool firstColor = true;
            for (int c = 0; c < 256; c++)
            {
                if (!used[c]) continue;
                for (int x = 0; x < w; x++)
                {
                    int bits = 0;
                    for (int y = 0; y < bandH; y++)
                        if (idx[(y0 + y) * w + x] == c) bits |= 1 << y;
                    row[x] = (byte)bits;
                }
                if (!firstColor) sb.Append('$');
                firstColor = false;
                sb.Append('#').Append(c);
                // Run-length encode
                int run = 1;
                for (int x = 1; x <= w; x++)
                {
                    if (x < w && row[x] == row[x - 1])
                    {
                        run++;
                        continue;
                    }
                    char ch = (char)(63 + row[x - 1]);
                    if (run > 3) sb.Append('!').Append(run).Append(ch);
                    else sb.Append(ch, run);
                    run = 1;
                }
            }
            sb.Append('-');
        }
        sb.Append("\u001b\\");
        return sb.ToString();
    }
}

/// <summary>kitty graphics protocol: PNG payload, chunked base64, sized in cells, replaced in place (id 1).</summary>
public static class KittyEncoder
{
    public static string Encode(BgraImage img, int cols, int rows)
    {
        var b64 = Convert.ToBase64String(Imaging.EncodePng(img));
        var sb = new StringBuilder(b64.Length + 256);
        const int chunk = 4096;
        for (int i = 0; i < b64.Length; i += chunk)
        {
            bool first = i == 0;
            bool more = i + chunk < b64.Length;
            sb.Append("\u001b_G");
            if (first) sb.Append($"a=T,f=100,q=2,i=1,p=1,C=1,c={cols},r={rows},");
            sb.Append("m=").Append(more ? 1 : 0).Append(';');
            sb.Append(b64, i, Math.Min(chunk, b64.Length - i));
            sb.Append("\u001b\\");
        }
        return sb.ToString();
    }
}
