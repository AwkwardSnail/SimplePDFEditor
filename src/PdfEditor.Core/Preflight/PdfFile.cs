using System.Globalization;
using System.IO.Compression;
using System.Text;


namespace PdfEditor.Core.Preflight;

/// <summary>
/// A PDF file read directly from its bytes: where every object is, the objects themselves, and
/// the unpacked data of streams. PDFium keeps much of this to itself, so the checks made on
/// opening, and the reading of a page's own instructions, look at the file here instead.
/// </summary>
internal sealed class PdfFile(byte[] pdf)
{
    public byte[] Bytes => pdf;

    // ------------------------------------------------------------------ a small PDF object model

    public enum Kind { Null, Bool, Number, Name, String, Array, Dict, Ref }

    /// <summary>One parsed value, with where it sits in the bytes it was read from.</summary>
    public sealed class Node
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

    public sealed class Reader(byte[] data, int position)
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

    /// <summary>Where an object is: at an offset in the file (Type 1), or inside an object stream (Type 2).</summary>
    public readonly record struct Entry(int Type, int A, int B);

    public sealed record Loaded(Node Value, byte[] Source, int StreamStart, int Generation);

    public Dictionary<int, Entry> Entries => _entries;
    /// <summary>Every object in the file. Asking for this reads them all; <see cref="TryGet"/> reads only the one asked for.</summary>
    public Dictionary<int, Loaded> Objects
    {
        get
        {
            if (!_allLoaded) LoadObjects();
            return _objects;
        }
    }
    public Node? Trailer => _trailer;
    public bool TrailerIsStream => _trailerIsStream;
    public int LastXref => _lastXref;

    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Dictionary<int, Loaded> _objects = [];
    private bool _allLoaded;
    private readonly HashSet<int> _unreadable = [];
    // Object streams already unpacked: their data, where each object in them starts, and where the objects begin.
    private readonly Dictionary<int, (byte[] Data, (int Number, int Offset)[] Places, int First)?> _packs = [];

    /// <summary>
    /// One object of the file, read when it is first asked for. An edit to one page needs a few
    /// dozen of a file's objects, not the thousands a large file holds.
    /// </summary>
    public bool TryGet(int number, out Loaded loaded)
    {
        if (_objects.TryGetValue(number, out loaded!)) return true;
        if (_allLoaded || _unreadable.Contains(number) || !_entries.TryGetValue(number, out var entry)) return false;
        try
        {
            if (entry.Type == 1 && entry.A > 0 && entry.A < pdf.Length)
            {
                var (n, _, value, streamStart) = new Reader(pdf, entry.A).ReadIndirect();
                if (n == number)
                {
                    _objects[number] = loaded = new Loaded(value, pdf, streamStart, entry.B);
                    return true;
                }
            }
            else if (entry.Type == 2 && entry.A != number)
            {
                if (!_packs.TryGetValue(entry.A, out var pack))
                {
                    pack = null;
                    if (TryGet(entry.A, out var holder) && holder.StreamStart >= 0 && holder.Value["Type"]?.Name == "ObjStm" && StreamData(holder.Value, pdf, holder.StreamStart) is { } data)
                    {
                        int count = holder.Value["N"]!.Int;
                        var header = new Reader(data, 0);
                        var places = new (int Number, int Offset)[count];
                        for (int i = 0; i < count; i++) places[i] = (header.ReadInt(), header.ReadInt());
                        pack = (data, places, holder.Value["First"]!.Int);
                    }
                    _packs[entry.A] = pack;
                }
                if (pack is { } p && entry.B >= 0 && entry.B < p.Places.Length && p.Places[entry.B].Number == number)
                {
                    _objects[number] = loaded = new Loaded(new Reader(p.Data, p.First + p.Places[entry.B].Offset).ReadObject(), p.Data, -1, 0);
                    return true;
                }
            }
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or NullReferenceException or IndexOutOfRangeException) { }
        _unreadable.Add(number);
        return false;
    }
    private Node? _trailer;          // of the newest part of the file
    private bool _trailerIsStream;   // the newest cross-reference is a stream, not a table
    private int _lastXref;

    /// <summary>
    /// Reads a whole file: by its cross-reference table, or, when that is missing or damaged, by
    /// looking for every "12 0 obj" in it (as PDF readers do to repair such a file).
    /// </summary>
    /// <param name="readAll">False to read each object only when it is asked for (see <see cref="TryGet"/>).</param>
    public static PdfFile Open(byte[] bytes, bool readAll = true)
    {
        var file = new PdfFile(bytes);
        try { file.ReadCrossReferences(); }
        catch (Exception e) when (e is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            file.FindObjectsBySearching();
        }
        if (file._trailer is null) file.FindObjectsBySearching();
        if (readAll) file.LoadObjects();
        return file;
    }

    private void FindObjectsBySearching()
    {
        _entries.Clear();
        string text = Encoding.Latin1.GetString(pdf);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?<![0-9])(\d{1,9})[ \t\r\n]+(\d{1,5})[ \t\r\n]+obj(?![A-Za-z0-9])"))
            _entries[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = new Entry(1, m.Index, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        _trailer = null;
        int at = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (at >= 0)
        {
            try { if (new Reader(pdf, at + 7).ReadObject() is { Kind: Kind.Dict } dict && dict["Root"] is not null) _trailer = dict; }
            catch (FormatException) { }
        }
        if (_trailer is not null) return;
        // No trailer either: the document's catalogue is the object that says it is one.
        foreach (var (number, entry) in _entries)
        {
            try
            {
                var value = new Reader(pdf, entry.A).ReadIndirect().Value;
                if (value["Type"]?.Name != "Catalog") continue;
                _trailer = new Node { Kind = Kind.Dict };
                _trailer.Entries["Root"] = new Node { Kind = Kind.Ref, RefNumber = number };
                return;
            }
            catch (FormatException) { }
        }
        throw new FormatException("The file's objects could not be found.");
    }

    // ---- finding where every object is

    public void ReadCrossReferences()
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

    public Node ReadCrossReferenceStream(Reader reader)
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
    public byte[]? StreamData(Node dict, byte[] source, int start)
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

        // A stream can be packed several times over; undo each layer in turn.
        var filters = dict["Filter"] switch { { Kind: Kind.Array } list => list.Items, { Kind: Kind.Name } one => [one], _ => new List<Node>() };
        var settings = dict["DecodeParms"] switch { { Kind: Kind.Array } list => list.Items, { Kind: Kind.Dict } one => [one], _ => new List<Node>() };
        byte[]? data = source.AsSpan(start, length).ToArray();
        for (int i = 0; i < filters.Count && data is not null; i++)
        {
            var parms = i < settings.Count && settings[i].Kind == Kind.Dict ? settings[i] : null;
            switch (filters[i].Kind == Kind.Name ? filters[i].Name : "")
            {
                case "FlateDecode" or "Fl": data = Unpredicted(Inflated(data), parms); break;
                case "LZWDecode" or "LZW": data = Unpredicted(Lzw(data, parms?["EarlyChange"] is { IsInteger: true } early ? early.Int != 0 : true), parms); break;
                case "ASCIIHexDecode" or "AHx": data = FromHex(data); break;
                case "ASCII85Decode" or "A85": data = FromBase85(data); break;
                case "RunLengthDecode" or "RL": data = FromRuns(data); break;
                default: return null; // a picture's own packing (JPEG and the like), or one not known here
            }
        }
        return data;

        static byte[] Inflated(byte[] packed)
        {
            using var unpacked = new MemoryStream();
            using (var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                try { zlib.CopyTo(unpacked); }
                catch (InvalidDataException) when (unpacked.Length > 0) { } // a damaged tail; use what came out
            }
            return unpacked.ToArray();
        }

        static byte[]? Unpredicted(byte[] data, Node? parms)
        {
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
        }

        // Pairs of hexadecimal digits, ending at ">".
        static byte[] FromHex(byte[] text)
        {
            var result = new List<byte>(text.Length / 2);
            int high = -1;
            foreach (byte b in text)
            {
                if (b == '>') break;
                if (!Uri.IsHexDigit((char)b)) continue;
                int digit = Convert.ToInt32(((char)b).ToString(), 16);
                if (high < 0) high = digit;
                else { result.Add((byte)(high * 16 + digit)); high = -1; }
            }
            if (high >= 0) result.Add((byte)(high * 16));
            return result.ToArray();
        }

        // Five characters for four bytes, "z" for four zero bytes, ending at "~>".
        static byte[] FromBase85(byte[] text)
        {
            var result = new List<byte>(text.Length);
            long group = 0;
            int count = 0;
            foreach (byte b in text)
            {
                if (b == '~') break;
                if (b == 'z' && count == 0) { result.AddRange(new byte[4]); continue; }
                if (b < '!' || b > 'u') continue;
                group = group * 85 + (b - '!');
                if (++count < 5) continue;
                result.Add((byte)(group >> 24)); result.Add((byte)(group >> 16)); result.Add((byte)(group >> 8)); result.Add((byte)group);
                (group, count) = (0, 0);
            }
            if (count > 1)
            {
                for (int i = count; i < 5; i++) group = group * 85 + 84;
                for (int i = 0; i < count - 1; i++) result.Add((byte)(group >> (24 - 8 * i)));
            }
            return result.ToArray();
        }

        // A length byte, then that many bytes as they are, or one byte repeated.
        static byte[] FromRuns(byte[] packed)
        {
            var result = new List<byte>(packed.Length * 2);
            for (int i = 0; i < packed.Length; )
            {
                int n = packed[i++];
                if (n == 128) break;
                if (n < 128) { for (int k = 0; k <= n && i < packed.Length; k++) result.Add(packed[i++]); }
                else if (i < packed.Length) { byte b = packed[i++]; for (int k = 0; k < 257 - n; k++) result.Add(b); }
            }
            return result.ToArray();
        }

        // LZW as PDF uses it: codes of 9 to 12 bits, 256 clears the table, 257 ends the data.
        static byte[] Lzw(byte[] packed, bool earlyChange)
        {
            var result = new List<byte>(packed.Length * 3);
            var table = new List<byte[]>(4096);
            void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); }
            Reset();
            int bits = 9, buffer = 0, held = 0;
            byte[]? previous = null;
            foreach (byte b in packed)
            {
                buffer = (buffer << 8) | b;
                held += 8;
                while (held >= bits)
                {
                    int code = (buffer >> (held - bits)) & ((1 << bits) - 1);
                    held -= bits;
                    if (code == 257) return result.ToArray();
                    if (code == 256) { Reset(); bits = 9; previous = null; continue; }
                    byte[] entry;
                    if (code < table.Count) entry = table[code];
                    else if (code == table.Count && previous is not null) entry = [.. previous, previous[0]];
                    else return result.ToArray(); // damaged: use what came out
                    result.AddRange(entry);
                    if (previous is not null && table.Count < 4096) table.Add([.. previous, entry[0]]);
                    previous = entry;
                    int limit = table.Count + (earlyChange ? 1 : 0);
                    bits = limit >= 2048 ? 12 : limit >= 1024 ? 11 : limit >= 512 ? 10 : 9; // one bit more as the table reaches each power of two
                }
            }
            return result.ToArray();
        }
        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    public static int IndexOf(byte[] data, ReadOnlySpan<byte> word, int from)
    {
        int found = data.AsSpan(from).IndexOf(word);
        return found < 0 ? -1 : from + found;
    }

    // ---- reading the objects

    public void LoadObjects()
    {
        _allLoaded = true;
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

    public (Node? Value, byte[]? Source) Resolve(Node? node, byte[] source)
    {
        for (int hops = 0; node is { Kind: Kind.Ref } && hops < 8; hops++)
        {
            if (!TryGet(node.RefNumber, out var target)) return (null, null);
            (node, source) = (target.Value, target.Source);
        }
        return (node, source);
    }

    public static string Raw(Node? node, byte[]? source) => node is null || source is null ? "" : Encoding.Latin1.GetString(source, node.Start, node.End - node.Start);
}