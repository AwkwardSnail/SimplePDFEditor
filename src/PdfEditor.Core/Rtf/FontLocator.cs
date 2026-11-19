using System.Buffers.Binary;
using System.Text;

namespace PdfEditor.Core.Rtf;

/// <summary>Finds installed TrueType font files by family name, bold and italic.</summary>
internal static class FontLocator
{
    private sealed record Face(string Path, bool Bold, bool Italic);

    private static readonly Lazy<Dictionary<string, List<Face>>> Faces = new(Scan);

    public static string? Find(string family, bool bold, bool italic)
    {
        if (!Faces.Value.TryGetValue(family.Trim().ToLowerInvariant(), out var faces)) return null;
        return faces.OrderBy(f => (f.Bold == bold ? 0 : 2) + (f.Italic == italic ? 0 : 1)).First().Path;
    }

    private static IEnumerable<string> FontDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(local, "Microsoft", "Windows", "Fonts"),
            "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".fonts"), Path.Combine(home, ".local", "share", "fonts"),
            "/Library/Fonts", "/System/Library/Fonts", Path.Combine(home, "Library", "Fonts"),
        }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct();
    }

    private static Dictionary<string, List<Face>> Scan()
    {
        var result = new Dictionary<string, List<Face>>();
        foreach (var dir in FontDirectories())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; }
            foreach (var file in files)
            {
                if (!file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Read(file) is not { } info) continue;
                    if (!result.TryGetValue(info.Family.ToLowerInvariant(), out var list)) result[info.Family.ToLowerInvariant()] = list = [];
                    list.Add(new Face(file, info.Bold, info.Italic));
                }
                catch (Exception) { /* unreadable font: ignore */ }
            }
        }
        return result;
    }

    /// <summary>Family name and style of a TrueType font (not CFF-based OpenType, which PDFium cannot embed this way).</summary>
    private static (string Family, bool Bold, bool Italic)? Read(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[12];
        if (stream.Read(header) != 12) return null;
        uint version = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (version != 0x00010000 && version != 0x74727565) return null; // TrueType outlines only
        int tables = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var directory = new byte[tables * 16];
        if (stream.Read(directory) != directory.Length) return null;

        (uint Offset, uint Length)? name = null, os2 = null;
        for (int i = 0; i < tables; i++)
        {
            var entry = directory.AsSpan(i * 16, 16);
            string tag = Encoding.ASCII.GetString(entry[..4]);
            var location = (BinaryPrimitives.ReadUInt32BigEndian(entry[8..]), BinaryPrimitives.ReadUInt32BigEndian(entry[12..]));
            if (tag == "name") name = location;
            else if (tag == "OS/2") os2 = location;
        }
        if (name is null) return null;

        string? family = ReadFamily(ReadTable(stream, name.Value));
        if (family is null) return null;
        bool bold = false, italic = false;
        if (os2 is { } o && ReadTable(stream, o) is { Length: >= 64 } os2Data)
        {
            ushort selection = BinaryPrimitives.ReadUInt16BigEndian(os2Data.AsSpan(62));
            italic = (selection & 1) != 0;
            bold = (selection & 0x20) != 0;
        }
        return (family, bold, italic);
    }

    private static byte[] ReadTable(Stream stream, (uint Offset, uint Length) table)
    {
        var data = new byte[Math.Min(table.Length, 1 << 20)];
        stream.Seek(table.Offset, SeekOrigin.Begin);
        int read = 0;
        while (read < data.Length)
        {
            int n = stream.Read(data, read, data.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return data;
    }

    /// <summary>The font family name (name ID 1), preferring the Windows English entry.</summary>
    private static string? ReadFamily(byte[] table)
    {
        if (table.Length < 6) return null;
        int count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        int strings = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4));
        string? best = null;
        int bestScore = -1;
        for (int i = 0; i < count; i++)
        {
            int at = 6 + i * 12;
            if (at + 12 > table.Length) break;
            var r = table.AsSpan(at, 12);
            int platform = BinaryPrimitives.ReadUInt16BigEndian(r);
            int language = BinaryPrimitives.ReadUInt16BigEndian(r[4..]);
            int nameId = BinaryPrimitives.ReadUInt16BigEndian(r[6..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(r[8..]);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(r[10..]);
            if (nameId != 1 || strings + offset + length > table.Length) continue;
            int score = platform == 3 ? (language == 0x409 ? 3 : 2) : platform == 1 ? 1 : 0;
            if (score <= bestScore) continue;
            var bytes = table.AsSpan(strings + offset, length);
            best = platform is 3 or 0 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            bestScore = score;
        }
        return best;
    }
}
