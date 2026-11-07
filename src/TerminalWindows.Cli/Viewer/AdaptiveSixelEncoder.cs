using System.Text;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Viewer;

/// <summary>
/// Sixel for a live screen: a palette made from the frame's own colours (median cut over a 15-bit
/// histogram, up to 256 entries) instead of a fixed colour cube, so window colours, text anti-aliasing
/// and photos keep their real shades. The palette is kept across frames while it still fits (no colour
/// flicker on a static desktop) and rebuilt when new colours would be shown badly.
/// </summary>
public sealed class AdaptiveSixelEncoder
{
    private const int Bins = 32768;
    private readonly short[] _lut = new short[Bins]; // 15-bit colour -> palette index, -1 = not assigned yet
    private byte[][] _palette = Array.Empty<byte[]>(); // RGB per entry
    private string _header = "";
    private readonly int[] _hist = new int[Bins];
    private readonly long[] _sumR = new long[Bins], _sumG = new long[Bins], _sumB = new long[Bins];

    /// <summary>How many times the palette was built (tests: a static screen must not rebuild every frame).</summary>
    public int Rebuilds { get; private set; }

    public AdaptiveSixelEncoder() => Array.Fill(_lut, (short)-1);

    private static int Key(byte[] p, int o) => ((p[o + 2] >> 3) << 10) | ((p[o + 1] >> 3) << 5) | (p[o] >> 3);

    public string Encode(BgraImage img) => SixelEncoder.EncodeIndexed(Quantize(img), img.Width, img.Height, _header);

    /// <summary>Palette index per pixel, updating the palette as needed.</summary>
    public byte[] Quantize(BgraImage img)
    {
        int n = img.Width * img.Height;
        var p = img.Pixels;
        Array.Clear(_hist);
        Array.Clear(_sumR);
        Array.Clear(_sumG);
        Array.Clear(_sumB);
        for (int i = 0, o = 0; i < n; i++, o += 4)
        {
            int k = Key(p, o);
            _hist[k]++;
            _sumR[k] += p[o + 2];
            _sumG[k] += p[o + 1];
            _sumB[k] += p[o];
        }

        if (_palette.Length == 0) Rebuild();
        else
        {
            // Keep the palette if the colours it has not seen yet are few or close to an existing entry.
            long badPixels = 0;
            for (int k = 0; k < Bins; k++)
            {
                if (_hist[k] == 0 || _lut[k] >= 0) continue;
                var (best, dist) = Nearest(k);
                if (dist > 12 * 12 * 3) badPixels += _hist[k];
                _lut[k] = (short)best;
            }
            if (badPixels * 200 > n) Rebuild(); // more than 0.5% of the picture would be visibly off
        }

        var idx = new byte[n];
        int w = img.Width;
        Parallel.For(0, img.Height, y =>
        {
            for (int x = 0, i = y * w, o = i * 4; x < w; x++, i++, o += 4)
                idx[i] = (byte)_lut[Key(p, o)];
        });
        return idx;
    }

    /// <summary>The RGB colour of a palette entry.</summary>
    public byte[] PaletteColor(int index) => _palette[index];

    public int PaletteSize => _palette.Length;

    private (int Index, int Dist) Nearest(int k)
    {
        int r = (int)(_sumR[k] / _hist[k]), g = (int)(_sumG[k] / _hist[k]), b = (int)(_sumB[k] / _hist[k]);
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < _palette.Length; i++)
        {
            var c = _palette[i];
            int dr = c[0] - r, dg = c[1] - g, db = c[2] - b, d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (best, bestD);
    }

    private void Rebuild()
    {
        Rebuilds++;
        var keys = new List<int>();
        for (int k = 0; k < Bins; k++) if (_hist[k] > 0) keys.Add(k);
        var boxes = new List<List<int>> { keys };
        // Median cut: split the box with the best (channel range x sqrt(pixels)), at its pixel-weighted median.
        while (boxes.Count < 256)
        {
            int pick = -1, axis = 0;
            double score = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var bx = boxes[i];
                if (bx.Count < 2) continue;
                var (ax, range) = WidestAxis(bx);
                if (range == 0) continue;
                long pixels = 0;
                foreach (var k in bx) pixels += _hist[k];
                double sc = range * Math.Sqrt(pixels);
                if (sc > score) { score = sc; pick = i; axis = ax; }
            }
            if (pick < 0) break; // every box is a single colour: the palette is exact
            var box = boxes[pick];
            int shift = axis == 0 ? 10 : axis == 1 ? 5 : 0;
            box.Sort((a, b) => ((a >> shift) & 31).CompareTo((b >> shift) & 31));
            long total = 0, acc = 0;
            foreach (var k in box) total += _hist[k];
            int cut = 1;
            for (int i = 0; i < box.Count - 1; i++)
            {
                acc += _hist[box[i]];
                if (acc * 2 >= total) { cut = i + 1; break; }
            }
            // Cut between two different channel values (the axis has range > 0, so one exists).
            while (cut < box.Count && ((box[cut] >> shift) & 31) == ((box[cut - 1] >> shift) & 31)) cut++;
            if (cut >= box.Count)
            {
                cut = 1;
                while (((box[cut] >> shift) & 31) == ((box[0] >> shift) & 31)) cut++;
            }
            boxes[pick] = box.GetRange(0, cut);
            boxes.Add(box.GetRange(cut, box.Count - cut));
        }

        Array.Fill(_lut, (short)-1);
        _palette = new byte[boxes.Count][];
        var sb = new StringBuilder(boxes.Count * 16);
        for (int i = 0; i < boxes.Count; i++)
        {
            long r = 0, g = 0, b = 0, c = 0;
            foreach (var k in boxes[i])
            {
                r += _sumR[k]; g += _sumG[k]; b += _sumB[k]; c += _hist[k];
                _lut[k] = (short)i;
            }
            c = Math.Max(1, c);
            var rgb = new[] { (byte)(r / c), (byte)(g / c), (byte)(b / c) };
            _palette[i] = rgb;
            sb.Append('#').Append(i).Append(";2;")
              .Append((int)Math.Round(rgb[0] * 100 / 255.0)).Append(';')
              .Append((int)Math.Round(rgb[1] * 100 / 255.0)).Append(';')
              .Append((int)Math.Round(rgb[2] * 100 / 255.0));
        }
        _header = sb.ToString();
    }

    private static (int Axis, int Range) WidestAxis(List<int> box)
    {
        int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
        foreach (var k in box)
        {
            int r = (k >> 10) & 31, g = (k >> 5) & 31, b = k & 31;
            if (r < rMin) rMin = r;
            if (r > rMax) rMax = r;
            if (g < gMin) gMin = g;
            if (g > gMax) gMax = g;
            if (b < bMin) bMin = b;
            if (b > bMax) bMax = b;
        }
        // Weighted by how sensitive the eye is to each channel (green most, blue least).
        int dr = (rMax - rMin) * 4, dg = (gMax - gMin) * 5, db = (bMax - bMin) * 3;
        return dr >= dg && dr >= db ? (0, dr) : dg >= db ? (1, dg) : (2, db);
    }
}
