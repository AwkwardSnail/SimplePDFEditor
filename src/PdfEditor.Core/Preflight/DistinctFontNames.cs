using System.Globalization;
using System.IO.Compression;
using System.Text;

using static PdfEditor.Core.Preflight.PdfFile;

namespace PdfEditor.Core.Preflight;

/// <summary>
/// Gives every embedded font in a PDF a name of its own, before PDFium sees the file.
///
/// A PDF may hold several different embedded fonts with one name, told apart only by a
/// six-letter prefix ("AAAAAB+Helvetica", "AAAAAF+Helvetica": separate cuts of a font, each
/// with its own letters). PDFium drops that prefix on loading and, when it writes a page,
/// tells fonts apart by name alone, so text in one such font would be drawn with another.
/// PDFium has no way to rename a font, so this is done on the file itself: the fonts that
/// share a name are found, and renamed copies of their entries are appended to the file as
/// an ordinary PDF update ("Helvetica-2"). Nothing else in the file is touched, and the
/// fonts' own data stays as it is, so pages look exactly the same.
///
/// The work is best effort. A file this reader cannot follow (password protected, damaged,
/// or built in an unusual way) is returned unchanged, and the editor falls back to redrawing
/// clashing text as shapes when a page is rewritten.
/// </summary>
internal static class DistinctFontNames
{
    /// <summary>The file with clashing fonts renamed, or the same array when there is nothing to do.</summary>
    public static byte[] Apply(byte[] pdf)
    {
        try { return new Pass(pdf).Run(); }
        catch (Exception e) when (e is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentException
                                      or OverflowException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return pdf;
        }
    }

    // ------------------------------------------------------------------ the pass over one file

    private sealed record Font(int Number, Loaded Object, Node NameNode, string Name, string Key, bool Embedded, string Signature);

    private sealed class Pass(byte[] pdf)
    {
        private readonly PdfFile _file = new(pdf);
        private Dictionary<int, Entry> _entries => _file.Entries;
        private Dictionary<int, Loaded> _objects => _file.Objects;
        private Node? _trailer => _file.Trailer;
        private bool _trailerIsStream => _file.TrailerIsStream;
        private int _lastXref => _file.LastXref;
        private void ReadCrossReferences() => _file.ReadCrossReferences();
        private void LoadObjects() => _file.LoadObjects();
        private byte[]? StreamData(Node dict, byte[] source, int start) => _file.StreamData(dict, source, start);
        private (Node? Value, byte[]? Source) Resolve(Node? node, byte[] source) => _file.Resolve(node, source);
        private static string Raw(Node? node, byte[]? source) => PdfFile.Raw(node, source);
        private static int IndexOf(byte[] data, ReadOnlySpan<byte> word, int from) => PdfFile.IndexOf(data, word, from);

        public byte[] Run()
        {
            if (pdf.Length < 32 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F') return pdf;
            ReadCrossReferences();
            if (_trailer is null || _trailer["Encrypt"] is not null || _trailer["Root"] is null) return pdf;
            LoadObjects();
            var renames = ChooseNames(FindFonts());
            return renames.Count == 0 ? pdf : Append(renames);
        }

        // ---- the fonts

        /// <summary>A font's name without the six-letter prefix of a partial font, which is how PDFium sees it.</summary>
        private static string WithoutPrefix(string name) => name.Length > 7 && name[6] == '+' ? name[7..] : name;

        private List<Font> FindFonts()
        {
            var fonts = new List<Font>();
            foreach (var (number, loaded) in _objects.OrderBy(o => o.Key))
            {
                var dict = loaded.Value;
                if (dict.Kind != Kind.Dict || dict["BaseFont"] is not { Kind: Kind.Name } name) continue;
                if (dict["Type"] is { } type && type.Name != "Font") continue;
                string kind = dict["Subtype"]?.Name switch
                {
                    "Type1" or "MMType1" => "Type1",
                    "TrueType" => "TrueType",
                    "Type0" => "Type0",
                    _ => "",
                };
                if (kind.Length == 0) continue; // among others, the inner half of a composite font

                // The font's own data, if the file carries it. A composite font keeps it one level down.
                Node? holder = dict;
                byte[]? holderSource = loaded.Source;
                if (kind == "Type0")
                {
                    var (descendants, source) = Resolve(dict["DescendantFonts"], loaded.Source);
                    (holder, holderSource) = descendants is { Kind: Kind.Array, Items.Count: > 0 } && source is not null
                        ? Resolve(descendants.Items[0], source) : (null, null);
                }
                Node? file = null;
                if (holder is not null && holderSource is not null)
                {
                    var (descriptor, _) = Resolve(holder["FontDescriptor"], holderSource);
                    file = descriptor?["FontFile"] ?? descriptor?["FontFile2"] ?? descriptor?["FontFile3"];
                }
                string fileKey = file is { Kind: Kind.Ref } ? file.RefNumber.ToString(CultureInfo.InvariantCulture) : file is null ? "" : "own" + number;
                string signature = $"{fileKey}|{Raw(dict["Encoding"], loaded.Source)}|{Raw(dict["ToUnicode"], loaded.Source)}";
                fonts.Add(new Font(number, loaded, name, name.Name, WithoutPrefix(name.Name) + "|" + kind, file is not null, signature));
            }
            return fonts;
        }

        /// <summary>Which fonts get a new name, and the text to add to their present one.</summary>
        private static List<(Font Font, string Suffix)> ChooseNames(List<Font> fonts)
        {
            var taken = fonts.Select(f => WithoutPrefix(f.Name)).ToHashSet(StringComparer.Ordinal);
            var renames = new List<(Font, string)>();
            foreach (var group in fonts.GroupBy(f => f.Key))
            {
                // Fonts that are really one font (same data, same letters) may share a name. A font
                // the file does not carry is drawn by name with one installed on the computer, so it
                // must keep its name; an embedded font sharing it is the one to change.
                var different = group.Where(f => f.Embedded).GroupBy(f => f.Signature).ToList();
                bool nameIsHeld = group.Any(f => !f.Embedded);
                foreach (var same in different.Skip(nameIsHeld ? 0 : 1))
                {
                    string plain = WithoutPrefix(same.First().Name), suffix;
                    int k = 2;
                    do suffix = "-" + k++.ToString(CultureInfo.InvariantCulture); while (!taken.Add(plain + suffix));
                    foreach (var font in same) renames.Add((font, suffix));
                }
            }
            return renames;
        }

        // ---- writing the update

        /// <summary>
        /// The file followed by an update holding the renamed font entries: the form a PDF takes
        /// when a program saves changes without rewriting the whole file.
        /// </summary>
        private byte[] Append(List<(Font Font, string Suffix)> renames)
        {
            using var output = new MemoryStream(pdf.Length + 4096);
            output.Write(pdf);
            void Text(string text) => output.Write(Encoding.Latin1.GetBytes(text));
            Text("\n");

            var placed = new SortedDictionary<int, (int Offset, int Generation)>();
            foreach (var (font, suffix) in renames)
            {
                var (dict, source) = (font.Object.Value, font.Object.Source);
                placed[font.Number] = ((int)output.Position, font.Object.Generation);
                Text($"{font.Number} {font.Object.Generation} obj\n");
                output.Write(source, dict.Start, font.NameNode.End - dict.Start);
                Text(suffix);
                output.Write(source, font.NameNode.End, dict.End - font.NameNode.End);
                Text("\nendobj\n");
            }

            int size = Math.Max(_trailer!["Size"] is { IsInteger: true } s ? s.Int : 0, _entries.Keys.Max() + 1);
            string kept = $" /Root {Raw(_trailer["Root"], pdf)}" +
                          (_trailer["Info"] is { } info ? $" /Info {Raw(info, pdf)}" : "") +
                          (_trailer["ID"] is { } id ? $" /ID {Raw(id, pdf)}" : "") +
                          $" /Prev {_lastXref}";

            // The update's cross-reference takes the form the file already uses.
            if (_trailerIsStream) placed[size] = ((int)output.Position, 0);
            var runs = new List<(int First, List<(int Offset, int Generation)> Items)>();
            foreach (var (number, place) in placed)
            {
                if (runs.Count == 0 || runs[^1].First + runs[^1].Items.Count != number) runs.Add((number, []));
                runs[^1].Items.Add(place);
            }
            int start = (int)output.Position;
            if (_trailerIsStream)
            {
                var rows = new MemoryStream();
                foreach (var (offset, generation) in placed.Values)
                    rows.Write([1, (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset, (byte)(generation >> 8), (byte)generation]);
                string index = string.Join(" ", runs.Select(r => $"{r.First} {r.Items.Count}"));
                Text($"{size} 0 obj\n<< /Type /XRef /Size {size + 1} /W [1 4 2] /Index [{index}]{kept} /Length {rows.Length} >>\nstream\n");
                rows.WriteTo(output);
                Text("\nendstream\nendobj\n");
            }
            else
            {
                Text("xref\n");
                foreach (var (first, items) in runs)
                {
                    Text($"{first} {items.Count}\n");
                    foreach (var (offset, generation) in items) Text($"{offset:D10} {generation:D5} n \n");
                }
                Text($"trailer\n<< /Size {size}{kept} >>\n");
            }
            Text($"startxref\n{start}\n%%EOF\n");
            return output.ToArray();
        }
    }
}
