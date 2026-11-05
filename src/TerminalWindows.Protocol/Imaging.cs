using System.Buffers.Binary;
using System.IO.Compression;
using K4os.Compression.LZ4;

namespace TerminalWindows.Protocol;

/// <summary>A tightly packed 32-bit BGRA image (stride = width * 4).</summary>
public sealed class BgraImage
{
    public BgraImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length != width * height * 4) throw new ArgumentException("Pixel buffer size mismatch");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

public static class Imaging
{
    /// <summary>Area-averaging downscale (or nearest-neighbour upscale). Fast enough for 30fps at viewer sizes.</summary>
    public static BgraImage Resize(BgraImage src, int width, int height)
    {
        if (width == src.Width && height == src.Height) return src;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var dst = new byte[width * height * 4];
        var s = src.Pixels;
        int sw = src.Width, sh = src.Height;

        if (width >= sw || height >= sh)
        {
            for (int y = 0; y < height; y++)
            {
                int sy = Math.Min(sh - 1, y * sh / height);
                for (int x = 0; x < width; x++)
                {
                    int sx = Math.Min(sw - 1, x * sw / width);
                    Buffer.BlockCopy(s, (sy * sw + sx) * 4, dst, (y * width + x) * 4, 4);
                }
            }
            return new BgraImage(width, height, dst);
        }

        Parallel.For(0, height, y =>
        {
            int y0 = y * sh / height, y1 = Math.Max(y0 + 1, (y + 1) * sh / height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * sw / width, x1 = Math.Max(x0 + 1, (x + 1) * sw / width);
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * sw * 4;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = row + xx * 4;
                        b += s[i]; g += s[i + 1]; r += s[i + 2]; a += s[i + 3];
                        n++;
                    }
                }
                int o = (y * width + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
            }
        });
        return new BgraImage(width, height, dst);
    }

    /// <summary>Fits (w, h) inside (maxW, maxH) keeping aspect ratio; never upscales.</summary>
    public static (int W, int H) Fit(int w, int h, int maxW, int maxH)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / h));
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    /// <summary>Encodes an opaque RGB PNG (alpha dropped: the desktop is always opaque).</summary>
    public static byte[] EncodePng(BgraImage img)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type RGB
        WriteChunk(ms, "IHDR", ihdr);

        int rowBytes = img.Width * 3;
        var raw = new byte[(rowBytes + 1) * img.Height];
        var p = img.Pixels;
        Parallel.For(0, img.Height, y =>
        {
            int o = y * (rowBytes + 1);
            raw[o] = 1; // filter: Sub
            int src = y * img.Width * 4;
            byte pr = 0, pg = 0, pb = 0;
            for (int x = 0; x < img.Width; x++)
            {
                byte b = p[src], g = p[src + 1], r = p[src + 2];
                raw[o + 1 + x * 3] = (byte)(r - pr);
                raw[o + 2 + x * 3] = (byte)(g - pg);
                raw[o + 3 + x * 3] = (byte)(b - pb);
                pr = r; pg = g; pb = b;
                src += 4;
            }
        });

        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
            WriteChunk(ms, "IDAT", z.ToArray());
        }
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>LZ4 block compression with an 8-byte header: i32 width, i32 height.</summary>
    public static byte[] CompressFrame(BgraImage img)
    {
        var target = new byte[8 + LZ4Codec.MaximumOutputSize(img.Pixels.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(0), img.Width);
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(4), img.Height);
        int n = LZ4Codec.Encode(img.Pixels, target.AsSpan(8), LZ4Level.L00_FAST);
        return target.AsSpan(0, 8 + n).ToArray();
    }

    public static BgraImage DecompressFrame(ReadOnlySpan<byte> data, int maxPixels = 16384 * 16384)
    {
        if (data.Length < 8) throw new ProtocolException("Frame too short");
        int w = BinaryPrimitives.ReadInt32LittleEndian(data);
        int h = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (w <= 0 || h <= 0 || (long)w * h > maxPixels) throw new ProtocolException("Invalid frame size");
        var pixels = new byte[w * h * 4];
        int n = LZ4Codec.Decode(data[8..], pixels);
        if (n != pixels.Length) throw new ProtocolException("Corrupt frame");
        return new BgraImage(w, h, pixels);
    }

    /// <summary>Change-detection fingerprint over every pixel (XxHash3 runs at several GB/s).</summary>
    public static ulong Fingerprint(BgraImage img) => System.IO.Hashing.XxHash3.HashToUInt64(img.Pixels);
}
