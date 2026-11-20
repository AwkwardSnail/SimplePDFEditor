using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PdfEditor.Core;

/// <summary>What was written to an RTF file, and what was left out.</summary>
/// <param name="Paragraphs">Paragraphs of text written.</param>
/// <param name="Pictures">Pictures written.</param>
/// <param name="DrawingsLeftOut">Drawn shapes (lines, boxes, figures) on the pages, which RTF text cannot hold.</param>
/// <param name="NotesLeftOut">Comments and highlights, which are not written.</param>
public sealed record RtfExportResult(int Paragraphs, int Pictures, int DrawingsLeftOut, int NotesLeftOut);

// Writing the document's text and pictures as RTF, for a word processor.
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Writes the document as RTF: its paragraphs with their fonts, sizes, bold, italic, colour and
    /// alignment, its pictures where they come in the text, and a new page where each page ends.
    /// A PDF fixes where everything stands and RTF lets text run on, so exact places, columns,
    /// drawn shapes, comments and highlights are not kept.
    /// </summary>
    public byte[] ExportRtf(out RtfExportResult result)
    {
        var fonts = new List<(string Name, string Family)>();
        var colours = new List<PdfColor>();
        int FontIndex(string pdfName)
        {
            var font = WordProcessorFont(pdfName);
            int at = fonts.IndexOf(font);
            if (at < 0) { fonts.Add(font); at = fonts.Count - 1; }
            return at;
        }
        int ColourIndex(PdfColor c)
        {
            var opaque = c with { A = 255 };
            int at = colours.IndexOf(opaque);
            if (at < 0) { colours.Add(opaque); at = colours.Count - 1; }
            return at + 1; // 0 is the word processor's own colour
        }
        static int Twips(double points) => (int)Math.Round(points * 20);

        // Every page first: the margins are those of the text as it stands in the document.
        int pages = PageCount;
        var all = new List<(PdfSize Size, IReadOnlyList<TextParagraph> Text, IReadOnlyList<ImageInfo> Pictures)>();
        double left = double.MaxValue, right = double.MaxValue, top = double.MaxValue;
        int drawings = 0, notes = 0;
        for (int p = 0; p < pages; p++)
        {
            var size = GetPageSize(p);
            var text = GetParagraphs(p);
            var pictures = GetImages(p);
            all.Add((size, text, pictures));
            foreach (var t in text)
            {
                left = Math.Min(left, t.Bounds.X);
                right = Math.Min(right, size.Width - t.Bounds.Right);
                top = Math.Min(top, t.Bounds.Y);
            }
            drawings += GetDrawings(p).Count;
            notes += GetAnnotations(p).Count(a => a.Kind != AnnotationKind.Other); // comments and highlights, not links
        }
        left = left == double.MaxValue ? 72 : Math.Clamp(left, 18, 144);
        right = right == double.MaxValue ? 72 : Math.Clamp(right, 18, 144);
        top = top == double.MaxValue ? 72 : Math.Clamp(top, 18, 108);

        var body = new StringBuilder();
        int paragraphs = 0, pictureCount = 0;
        for (int p = 0; p < pages; p++)
        {
            var (size, text, pictures) = all[p];
            if (p > 0)
            {
                // A page of another size starts a section of its own; otherwise just a new page.
                var before = all[p - 1].Size;
                if (Math.Abs(before.Width - size.Width) > 1 || Math.Abs(before.Height - size.Height) > 1)
                    body.Append("\\sect\\sectd\\sbkpage\\pgwsxn").Append(Twips(size.Width)).Append("\\pghsxn").Append(Twips(size.Height))
                        .Append(size.Width > size.Height ? "\\lndscpsxn" : "").Append('\n');
                else body.Append("\\page\n");
            }

            // Text in the order it reads; each picture before the first paragraph that starts below its top.
            var waiting = pictures.OrderBy(i => i.Bounds.Y).ThenBy(i => i.Bounds.X).ToList();
            double previousBottom = top;
            void Picture(ImageInfo image)
            {
                try
                {
                    var pixels = GetImagePixels(p, image.ObjectIndex, asDisplayed: true);
                    if (pixels.Width <= 0 || pixels.Height <= 0) return;
                    double width = Math.Min(image.Bounds.Width, size.Width - left - right), height = image.Bounds.Height * width / Math.Max(1, image.Bounds.Width);
                    body.Append("\\pard\\ql");
                    double indent = Math.Min(image.Bounds.X - left, size.Width - left - right - width);
                    if (indent > 3) body.Append("\\li").Append(Twips(indent));
                    body.Append(" {\\pict\\pngblip\\picw").Append(pixels.Width).Append("\\pich").Append(pixels.Height)
                        .Append("\\picwgoal").Append(Twips(width)).Append("\\pichgoal").Append(Twips(height)).Append('\n');
                    byte[] png = Png(pixels);
                    const string hex = "0123456789abcdef";
                    for (int i = 0; i < png.Length; i++)
                    {
                        body.Append(hex[png[i] >> 4]).Append(hex[png[i] & 15]);
                        if (i % 64 == 63) body.Append('\n');
                    }
                    body.Append("}\\par\n");
                    pictureCount++;
                }
                catch (PdfException) { } // a picture that cannot be read is left out
            }

            foreach (var t in text)
            {
                while (waiting.Count > 0 && waiting[0].Bounds.Y <= t.Bounds.Y + 1)
                {
                    Picture(waiting[0]);
                    previousBottom = Math.Max(previousBottom, waiting[0].Bounds.Bottom);
                    waiting.RemoveAt(0);
                }
                if (t.Text.Trim().Length == 0) continue;

                body.Append("\\pard").Append(t.Alignment switch { TextAlignment.Center => "\\qc", TextAlignment.Right => "\\qr", TextAlignment.Justify => "\\qj", _ => "\\ql" });
                double indentBy = t.Bounds.X - left;
                if (t.Alignment is TextAlignment.Left or TextAlignment.Justify && indentBy > 3) body.Append("\\li").Append(Twips(indentBy));
                double gap = t.Bounds.Y - previousBottom;
                if (gap > 2) body.Append("\\sb").Append(Twips(Math.Min(gap, 48)));
                body.Append(' ');

                // Runs of text that look the same are written as one, however many pieces the page drew them in.
                var spans = new List<TextSpan>();
                foreach (var s in t.Spans.Count > 0 ? t.Spans : [new TextSpan(0, t.Text.Length, "", t.FontSize, PdfColor.Black, false, false)])
                {
                    if (spans.Count > 0 && spans[^1] is var last && last.Start + last.Length == s.Start && WordProcessorFont(last.FontName) == WordProcessorFont(s.FontName)
                        && Math.Round(last.FontSize * 2) == Math.Round(s.FontSize * 2) && last.Color == s.Color && last.Bold == s.Bold && last.Italic == s.Italic)
                        spans[^1] = last with { Length = last.Length + s.Length };
                    else spans.Add(s);
                }
                foreach (var span in spans)
                {
                    int start = Math.Clamp(span.Start, 0, t.Text.Length), length = Math.Clamp(span.Length, 0, t.Text.Length - start);
                    if (length == 0) continue;
                    body.Append("{\\f").Append(FontIndex(span.FontName)).Append("\\fs").Append((int)Math.Round(Math.Max(1, span.FontSize) * 2));
                    if (span.Bold) body.Append("\\b");
                    if (span.Italic) body.Append("\\i");
                    if (span.Color is not { R: 0, G: 0, B: 0 }) body.Append("\\cf").Append(ColourIndex(span.Color));
                    body.Append(' ');
                    foreach (char ch in t.Text.AsSpan(start, length))
                    {
                        if (ch is '\\' or '{' or '}') body.Append('\\').Append(ch);
                        else if (ch == '\n') body.Append("\\line ");
                        else if (ch == '\t') body.Append("\\tab ");
                        else if (ch == '\u00A0') body.Append("\\~");
                        else if (ch is >= ' ' and < (char)127) body.Append(ch);
                        else if (ch >= ' ') body.Append("\\u").Append(((short)ch).ToString(CultureInfo.InvariantCulture)).Append('?');
                    }
                    body.Append('}');
                }
                body.Append("\\par\n");
                paragraphs++;
                previousBottom = t.Bounds.Bottom;
            }
            foreach (var image in waiting) Picture(image);
        }

        var first = pages > 0 ? all[0].Size : new PdfSize(595.28, 841.89);
        var rtf = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1\n{\\fonttbl");
        if (fonts.Count == 0) fonts.Add(("Arial", "swiss"));
        for (int i = 0; i < fonts.Count; i++) rtf.Append("{\\f").Append(i).Append("\\f").Append(fonts[i].Family).Append(' ').Append(fonts[i].Name).Append(";}");
        rtf.Append("}\n{\\colortbl;");
        foreach (var c in colours) rtf.Append("\\red").Append(c.R).Append("\\green").Append(c.G).Append("\\blue").Append(c.B).Append(';');
        rtf.Append("}\n\\paperw").Append(Twips(first.Width)).Append("\\paperh").Append(Twips(first.Height)).Append(first.Width > first.Height ? "\\landscape" : "")
           .Append("\\margl").Append(Twips(left)).Append("\\margr").Append(Twips(right)).Append("\\margt").Append(Twips(top)).Append("\\margb").Append(Twips(Math.Min(top, 54)))
           .Append("\\widowctrl\n").Append(body).Append("}\n");

        result = new RtfExportResult(paragraphs, pictureCount, drawings, notes);
        return Encoding.ASCII.GetBytes(rtf.ToString());
    }

    /// <summary>
    /// The font a word processor should use for a font named in a PDF: the name as people know it
    /// (without the cut-down prefix and the weight), and the kind of font to fall back on when it
    /// is not installed.
    /// </summary>
    private static (string Name, string Family) WordProcessorFont(string pdfName)
    {
        string name = pdfName;
        int plus = name.IndexOf('+');
        if (plus is > 0 and <= 7) name = name[(plus + 1)..];
        int cut = name.IndexOfAny(['-', ',']);
        if (cut > 0) name = name[..cut];
        foreach (string tail in new[] { "PSMT", "MT", "PS", "BoldItalic", "BoldOblique", "Bold", "Italic", "Oblique", "Regular", "Roman" })
            if (name.Length > tail.Length + 2 && name.EndsWith(tail, StringComparison.Ordinal)) name = name[..^tail.Length];
        // "TimesNewRoman" reads "Times New Roman".
        var spaced = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) spaced.Append(' ');
            if (name[i] is >= ' ' and < (char)127 and not ('\\' or '{' or '}' or ';')) spaced.Append(name[i]);
        }
        name = spaced.ToString().Trim();
        string lower = pdfName.ToLowerInvariant();
        string family = lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consol") ? "modern"
            : lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("roman") || lower.Contains("georgia") || lower.Contains("garamond") || lower.Contains("minion") || lower.Contains("cambria") || lower.Contains("book") ? "roman"
            : "swiss";
        if (name.Length == 0 || name is "Helvetica" or "Helvetica Neue") name = family switch { "modern" => "Courier New", "roman" => "Times New Roman", _ => "Arial" };
        else if (name == "Times") name = "Times New Roman";
        else if (name == "Courier") name = "Courier New";
        return (name, family);
    }

    /// <summary>A picture's pixels (blue, green, red, see-through; rows top to bottom) as a PNG file.</summary>
    private static byte[] Png(RenderedPage picture)
    {
        // Each row: a nought (no filter), then red, green, blue and see-through for every pixel.
        var rows = new byte[picture.Height * (1 + picture.Width * 4)];
        int at = 0;
        for (int y = 0; y < picture.Height; y++)
        {
            rows[at++] = 0;
            int from = y * picture.Stride;
            for (int x = 0; x < picture.Width; x++, from += 4)
            {
                rows[at++] = picture.Pixels[from + 2];
                rows[at++] = picture.Pixels[from + 1];
                rows[at++] = picture.Pixels[from];
                rows[at++] = picture.Pixels[from + 3];
            }
        }
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rows);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> four = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(four, (uint)data.Length);
            png.Write(four);
            byte[] name = Encoding.ASCII.GetBytes(type);
            png.Write(name);
            png.Write(data);
            uint crc = 0xFFFFFFFF; // the check PNG asks for, over the name and the data
            foreach (byte b in name.Concat(data))
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            crc ^= 0xFFFFFFFF;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(four, crc);
            png.Write(four);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)picture.Width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)picture.Height);
        header[8] = 8; // bits for each of red, green, blue and see-through
        header[9] = 6; // colour with see-through
        Chunk("IHDR", header);
        Chunk("IDAT", packed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }
}
