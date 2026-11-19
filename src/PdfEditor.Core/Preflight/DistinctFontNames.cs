using System.Globalization;
using System.IO.Compression;
using System.Text;

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

    // ------------------------------------------------------------------ a small PDF object model

    private enum Kind { Null, Bool, Number, Name, String, Array, Dict, Ref }

    /// <summary>One parsed value, with where it sits in the bytes it was read from.</summary>
    private sealed class Node
    {
        public Kind Kind;
        public int Start, End;                    // End is one past the last byte
        public double Number;
        public string Name = "";
        public List<Node> Items = [];
        public Dictionary<string, Node> Entries = [];
        public int RefNumber, RefGeneration;

        public Node? this[string key] => Kind == Kind.Dict && Entries.TryGetValue(key, out var value) ? value : null;
        public bool IsInteger => Kind == Kind.Number && Number == Math.Floor(Number) && Math.Abs(Number) < int.MaxValue;
        public int Int => IsInteger ? (int)Number : throw new FormatException("A whole number was expected.");
    }

    private sealed class Reader(byte[] data, int position)
    {
        public readonly byte[] Data = data;
        public int Position = position;

        private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
        private bool AtEnd => Position >= Data.Length;

        public void SkipSpace()
        {
            while (!AtEnd)
            {
                byte b = Data[Position];
                if (IsSpace(b)) Position++;
                else if (b == '%') while (!AtEnd && Data[Position] is not (10 or 13)) Position++;
                else break;
            }
        }

        /// <summary>True, and moves past it, when the given word comes next as a whole word.</summary>
        public bool TryWord(string word)
        {
            SkipSpace();
            if (Position + word.Length > Data.Length) return false;
            for (int i = 0; i < word.Length; i++) if (Data[Position + i] != word[i]) return false;
            int after = Position + word.Length;
            if (after < Data.Length && !IsSpace(Data[after]) && !IsDelimiter(Data[after])) return false;
            Position = after;
            return true;
        }

        public int ReadInt()
        {
            SkipSpace();
            var node = ReadObject();
            return node.Int;
        }

        public Node ReadObject()
        {
            SkipSpace();
            if (AtEnd) throw new FormatException("The file ends in the middle of an object.");
            int start = Position;
            byte b = Data[Position];
            Node node;
            if (b == '/')
            {
                Position++;
                var name = new StringBuilder();
                while (!AtEnd && !IsSpace(Data[Position]) && !IsDelimiter(Data[Position]))
                {
                    if (Data[Position] == '#' && Position + 2 < Data.Length && Uri.IsHexDigit((char)Data[Position + 1]) && Uri.IsHexDigit((char)Data[Position + 2]))
                    {
                        name.Append((char)Convert.ToInt32(Encoding.ASCII.GetString(Data, Position + 1, 2), 16));
                        Position += 3;
                    }
                    else name.Append((char)Data[Position++]);
                }
                node = new Node { Kind = Kind.Name, Name = name.ToString() };
            }
            else if (b == '(')
            {
                int depth = 0;
                do
                {
                    if (AtEnd) throw new FormatException("A text string is not closed.");
                    byte c = Data[Position++];
                    if (c == '\\') Position++;
                    else if (c == '(') depth++;
                    else if (c == ')') depth--;
                } while (depth > 0);
                node = new Node { Kind = Kind.String };
            }
            else if (b == '<' && Position + 1 < Data.Length && Data[Position + 1] == '<')
            {
                Position += 2;
                node = new Node { Kind = Kind.Dict };
                while (true)
                {
                    SkipSpace();
                    if (AtEnd) throw new FormatException("A dictionary is not closed.");
                    if (Data[Position] == '>' && Position + 1 < Data.Length && Data[Position + 1] == '>') { Position += 2; break; }
                    var key = ReadObject();
                    if (key.Kind != Kind.Name) throw new FormatException("A dictionary key is not a name.");
                    node.Entries[key.Name] = ReadObject();
                }
            }
            else if (b == '<')
            {
                while (!AtEnd && Data[Position] != '>') Position++;
                Position++;
                node = new Node { Kind = Kind.String };
            }
            else if (b == '[')
            {
                Position++;
                node = new Node { Kind = Kind.Array };
                while (true)
                {
                    SkipSpace();
                    if (AtEnd) throw new FormatException("An array is not closed.");
                    if (Data[Position] == ']') { Position++; break; }
                    node.Items.Add(ReadObject());
                }
            }
            else if (b is (byte)'+' or (byte)'-' or (byte)'.' || b is >= (byte)'0' and <= (byte)'9')
            {
                while (!AtEnd && (Data[Position] is (byte)'+' or (byte)'-' or (byte)'.' || Data[Position] is >= (byte)'0' and <= (byte)'9')) Position++;
                string text = Encoding.ASCII.GetString(Data, start, Position - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) throw new FormatException("A number could not be read.");
                node = new Node { Kind = Kind.Number, Number = value };

                // "12 0 R" is a reference to object 12.
                if (node.IsInteger && value >= 0 && !text.Contains('.'))
                {
                    int afterNumber = Position;
                    SkipSpace();
                    int g = Position;
                    while (!AtEnd && Data[Position] is >= (byte)'0' and <= (byte)'9') Position++;
                    if (Position > g && Position - g <= 5)
                    {
                        int generation = int.Parse(Encoding.ASCII.GetString(Data, g, Position - g), CultureInfo.InvariantCulture);
                        if (TryWord("R")) node = new Node { Kind = Kind.Ref, RefNumber = (int)value, RefGeneration = generation };
                        else Position = afterNumber;
                    }
                    else Position = afterNumber;
                }
            }
            else if (TryWord("true") || TryWord("false")) node = new Node { Kind = Kind.Bool };
            else if (TryWord("null")) node = new Node { Kind = Kind.Null };
            else throw new FormatException("Something that is not a PDF object was found.");
            node.Start = start;
            node.End = Position;
            return node;
        }

        /// <summary>Reads "12 0 obj ..." and says where its stream data begins (or -1 when it has none).</summary>
        public (int Number, int Generation, Node Value, int StreamStart) ReadIndirect()
        {
            int number = ReadInt(), generation = ReadInt();
            if (!TryWord("obj")) throw new FormatException("An object does not start as it should.");
            var value = ReadObject();
            int streamStart = -1;
            if (value.Kind == Kind.Dict && TryWord("stream"))
            {
                // The word is followed by a line end, then the data.
                if (!AtEnd && Data[Position] == 13) Position++;
                if (!AtEnd && Data[Position] == 10) Position++;
                streamStart = Position;
            }
            return (number, generation, value, streamStart);
        }
    }

    // ------------------------------------------------------------------ the pass over one file

    /// <summary>Where an object is: at an offset in the file (Type 1), or inside an object stream (Type 2).</summary>
    private readonly record struct Entry(int Type, int A, int B);

    private sealed record Loaded(Node Value, byte[] Source, int StreamStart, int Generation);

    private sealed record Font(int Number, Loaded Object, Node NameNode, string Name, string Key, bool Embedded, string Signature);

    private sealed class Pass(byte[] pdf)
    {
        private readonly Dictionary<int, Entry> _entries = [];
        private readonly Dictionary<int, Loaded> _objects = [];
        private Node? _trailer;          // of the newest part of the file
        private bool _trailerIsStream;   // the newest cross-reference is a stream, not a table
        private int _lastXref;

        public byte[] Run()
        {
            if (pdf.Length < 32 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F') return pdf;
            ReadCrossReferences();
            if (_trailer is null || _trailer["Encrypt"] is not null || _trailer["Root"] is null) return pdf;
            LoadObjects();
            var renames = ChooseNames(FindFonts());
            return renames.Count == 0 ? pdf : Append(renames);
        }

        // ---- finding where every object is

        private void ReadCrossReferences()
        {
            int tailStart = Math.Max(0, pdf.Length - 2048);
            int marker = Encoding.Latin1.GetString(pdf, tailStart, pdf.Length - tailStart).LastIndexOf("startxref", StringComparison.Ordinal);
            if (marker < 0) throw new FormatException("The file has no cross-reference table.");
            _lastXref = new Reader(pdf, tailStart + marker + 9).ReadInt();

            var seen = new HashSet<int>();
            int position = _lastXref;
            while (position > 0 && position < pdf.Length && seen.Add(position))
            {
                var reader = new Reader(pdf, position);
                Node trailer;
                bool isStream = !reader.TryWord("xref");
                if (!isStream)
                {
                    while (!reader.TryWord("trailer"))
                    {
                        int first = reader.ReadInt(), count = reader.ReadInt();
                        for (int i = 0; i < count; i++)
                        {
                            int offset = reader.ReadInt(), generation = reader.ReadInt();
                            bool used = reader.TryWord("n");
                            if (!used && !reader.TryWord("f")) throw new FormatException("A cross-reference entry is damaged.");
                            _entries.TryAdd(first + i, used ? new Entry(1, offset, generation) : new Entry(0, 0, 0));
                        }
                    }
                    trailer = reader.ReadObject();
                    if (trailer.Kind != Kind.Dict) throw new FormatException("The trailer is damaged.");
                    // A file may keep part of its table in a stream as well.
                    if (trailer["XRefStm"] is { IsInteger: true } extra && seen.Add(extra.Int)) ReadCrossReferenceStream(new Reader(pdf, extra.Int));
                }
                else trailer = ReadCrossReferenceStream(reader);

                if (_trailer is null)
                {
                    _trailer = trailer;
                    _trailerIsStream = isStream;
                }
                position = trailer["Prev"] is { IsInteger: true } previous ? previous.Int : 0;
            }
        }

        private Node ReadCrossReferenceStream(Reader reader)
        {
            var (_, _, dict, streamStart) = reader.ReadIndirect();
            if (streamStart < 0 || dict["Type"]?.Name != "XRef") throw new FormatException("A cross-reference stream was expected.");
            byte[] data = StreamData(dict, pdf, streamStart) ?? throw new FormatException("The cross-reference stream could not be unpacked.");
            var widths = dict["W"] is { Kind: Kind.Array, Items.Count: 3 } w ? w.Items.Select(i => i.Int).ToArray() : throw new FormatException("The cross-reference stream has no field sizes.");
            var index = dict["Index"] is { Kind: Kind.Array } ix ? ix.Items.Select(i => i.Int).ToArray() : [0, dict["Size"]!.Int];
            int row = widths.Sum(), at = 0;
            if (row <= 0 || widths.Any(x => x is < 0 or > 8)) throw new FormatException("The cross-reference stream has bad field sizes.");
            for (int section = 0; section + 1 < index.Length; section += 2)
                for (int i = 0; i < index[section + 1]; i++, at += row)
                {
                    if (at + row > data.Length) throw new FormatException("The cross-reference stream is too short.");
                    long Field(int which)
                    {
                        long value = 0;
                        int from = at + widths.Take(which).Sum();
                        for (int k = 0; k < widths[which]; k++) value = (value << 8) | data[from + k];
                        return value;
                    }
                    long type = widths[0] == 0 ? 1 : Field(0);
                    _entries.TryAdd(index[section] + i, type is 1 or 2 ? new Entry((int)type, checked((int)Field(1)), checked((int)Field(2))) : new Entry(0, 0, 0));
                }
            return dict;
        }

        /// <summary>The unpacked data of a stream, or null when it is packed in a way not handled here.</summary>
        private byte[]? StreamData(Node dict, byte[] source, int start)
        {
            int length = -1;
            var lengthNode = dict["Length"];
            if (lengthNode is { Kind: Kind.Ref } && _entries.TryGetValue(lengthNode.RefNumber, out var entry) && entry.Type == 1)
            {
                try { lengthNode = new Reader(pdf, entry.A).ReadIndirect().Value; }
                catch (FormatException) { lengthNode = null; }
            }
            if (lengthNode is { IsInteger: true }) length = lengthNode.Int;
            if (length < 0 || start + length > source.Length)
            {
                // No usable length: the data runs up to the word that closes it.
                int end = IndexOf(source, "endstream"u8, start);
                if (end < 0) return null;
                length = end - start;
            }

            var filter = dict["Filter"];
            var parms = dict["DecodeParms"];
            if (filter is { Kind: Kind.Array, Items.Count: 1 }) filter = filter.Items[0];
            if (parms is { Kind: Kind.Array, Items.Count: 1 }) parms = parms.Items[0];
            if (filter is null or { Kind: Kind.Array, Items.Count: 0 }) return source.AsSpan(start, length).ToArray();
            if (filter.Kind != Kind.Name || filter.Name != "FlateDecode") return null;

            using var unpacked = new MemoryStream();
            using (var zlib = new ZLibStream(new MemoryStream(source, start, length), CompressionMode.Decompress))
            {
                try { zlib.CopyTo(unpacked); }
                catch (InvalidDataException) when (unpacked.Length > 0) { } // a damaged tail; use what came out
            }
            byte[] data = unpacked.ToArray();

            int predictor = parms?["Predictor"] is { IsInteger: true } p ? p.Int : 1;
            if (predictor == 1) return data;
            if (predictor < 10) return null; // the TIFF kind is not used for these streams
            int columns = parms?["Columns"] is { IsInteger: true } c ? c.Int : 1;
            int colours = parms?["Colors"] is { IsInteger: true } n ? n.Int : 1;
            int bits = parms?["BitsPerComponent"] is { IsInteger: true } bpc ? bpc.Int : 8;
            int pixel = Math.Max(1, colours * bits / 8), rowBytes = (columns * colours * bits + 7) / 8;
            if (rowBytes <= 0) return null;
            int rows = data.Length / (rowBytes + 1);
            var result = new byte[rows * rowBytes];
            for (int r = 0; r < rows; r++)
            {
                int from = r * (rowBytes + 1), to = r * rowBytes;
                byte kind = data[from];
                for (int i = 0; i < rowBytes; i++)
                {
                    int left = i >= pixel ? result[to + i - pixel] : 0;
                    int up = r > 0 ? result[to - rowBytes + i] : 0;
                    int upLeft = r > 0 && i >= pixel ? result[to - rowBytes + i - pixel] : 0;
                    int predicted = kind switch
                    {
                        1 => left,
                        2 => up,
                        3 => (left + up) / 2,
                        4 => Paeth(left, up, upLeft),
                        _ => 0,
                    };
                    result[to + i] = (byte)(data[from + 1 + i] + predicted);
                }
            }
            return result;

            static int Paeth(int a, int b, int c)
            {
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            }
        }

        private static int IndexOf(byte[] data, ReadOnlySpan<byte> word, int from)
        {
            int found = data.AsSpan(from).IndexOf(word);
            return found < 0 ? -1 : from + found;
        }

        // ---- reading the objects

        private void LoadObjects()
        {
            foreach (var (number, entry) in _entries)
            {
                if (entry.Type != 1 || entry.A <= 0 || entry.A >= pdf.Length) continue;
                try
                {
                    var (n, _, value, streamStart) = new Reader(pdf, entry.A).ReadIndirect();
                    if (n == number) _objects[number] = new Loaded(value, pdf, streamStart, entry.B);
                }
                catch (FormatException) { } // a damaged object: not a font we can rename
            }

            // Objects packed together inside object streams.
            foreach (var group in _entries.Where(e => e.Value.Type == 2).GroupBy(e => e.Value.A))
            {
                if (!_objects.TryGetValue(group.Key, out var holder) || holder.StreamStart < 0 || holder.Value["Type"]?.Name != "ObjStm") continue;
                try
                {
                    byte[]? data = StreamData(holder.Value, pdf, holder.StreamStart);
                    if (data is null) continue;
                    int count = holder.Value["N"]!.Int, first = holder.Value["First"]!.Int;
                    var header = new Reader(data, 0);
                    var places = new (int Number, int Offset)[count];
                    for (int i = 0; i < count; i++) places[i] = (header.ReadInt(), header.ReadInt());
                    foreach (var (number, entry) in group)
                    {
                        if (entry.B < 0 || entry.B >= count || places[entry.B].Number != number) continue;
                        try { _objects[number] = new Loaded(new Reader(data, first + places[entry.B].Offset).ReadObject(), data, -1, 0); }
                        catch (FormatException) { }
                    }
                }
                catch (Exception e) when (e is FormatException or InvalidDataException or NullReferenceException) { }
            }
        }

        private (Node? Value, byte[]? Source) Resolve(Node? node, byte[] source)
        {
            for (int hops = 0; node is { Kind: Kind.Ref } && hops < 8; hops++)
            {
                if (!_objects.TryGetValue(node.RefNumber, out var target)) return (null, null);
                (node, source) = (target.Value, target.Source);
            }
            return (node, source);
        }

        private static string Raw(Node? node, byte[]? source) => node is null || source is null ? "" : Encoding.Latin1.GetString(source, node.Start, node.End - node.Start);

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
