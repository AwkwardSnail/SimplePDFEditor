using System.IO.Compression;
using System.Text;

namespace PdfEditor.Core.Preflight;

/// <summary>
/// Makes a saved file smaller without changing what is in it. PDFium writes every object of a
/// document out in plain text, one after another. A document with many small objects (a tagged
/// one has thousands) then comes out far larger than it went in, since the program that made it
/// had packed those objects into compressed "object streams". This pass packs them again, the
/// way PDF 1.5 allows, and leaves out objects nothing refers to any more.
/// <para>It works only on what PDFium itself has just written (one cross-reference table, no
/// encryption). On anything it does not expect it returns the bytes it was given.</para>
/// </summary>
internal static class PackObjects
{
    private const int PerStream = 200; // objects in one object stream

    public static byte[] Apply(byte[] pdf)
    {
        try
        {
            var packed = new Pass(pdf).Run();
            return packed is not null && packed.Length < pdf.Length ? packed : pdf;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            return pdf;
        }
    }

    private sealed class Pass(byte[] d)
    {
        private readonly List<int> _refs = [];

        private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        private int SkipSpace(int p)
        {
            while (p < d.Length)
            {
                if (IsSpace(d[p])) p++;
                else if (d[p] == '%') { while (p < d.Length && d[p] != 10 && d[p] != 13) p++; }
                else break;
            }
            return p;
        }

        private bool At(int p, string word)
        {
            if (p + word.Length > d.Length) return false;
            for (int i = 0; i < word.Length; i++) if (d[p + i] != word[i]) return false;
            return true;
        }

        private int ReadInt(ref int p)
        {
            p = SkipSpace(p);
            int start = p;
            long value = 0;
            while (p < d.Length && d[p] is >= (byte)'0' and <= (byte)'9') value = checked(value * 10 + (d[p++] - '0'));
            if (p == start || value > int.MaxValue) throw new FormatException("A number was expected.");
            return (int)value;
        }

        /// <summary>Steps over one value (of any kind) and returns where it ends. References found inside it are added to the list.</summary>
        private int Value(int p)
        {
            p = SkipSpace(p);
            if (p >= d.Length) throw new FormatException("The file ends inside an object.");
            switch (d[p])
            {
                case (byte)'<' when p + 1 < d.Length && d[p + 1] == '<':
                    return Items(p + 2, ">>");
                case (byte)'<':
                    while (p < d.Length && d[p] != '>') p++;
                    return p + 1;
                case (byte)'[':
                    return Items(p + 1, "]");
                case (byte)'(':
                {
                    int depth = 0;
                    for (; p < d.Length; p++)
                    {
                        if (d[p] == '\\') p++;
                        else if (d[p] == '(') depth++;
                        else if (d[p] == ')' && --depth == 0) return p + 1;
                    }
                    throw new FormatException("A string does not end.");
                }
                case (byte)'/':
                    p++;
                    while (p < d.Length && !IsSpace(d[p]) && !IsDelimiter(d[p])) p++;
                    return p;
                default:
                    int start = p;
                    while (p < d.Length && !IsSpace(d[p]) && !IsDelimiter(d[p])) p++;
                    if (p == start) throw new FormatException("Something unexpected is in an object.");
                    return p;
            }
        }

        /// <summary>Steps over the items of a dictionary or an array, noting each "12 0 R".</summary>
        private int Items(int p, string end)
        {
            long first = -1, second = -1; // the last two whole numbers seen in a row
            while (true)
            {
                p = SkipSpace(p);
                if (At(p, end)) return p + end.Length;
                int start = p;
                p = Value(p);
                bool plain = d[start] is not ((byte)'<' or (byte)'[' or (byte)'(' or (byte)'/');
                if (plain && p - start == 1 && d[start] == 'R' && first >= 0 && second >= 0)
                {
                    _refs.Add((int)first);
                    (first, second) = (-1, -1);
                    continue;
                }
                long number = -1;
                if (plain && p - start <= 10)
                {
                    number = 0;
                    for (int i = start; i < p && number >= 0; i++) number = d[i] is >= (byte)'0' and <= (byte)'9' ? number * 10 + (d[i] - '0') : -1;
                }
                (first, second) = number >= 0 ? (second, number) : (-1, -1);
            }
        }

        /// <summary>The top-level entries of a dictionary that starts at <paramref name="p"/>: each key with where its value lies.</summary>
        private Dictionary<string, (int Start, int End)> Entries(int p, out int end)
        {
            p = SkipSpace(p);
            if (!At(p, "<<")) throw new FormatException("A dictionary was expected.");
            p += 2;
            var entries = new Dictionary<string, (int, int)>();
            while (true)
            {
                p = SkipSpace(p);
                if (At(p, ">>")) { end = p + 2; return entries; }
                if (d[p] != '/') throw new FormatException("A dictionary key was expected.");
                int keyEnd = Value(p);
                string key = Encoding.Latin1.GetString(d, p, keyEnd - p);
                int valueStart = SkipSpace(keyEnd);
                p = Value(valueStart);
                // "12 0 R" is three tokens: take the other two when that is what follows.
                int q = SkipSpace(p);
                if (q < d.Length && d[q] is >= (byte)'0' and <= (byte)'9')
                {
                    int afterGeneration = Value(q), r = SkipSpace(afterGeneration);
                    if (r < d.Length && d[r] == 'R' && (r + 1 >= d.Length || IsSpace(d[r + 1]) || IsDelimiter(d[r + 1]))) p = r + 1;
                }
                entries[key] = (valueStart, p);
            }
        }

        private sealed record Obj(int Number, int Generation, int ValueStart, int ValueEnd, bool IsStream, int End, List<int> Refs);

        public byte[]? Run()
        {
            // Where the cross-reference table is.
            int tail = Math.Max(0, d.Length - 1024);
            int marker = -1;
            for (int i = d.Length - 9; i >= tail; i--) if (At(i, "startxref")) { marker = i; break; }
            if (marker < 0) return null;
            int p = marker + 9;
            int xref = ReadInt(ref p);
            if (!At(xref, "xref")) return null; // not a plain table: not what PDFium writes

            // The table: where every object in use starts.
            var offsets = new Dictionary<int, (int Offset, int Generation)>();
            p = xref + 4;
            while (true)
            {
                p = SkipSpace(p);
                if (At(p, "trailer")) break;
                int first = ReadInt(ref p), count = ReadInt(ref p);
                p = SkipSpace(p);
                for (int i = 0; i < count; i++)
                {
                    int offset = ReadInt(ref p), generation = ReadInt(ref p);
                    p = SkipSpace(p);
                    byte kind = d[p++];
                    if (kind == 'n' && offset > 0) offsets[first + i] = (offset, generation);
                    else if (kind != 'f' && kind != 'n') throw new FormatException("The cross-reference table is not as expected.");
                }
            }
            var trailer = Entries(p + 7, out _);
            if (trailer.ContainsKey("/Encrypt") || trailer.ContainsKey("/Prev") || !trailer.ContainsKey("/Root")) return null;

            // Every object: its value, whether it is a stream, and what it refers to.
            var starts = offsets.Values.Select(o => o.Offset).Append(xref).Distinct().OrderBy(o => o).ToList();
            var objects = new Dictionary<int, Obj>();
            foreach (var (number, (offset, generation)) in offsets)
            {
                int q = offset;
                if (ReadInt(ref q) != number) throw new FormatException("An object is not where the table says.");
                ReadInt(ref q);
                q = SkipSpace(q);
                if (!At(q, "obj")) throw new FormatException("An object is not where the table says.");
                int valueStart = SkipSpace(q + 3);
                _refs.Clear();
                int valueEnd = Value(valueStart);
                var refs = _refs.ToList();
                int after = SkipSpace(valueEnd);
                int limit = starts[starts.BinarySearch(offset) + 1]; // where the next thing in the file starts
                if (At(after, "endobj")) objects[number] = new Obj(number, generation, valueStart, valueEnd, false, valueEnd, refs);
                else if (At(after, "stream"))
                {
                    // The stream runs to the last "endobj" before the next object.
                    int end = -1;
                    for (int i = limit - 6; i > after; i--) if (At(i, "endobj")) { end = i; break; }
                    if (end < 0) throw new FormatException("A stream does not end.");
                    while (end > after && IsSpace(d[end - 1])) end--;
                    objects[number] = new Obj(number, generation, valueStart, valueEnd, true, end, refs);
                }
                else throw new FormatException("An object does not end as expected.");
            }

            // What can be reached from the trailer; the rest is left behind.
            var keep = new HashSet<int>();
            var toVisit = new Stack<int>();
            foreach (var (_, (start, end)) in trailer)
            {
                int q = start;
                var numbers = new List<int>();
                while (q < end)
                {
                    q = SkipSpace(q);
                    if (q >= end) break;
                    int tokenStart = q;
                    q = Value(q);
                    if (q - tokenStart == 1 && d[tokenStart] == 'R' && numbers.Count >= 2) { toVisit.Push(numbers[^2]); numbers.Clear(); continue; }
                    if (int.TryParse(Encoding.Latin1.GetString(d, tokenStart, q - tokenStart), out int n) && n >= 0) numbers.Add(n);
                    else
                    {
                        numbers.Clear();
                        // An array or dictionary in the trailer (the /ID array holds none, but be thorough).
                        if (d[tokenStart] is (byte)'[' or (byte)'<')
                        {
                            _refs.Clear();
                            Value(tokenStart);
                            foreach (int r in _refs) toVisit.Push(r);
                        }
                    }
                }
            }
            while (toVisit.Count > 0)
            {
                int n = toVisit.Pop();
                if (!keep.Add(n) || !objects.TryGetValue(n, out var o)) continue;
                foreach (int r in o.Refs) toVisit.Push(r);
            }
            var kept = objects.Values.Where(o => keep.Contains(o.Number)).OrderBy(o => o.Number).ToList();
            if (kept.Count == 0) return null;

            // Write: streams and anything with a generation as they are; the rest packed, 200 to a stream.
            var output = new MemoryStream(d.Length / 2);
            void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
            int headerEnd = 0;
            while (headerEnd < d.Length && d[headerEnd] != 10 && d[headerEnd] != 13) headerEnd++;
            string header = Encoding.Latin1.GetString(d, 0, headerEnd);
            if (!header.StartsWith("%PDF-1.", StringComparison.Ordinal) && !header.StartsWith("%PDF-2.", StringComparison.Ordinal)) return null;
            // Object streams came with PDF 1.5.
            if (header.StartsWith("%PDF-1.", StringComparison.Ordinal) && int.TryParse(header.AsSpan(7), out int minor) && minor < 5) header = "%PDF-1.5";
            Write(header + "\n%âãÏÓ\n");

            int next = Math.Max(offsets.Keys.Max(), 0) + 1; // numbers for the new objects
            var where = new Dictionary<int, (int Type, long A, int B)>(); // object -> cross-reference entry
            foreach (var o in kept.Where(o => o.IsStream || o.Generation != 0))
            {
                where[o.Number] = (1, output.Position, o.Generation);
                Write($"{o.Number} {o.Generation} obj\n");
                output.Write(d, o.ValueStart, o.End - o.ValueStart);
                Write(o.IsStream ? "\nendobj\n" : "\nendobj\n");
            }
            var loose = kept.Where(o => !o.IsStream && o.Generation == 0).ToList();
            for (int i = 0; i < loose.Count; i += PerStream)
            {
                var group = loose.Skip(i).Take(PerStream).ToList();
                var index = new StringBuilder();
                var body = new MemoryStream();
                for (int k = 0; k < group.Count; k++)
                {
                    index.Append(group[k].Number).Append(' ').Append(body.Position).Append(' ');
                    body.Write(d, group[k].ValueStart, group[k].ValueEnd - group[k].ValueStart);
                    body.WriteByte((byte)'\n');
                    where[group[k].Number] = (2, next, k);
                }
                byte[] head = Encoding.Latin1.GetBytes(index.ToString());
                byte[] data = Deflate([.. head, .. body.ToArray()]);
                where[next] = (1, output.Position, 0);
                Write($"{next} 0 obj\n<</Type/ObjStm/N {group.Count}/First {head.Length}/Filter/FlateDecode/Length {data.Length}>>stream\n");
                output.Write(data);
                Write("\nendstream\nendobj\n");
                next++;
            }

            // The cross-reference stream, which also carries what the trailer carried.
            long xrefAt = output.Position;
            where[next] = (1, xrefAt, 0);
            int size = next + 1;
            var table = new byte[size * 7];
            for (int n = 0; n < size; n++)
            {
                var (type, a, b) = where.TryGetValue(n, out var entry) ? entry : (0, 0L, n == 0 ? 65535 : 0);
                table[n * 7] = (byte)type;
                table[n * 7 + 1] = (byte)(a >> 24); table[n * 7 + 2] = (byte)(a >> 16); table[n * 7 + 3] = (byte)(a >> 8); table[n * 7 + 4] = (byte)a;
                table[n * 7 + 5] = (byte)(b >> 8); table[n * 7 + 6] = (byte)b;
            }
            if (xrefAt > uint.MaxValue) return null;
            byte[] packedTable = Deflate(table);
            var dict = new StringBuilder($"<</Type/XRef/Size {size}/W[1 4 2]");
            foreach (var (key, (start, end)) in trailer)
                if (key is not ("/Size" or "/Type" or "/W" or "/Index" or "/Length" or "/Filter" or "/DecodeParms" or "/XRefStm"))
                    dict.Append(key).Append(' ').Append(Encoding.Latin1.GetString(d, start, end - start));
            dict.Append($"/Filter/FlateDecode/Length {packedTable.Length}>>");
            Write($"{next} 0 obj\n{dict}stream\n");
            output.Write(packedTable);
            Write($"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n");
            return output.ToArray();
        }

        private static byte[] Deflate(byte[] data)
        {
            var result = new MemoryStream();
            using (var z = new ZLibStream(result, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            return result.ToArray();
        }
    }
}
