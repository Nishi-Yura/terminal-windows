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
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w, o = i * 4; x < w; x++, i++, o += 4)
                idx[i] = Lut[((p[o + 2] >> 3) << 10) | ((p[o + 1] >> 3) << 5) | (p[o] >> 3)];
        });

        // Bands of 6 rows are independent: encode them in parallel, then concatenate in order.
        int bands = (h + 5) / 6;
        var parts = new string[bands];
        Parallel.For(0, bands, () => (Row: new byte[w], Min: new int[256], Max: new int[256]), (band, _, s) =>
        {
            parts[band] = EncodeBand(idx, w, h, band * 6, s.Row, s.Min, s.Max);
            return s;
        }, _ => { });

        var sb = new StringBuilder(parts.Sum(x => x.Length) + PaletteHeader.Length + 64);
        sb.Append("\u001bP0;1;0q\"1;1;").Append(w).Append(';').Append(h);
        sb.Append(PaletteHeader);
        foreach (var part in parts) sb.Append(part);
        sb.Append("\u001b\\");
        return sb.ToString();
    }

    /// <summary>Palette index per pixel (exposed for tests).</summary>
    public static byte[] Quantize(BgraImage img)
    {
        var p = img.Pixels;
        var idx = new byte[img.Width * img.Height];
        for (int i = 0, o = 0; i < idx.Length; i++, o += 4)
            idx[i] = Lut[((p[o + 2] >> 3) << 10) | ((p[o + 1] >> 3) << 5) | (p[o] >> 3)];
        return idx;
    }

    private static string EncodeBand(byte[] idx, int w, int h, int y0, byte[] row, int[] minX, int[] maxX)
    {
        int bandH = Math.Min(6, h - y0);
        Array.Fill(minX, int.MaxValue);
        Array.Fill(maxX, -1);
        for (int y = 0; y < bandH; y++)
        {
            int off = (y0 + y) * w;
            for (int x = 0; x < w; x++)
            {
                int c = idx[off + x];
                if (x < minX[c]) minX[c] = x;
                if (x > maxX[c]) maxX[c] = x;
            }
        }
        var sb = new StringBuilder(w);
        bool firstColor = true;
        for (int c = 0; c < 256; c++)
        {
            if (maxX[c] < 0) continue;
            // Only scan the columns where this colour occurs (most colours cover a small part of a band).
            int x0 = minX[c], x1 = maxX[c];
            for (int x = x0; x <= x1; x++)
            {
                int bits = 0;
                for (int y = 0; y < bandH; y++)
                    if (idx[(y0 + y) * w + x] == c) bits |= 1 << y;
                row[x] = (byte)bits;
            }
            if (!firstColor) sb.Append('$');
            firstColor = false;
            sb.Append('#').Append(c);
            if (x0 > 0) AppendRun(sb, x0, '?'); // skip to the first column (empty sixels)
            int run = 1;
            for (int x = x0 + 1; x <= x1 + 1; x++)
            {
                if (x <= x1 && row[x] == row[x - 1])
                {
                    run++;
                    continue;
                }
                AppendRun(sb, run, (char)(63 + row[x - 1]));
                run = 1;
            }
        }
        sb.Append('-');
        return sb.ToString();
    }

    private static void AppendRun(StringBuilder sb, int run, char ch)
    {
        if (run > 3) sb.Append('!').Append(run).Append(ch);
        else sb.Append(ch, run);
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
