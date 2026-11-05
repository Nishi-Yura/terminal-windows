using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace TerminalWindows.Cli.Setup;

public sealed record WimImage(int Index, string Name, string? EditionId, string? DisplayName, long? SizeBytes);

/// <summary>
/// Reads the list of Windows editions from sources\install.wim (or install.esd) inside a Windows ISO,
/// without mounting it. Consumer ISOs contain several editions; without picking one, unattended
/// setup stops at the edition selection screen.
/// </summary>
public static class IsoInspector
{
    public static IReadOnlyList<WimImage> ReadImages(string isoPath)
    {
        using var fs = File.OpenRead(isoPath);
        DiscFileSystem disc = UdfReader.Detect(fs) ? new UdfReader(fs) : new CDReader(fs, joliet: true);
        try
        {
            var path = new[] { @"sources\install.wim", @"sources\install.esd", @"SOURCES\INSTALL.WIM", @"SOURCES\INSTALL.ESD" }
                .FirstOrDefault(disc.FileExists)
                ?? throw new InvalidDataException("No sources\\install.wim or install.esd in this ISO. Is it a Windows installation ISO?");
            using var wim = disc.OpenFile(path, FileMode.Open, FileAccess.Read);
            return ParseWim(wim);
        }
        finally
        {
            (disc as IDisposable)?.Dispose();
        }
    }

    /// <summary>Parses the WIM header and its (uncompressed, UTF-16) XML metadata resource.</summary>
    public static IReadOnlyList<WimImage> ParseWim(Stream wim)
    {
        var header = new byte[0x60];
        ReadExactly(wim, header);
        if (Encoding.ASCII.GetString(header, 0, 5) != "MSWIM") throw new InvalidDataException("Not a WIM/ESD file");
        // rhXmlData (RESHDR_DISK_SHORT at 0x48): 7-byte size + 1 flag byte, 8-byte offset, 8-byte original size
        long xmlSize = (long)(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x48)) & 0x00FF_FFFF_FFFF_FFFFUL);
        long xmlOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x50));
        if (xmlSize <= 0 || xmlSize > 64 * 1024 * 1024) throw new InvalidDataException("Unexpected WIM XML size");
        wim.Position = xmlOffset;
        var xmlBytes = new byte[xmlSize];
        ReadExactly(wim, xmlBytes);
        return ParseXml(Encoding.Unicode.GetString(xmlBytes).TrimStart('﻿'));
    }

    public static IReadOnlyList<WimImage> ParseXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Root!.Elements("IMAGE").Select(img => new WimImage(
            int.Parse((string)img.Attribute("INDEX")!),
            (string?)img.Element("NAME") ?? "",
            (string?)img.Element("WINDOWS")?.Element("EDITIONID"),
            (string?)img.Element("DISPLAYNAME"),
            long.TryParse((string?)img.Element("TOTALBYTES"), out var b) ? b : null)).ToList();
    }

    /// <summary>
    /// Picks an image: an explicit --edition (matched against EDITIONID, NAME or DISPLAYNAME),
    /// otherwise the only image, otherwise Pro > Enterprise > Education > first.
    /// </summary>
    public static WimImage Choose(IReadOnlyList<WimImage> images, string? edition)
    {
        if (images.Count == 0) throw new InvalidDataException("The ISO lists no Windows images");
        if (!string.IsNullOrWhiteSpace(edition))
        {
            var e = edition.Trim();
            return images.FirstOrDefault(i => string.Equals(i.EditionId, e, StringComparison.OrdinalIgnoreCase))
                   ?? images.FirstOrDefault(i => string.Equals(i.Name, e, StringComparison.OrdinalIgnoreCase))
                   ?? images.FirstOrDefault(i => i.Name.Contains(e, StringComparison.OrdinalIgnoreCase)
                                                 || (i.DisplayName?.Contains(e, StringComparison.OrdinalIgnoreCase) ?? false))
                   ?? throw new InvalidDataException($"No edition matches '{edition}'. Available: " +
                                                     string.Join(", ", images.Select(i => $"{i.Index}:{i.EditionId ?? i.Name}")));
        }
        if (images.Count == 1) return images[0];
        foreach (var preferred in new[] { "Professional", "Enterprise", "Education", "Core" })
        {
            var hit = images.FirstOrDefault(i => string.Equals(i.EditionId, preferred, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return images[0];
    }

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }
    }
}
