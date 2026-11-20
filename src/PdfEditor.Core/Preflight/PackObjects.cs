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

    public static byte[] Apply(byte[] pdf) => Apply(pdf, null, null) ?? pdf;

    /// <summary>
    /// Packs the file with some objects given new contents (a whole object body: a dictionary, or
    /// a dictionary with its stream) and some left out. Returns null when the file could not be
    /// packed, since the replacements would then be lost.
    /// </summary>
    public static byte[]? Apply(byte[] pdf, IReadOnlyDictionary<int, byte[]>? replace, IReadOnlySet<int>? drop)
    {
        try
        {
            var pass = new Pass(pdf) { Replace = replace, Drop = drop };
            if (replace is not null || drop is not null) return pass.Run();
            var packed = pass.Run();
            // Smaller is the usual reason to use the packed file; a deleted page that PDFium left
            // in the file is another, whatever the size.
            return packed is not null && (packed.Length < pdf.Length || pass.LeftPagesBehind) ? packed : pdf;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            return replace is null && drop is null ? pdf : null;
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

        /// <summary>Objects to write with other contents than the file has, and objects to leave out.</summary>
        public IReadOnlyDictionary<int, byte[]>? Replace { get; init; }
        public IReadOnlySet<int>? Drop { get; init; }

        /// <summary>True when the file held deleted pages, or bookmarks leading to them, that were taken out.</summary>
        public bool LeftPagesBehind { get; private set; }

        private string Text((int Start, int End) span) => Encoding.Latin1.GetString(d, span.Start, span.End - span.Start);

        /// <summary>The object a value refers to when it is just "12 0 R"; otherwise null.</summary>
        private int? Ref((int Start, int End) span)
        {
            var parts = Text(span).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3 && parts[2] == "R" && int.TryParse(parts[0], out int n) && n >= 0 ? n : null;
        }

        private Dictionary<string, (int Start, int End)>? Dict(Obj? o) => o is not null && At(o.ValueStart, "<<") ? Entries(o.ValueStart, out _) : null;

        /// <summary>A value, or what it refers to when it is a reference to something that is not a stream.</summary>
        private (int Start, int End)? Resolved((int Start, int End) span, Dictionary<int, Obj> objects) =>
            Ref(span) is { } n ? (objects.TryGetValue(n, out var o) && !o.IsStream ? (o.ValueStart, o.ValueEnd) : null) : span;

        private List<int> RefsIn((int Start, int End) span)
        {
            _refs.Clear();
            if (d[span.Start] is (byte)'[' or (byte)'<') Value(span.Start);
            return _refs.ToList();
        }

        private static byte[] Rebuilt(Dictionary<string, (int Start, int End)> entries, Dictionary<string, string?> changes, byte[] d)
        {
            var sb = new StringBuilder("<<");
            foreach (var (key, (start, end)) in entries)
            {
                if (changes.ContainsKey(key)) continue;
                sb.Append(key).Append(' ').Append(Encoding.Latin1.GetString(d, start, end - start)).Append(' ');
            }
            foreach (var (key, value) in changes) if (value is not null) sb.Append(key).Append(' ').Append(value).Append(' ');
            return Encoding.Latin1.GetBytes(sb.Append(">>").ToString());
        }

        /// <summary>
        /// Pages the file still holds though they are no longer in the document's page tree. PDFium
        /// takes a deleted page out of the tree, but anything that still points at it (a bookmark,
        /// a link, a named destination) keeps the page and all its content in the saved file.
        /// </summary>
        private HashSet<int> PagesOutsideTheTree(Dictionary<int, Obj> objects, Dictionary<string, (int Start, int End)>? root)
        {
            var dead = new HashSet<int>();
            if (root is null || !root.TryGetValue("/Pages", out var pagesSpan) || Ref(pagesSpan) is not { } top) return dead;
            var inTree = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(top);
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                if (!inTree.Add(n) || Dict(objects.GetValueOrDefault(n)) is not { } node) continue;
                if (node.TryGetValue("/Kids", out var kids) && Resolved(kids, objects) is { } list) foreach (int kid in RefsIn(list)) stack.Push(kid);
            }
            if (inTree.Count < 2) return dead; // no pages found: the tree was not understood, so judge nothing
            foreach (var o in objects.Values)
                if (!o.IsStream && !inTree.Contains(o.Number) && Dict(o) is { } entries && entries.TryGetValue("/Type", out var type) && Text(type) == "/Page")
                    dead.Add(o.Number);
            return dead;
        }

        /// <summary>
        /// Takes bookmarks that led to a deleted page out of the outline. One that has bookmarks of
        /// its own beneath it stays as a heading and only loses where it led. Returns the outline
        /// items that are gone; the rewritten ones are put in <paramref name="rewritten"/>.
        /// </summary>
        private HashSet<int> UnhookBookmarks(Dictionary<int, Obj> objects, Dictionary<string, (int Start, int End)> root, HashSet<int> dead, Dictionary<int, byte[]> rewritten)
        {
            var gone = new HashSet<int>();
            if (!root.TryGetValue("/Outlines", out var outlineSpan) || Ref(outlineSpan) is not { } top) return gone;
            var entries = new Dictionary<int, Dictionary<string, (int Start, int End)>>();
            var kids = new Dictionary<int, List<int>>();
            var parent = new Dictionary<int, int>();
            var order = new List<int>(); // parents before children
            var queue = new Queue<int>();
            queue.Enqueue(top);
            while (queue.Count > 0 && entries.Count < 200000)
            {
                int n = queue.Dequeue();
                if (entries.ContainsKey(n) || Dict(objects.GetValueOrDefault(n)) is not { } item) continue;
                entries[n] = item;
                order.Add(n);
                var list = kids[n] = [];
                var seen = new HashSet<int>();
                for (int? c = item.TryGetValue("/First", out var first) ? Ref(first) : null; c is { } child && seen.Add(child) && !entries.ContainsKey(child); )
                {
                    if (Dict(objects.GetValueOrDefault(child)) is not { } childEntries) break;
                    list.Add(child);
                    parent[child] = n;
                    queue.Enqueue(child);
                    c = childEntries.TryGetValue("/Next", out var nextSpan) ? Ref(nextSpan) : null;
                }
            }

            bool LeadsToDeadPage(Dictionary<string, (int Start, int End)> item)
            {
                (int Start, int End)? dest = item.TryGetValue("/Dest", out var direct) ? Resolved(direct, objects) : null;
                if (dest is null && item.TryGetValue("/A", out var action) && Resolved(action, objects) is { } a && At(a.Start, "<<")
                    && Entries(a.Start, out _).TryGetValue("/D", out var target)) dest = Resolved(target, objects);
                return dest is { } array && d[array.Start] == '[' && RefsIn(array) is { Count: > 0 } refs && dead.Contains(refs[0]);
            }

            var counts = entries.ToDictionary(e => e.Key, e => e.Value.TryGetValue("/Count", out var c) && int.TryParse(Text(c), out int v) ? v : (int?)null);
            var changes = new Dictionary<int, Dictionary<string, string?>>();
            Dictionary<string, string?> Change(int n) => changes.TryGetValue(n, out var c) ? c : changes[n] = [];
            var relink = new HashSet<int>();
            foreach (int n in Enumerable.Reverse(order)) // children before parents
            {
                if (n == top || !LeadsToDeadPage(entries[n])) continue;
                if (kids[n].Count > 0)
                {
                    Change(n)["/Dest"] = null;
                    Change(n)["/A"] = null;
                    continue;
                }
                gone.Add(n);
                int up = parent[n];
                kids[up].Remove(n);
                relink.Add(up);
                // One fewer bookmark showing beneath each open ancestor, or hidden beneath the first closed one.
                for (int a = up; ; a = parent[a])
                {
                    if (counts[a] is not { } count) break;
                    counts[a] = count > 0 ? count - 1 : count + 1;
                    Change(a)["/Count"] = counts[a] == 0 ? null : counts[a]!.Value.ToString();
                    if (count < 0 || !parent.ContainsKey(a)) break;
                }
            }
            foreach (int up in relink)
            {
                var list = kids[up];
                Change(up)["/First"] = list.Count > 0 ? $"{list[0]} 0 R" : null;
                Change(up)["/Last"] = list.Count > 0 ? $"{list[^1]} 0 R" : null;
                if (list.Count == 0) Change(up)["/Count"] = null;
                for (int i = 0; i < list.Count; i++)
                {
                    Change(list[i])["/Prev"] = i > 0 ? $"{list[i - 1]} 0 R" : null;
                    Change(list[i])["/Next"] = i + 1 < list.Count ? $"{list[i + 1]} 0 R" : null;
                }
            }
            foreach (var (n, change) in changes) if (!gone.Contains(n)) rewritten[n] = Rebuilt(entries[n], change, d);
            return gone;
        }

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

            // Pages deleted from the document but still in the file, and the bookmarks that led to them.
            var rewritten = new Dictionary<int, byte[]>();
            var rootEntries = Ref(trailer["/Root"]) is { } rootNumber ? Dict(objects.GetValueOrDefault(rootNumber)) : null;
            var dead = PagesOutsideTheTree(objects, rootEntries);
            bool pagesLeft = dead.Count > 0;
            // PDFium itself blanks a deleted page: its object stays, holding nothing ("null"). Whatever
            // led there leads nowhere now, and is treated the same way.
            foreach (var o in objects.Values)
                if (!o.IsStream && o.ValueEnd - o.ValueStart == 4 && At(o.ValueStart, "null")) dead.Add(o.Number);
            var gone = dead.Count > 0 && rootEntries is not null ? UnhookBookmarks(objects, rootEntries, dead, rewritten) : [];
            LeftPagesBehind = pagesLeft || gone.Count > 0 || rewritten.Count > 0;
            if (Drop is not null) gone.UnionWith(Drop);
            if (Replace is not null) foreach (var (number, body) in Replace) rewritten[number] = body;

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
                if (dead.Contains(n) || gone.Contains(n)) continue; // anything still pointing at these now points at nothing
                if (!keep.Add(n) || !objects.TryGetValue(n, out var o)) continue;
                foreach (int r in o.Refs) if (!gone.Contains(r)) toVisit.Push(r);
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
                if (rewritten.TryGetValue(o.Number, out var replaced)) output.Write(replaced);
                else output.Write(d, o.ValueStart, o.End - o.ValueStart);
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
                    if (rewritten.TryGetValue(group[k].Number, out var changed)) body.Write(changed);
                    else body.Write(d, group[k].ValueStart, group[k].ValueEnd - group[k].ValueStart);
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
