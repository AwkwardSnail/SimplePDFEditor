using System.Text;
using PdfEditor.Core.Preflight;
using static PdfEditor.Core.Preflight.PdfFile;

namespace PdfEditor.Core.Content;

/// <summary>The kinds of item a page's instructions draw, numbered as PDFium numbers its page objects.</summary>
internal enum ItemKind { Text = 1, Path = 2, Image = 3, Shading = 4, Group = 5 }

/// <summary>
/// One item drawn by a page's instructions, and where in those instructions it is drawn:
/// from <see cref="Start"/> (the first instruction that belongs to it alone) to <see cref="End"/>
/// (just past the instruction that paints it).
/// </summary>
internal sealed record DrawnItem(ItemKind Kind, int Start, int End, string Operator);

/// <summary>
/// A page's own drawing instructions (its content streams, unpacked and joined), read straight
/// from the file, with the items they draw in the order they are drawn.
/// <para>PDFium reads the same instructions into a list of page objects, and can write that list
/// back only in part (see the README on what it loses). Knowing which instructions draw which
/// item is what lets an edit change just those and leave the rest of the page byte for byte as
/// it was. The items here are meant to match PDFium's page objects one for one, in order.</para>
/// </summary>
internal sealed class PageInstructions
{
    public required byte[] Data { get; init; }
    public required IReadOnlyList<DrawnItem> Items { get; init; }
    /// <summary>Where each of the page's content streams starts in <see cref="Data"/>.</summary>
    public required IReadOnlyList<int> StreamStarts { get; init; }

    /// <summary>Reads a page's instructions, or returns null (with the reason) when they cannot be read reliably.</summary>
    public static PageInstructions? Read(PdfFile file, int pageIndex, out string? problem)
    {
        problem = null;
        try
        {
            var page = FindPage(file, pageIndex);
            if (page is null) { problem = "the page was not found in the file's page tree"; return null; }
            var (pageNode, pageSource, resources, resourcesSource) = page.Value;

            // The content: one stream, or several that are read as if joined.
            var data = new MemoryStream();
            var starts = new List<int>();
            var (contents, contentsSource) = file.Resolve(pageNode["Contents"], pageSource);
            var streams = new List<Node>();
            if (pageNode["Contents"] is { Kind: Kind.Ref } single && contents is { Kind: Kind.Dict }) streams.Add(single);
            else if (contents is { Kind: Kind.Array }) streams.AddRange(contents.Items);
            foreach (var reference in streams)
            {
                if (reference.Kind != Kind.Ref || !file.Objects.TryGetValue(reference.RefNumber, out var stream) || stream.StreamStart < 0) continue;
                byte[]? unpacked = file.StreamData(stream.Value, stream.Source, stream.StreamStart);
                if (unpacked is null) { problem = "a content stream is packed in a way that is not read here"; return null; }
                if (data.Length > 0) data.WriteByte((byte)'\n');
                starts.Add((int)data.Length);
                data.Write(unpacked);
            }
            _ = contentsSource;

            var bytes = data.ToArray();
            var names = new ResourceNames(file, resources, resourcesSource);
            return new PageInstructions { Data = bytes, StreamStarts = starts, Items = new Scanner(bytes, names).Run() };
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            problem = e.Message;
            return null;
        }
    }

    /// <summary>The page with the given index, with the resources it uses (its own, or those it inherits).</summary>
    private static (Node Page, byte[] Source, Node? Resources, byte[]? ResourcesSource)? FindPage(PdfFile file, int pageIndex)
    {
        var (root, rootSource) = file.Resolve(file.Trailer?["Root"], file.Bytes);
        if (root is null || rootSource is null) return null;
        int seen = 0;
        return Walk(root["Pages"], rootSource, null, null, 0);

        (Node, byte[], Node?, byte[]?)? Walk(Node? reference, byte[] source, Node? inherited, byte[]? inheritedSource, int depth)
        {
            var (node, nodeSource) = file.Resolve(reference, source);
            if (node is not { Kind: Kind.Dict } || nodeSource is null || depth > 64) return null;
            if (node["Resources"] is { } own) (inherited, inheritedSource) = file.Resolve(own, nodeSource);
            var (kids, kidsSource) = file.Resolve(node["Kids"], nodeSource);
            if (kids is not { Kind: Kind.Array } || kidsSource is null)
                return seen++ == pageIndex ? (node, nodeSource, inherited, inheritedSource) : null; // a page
            foreach (var kid in kids.Items)
                if (Walk(kid, kidsSource, inherited, inheritedSource, depth + 1) is { } found) return found;
            return null;
        }
    }

    /// <summary>What the names used in the instructions stand for: which are pictures, groups and gradients.</summary>
    private sealed class ResourceNames(PdfFile file, Node? resources, byte[]? source)
    {
        private readonly Dictionary<string, ItemKind?> _xobjects = [];

        public ItemKind? XObject(string name)
        {
            if (_xobjects.TryGetValue(name, out var known)) return known;
            ItemKind? kind = null;
            if (resources is not null && source is not null)
            {
                var (group, groupSource) = file.Resolve(resources["XObject"], source);
                if (group?[name] is { } entry && groupSource is not null)
                {
                    var (dict, _) = file.Resolve(entry, groupSource);
                    kind = dict?["Subtype"]?.Name switch { "Image" => ItemKind.Image, "Form" => ItemKind.Group, _ => null };
                }
            }
            return _xobjects[name] = kind;
        }

        public bool HasShading(string name)
        {
            if (resources is null || source is null) return false;
            var (group, _) = file.Resolve(resources["Shading"], source);
            return group?[name] is not null;
        }
    }

    /// <summary>Goes through the instructions one by one and notes each that draws an item, as PDFium does.</summary>
    private sealed class Scanner(byte[] d, ResourceNames names)
    {
        private int _p;
        private readonly List<DrawnItem> _items = [];
        // Operands waiting for their operator: where each starts, and what the last one was.
        private int _operandsStart = -1;
        private string? _lastName;
        private bool _lastStringEmpty, _arrayHasText;
        // The path being built.
        private int _pathStart = -1, _points;
        private bool _lastIsOpenMove;

        private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        public List<DrawnItem> Run()
        {
            while (true)
            {
                SkipSpace();
                if (_p >= d.Length) break;
                int start = _p;
                byte b = d[_p];
                if (b == '/') { Operand(start); _p++; int s = _p; while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++; _lastName = Name(s, _p); }
                else if (b == '(') { Operand(start); _lastStringEmpty = Literal(); }
                else if (b == '<' && _p + 1 < d.Length && d[_p + 1] == '<') { Operand(start); Dictionary(); }
                else if (b == '<') { Operand(start); _lastStringEmpty = Hex(); }
                else if (b == '[') { Operand(start); Array(); }
                else if (b == ']' || b == '>' || b == ')' || b == '{' || b == '}') _p++; // stray: PDFium steps over these too
                else
                {
                    while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++;
                    if (_p == start) { _p++; continue; }
                    byte first = d[start];
                    bool number = first is (byte)'+' or (byte)'-' or (byte)'.' || first is >= (byte)'0' and <= (byte)'9';
                    if (number) Operand(start);
                    else
                    {
                        Operator(Encoding.Latin1.GetString(d, start, _p - start), start);
                        _operandsStart = -1;
                        _lastName = null;
                    }
                }
            }
            return _items;
        }

        private void Operand(int start) { if (_operandsStart < 0) _operandsStart = start; }

        private string Name(int start, int end)
        {
            // "#20" in a name is a character given by its code.
            var sb = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                if (d[i] == '#' && i + 2 < end && Uri.IsHexDigit((char)d[i + 1]) && Uri.IsHexDigit((char)d[i + 2]))
                {
                    sb.Append((char)Convert.ToInt32(Encoding.ASCII.GetString(d, i + 1, 2), 16));
                    i += 2;
                }
                else sb.Append((char)d[i]);
            }
            return sb.ToString();
        }

        private void SkipSpace()
        {
            while (_p < d.Length)
            {
                if (IsSpace(d[_p])) _p++;
                else if (d[_p] == '%') { while (_p < d.Length && d[_p] is not (10 or 13)) _p++; }
                else break;
            }
        }

        /// <summary>Steps over "(...)" and says whether it was empty.</summary>
        private bool Literal()
        {
            int depth = 0, start = _p;
            while (_p < d.Length)
            {
                byte c = d[_p++];
                if (c == '\\') _p++;
                else if (c == '(') depth++;
                else if (c == ')' && --depth == 0) break;
            }
            return _p - start <= 2;
        }

        /// <summary>Steps over a string written as hexadecimal digits, and says whether it was empty.</summary>
        private bool Hex()
        {
            _p++;
            bool any = false;
            while (_p < d.Length && d[_p] != '>') { if (Uri.IsHexDigit((char)d[_p])) any = true; _p++; }
            _p++;
            return !any;
        }

        private void Array()
        {
            _p++;
            _arrayHasText = false;
            while (true)
            {
                SkipSpace();
                if (_p >= d.Length) return;
                byte b = d[_p];
                if (b == ']') { _p++; return; }
                if (b == '(') { if (!Literal()) _arrayHasText = true; }
                else if (b == '<' && _p + 1 < d.Length && d[_p + 1] == '<') Dictionary();
                else if (b == '<') { if (!Hex()) _arrayHasText = true; }
                else if (b == '[') { bool outer = _arrayHasText; Array(); _arrayHasText = outer; }
                else if (b == '/') { _p++; while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++; }
                else { int s = _p; while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++; if (_p == s) _p++; }
            }
        }

        private void Dictionary()
        {
            _p += 2;
            while (true)
            {
                SkipSpace();
                if (_p >= d.Length) return;
                byte b = d[_p];
                if (b == '>' && _p + 1 < d.Length && d[_p + 1] == '>') { _p += 2; return; }
                if (b == '(') Literal();
                else if (b == '<' && _p + 1 < d.Length && d[_p + 1] == '<') Dictionary();
                else if (b == '<') Hex();
                else if (b == '[') Array();
                else if (b == '/') { _p++; while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++; }
                else { int s = _p; while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++; if (_p == s) _p++; }
            }
        }

        private void Add(ItemKind kind, int start, string op) => _items.Add(new DrawnItem(kind, start, _p, op));

        private void Operator(string op, int at)
        {
            int start = _operandsStart >= 0 ? _operandsStart : at;
            switch (op)
            {
                // ---- building a path
                case "m":
                    if (_pathStart < 0) _pathStart = start;
                    if (!_lastIsOpenMove) _points++;
                    _lastIsOpenMove = true;
                    break;
                case "l" or "c" or "v" or "y":
                    if (_points == 0) break; // nothing to continue from: ignored
                    _points += op == "l" ? 1 : 3;
                    _lastIsOpenMove = false;
                    break;
                case "re":
                    if (_pathStart < 0) _pathStart = start;
                    _points += 4;
                    _lastIsOpenMove = false;
                    break;
                case "h":
                    if (_points > 0 && !_lastIsOpenMove) _points++; // may add the closing line; either way more than a lone point
                    break;

                // ---- painting it
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n":
                {
                    int points = _lastIsOpenMove ? _points - 1 : _points; // a move at the very end draws nothing
                    if (op != "n" && points >= 2) Add(ItemKind.Path, _pathStart >= 0 ? _pathStart : start, op);
                    (_pathStart, _points, _lastIsOpenMove) = (-1, 0, false);
                    break;
                }

                // ---- text
                case "Tj" or "'" or "\"":
                    if (!_lastStringEmpty) Add(ItemKind.Text, start, op);
                    break;
                case "TJ":
                    if (_arrayHasText) Add(ItemKind.Text, start, op);
                    break;

                // ---- pictures, groups, gradients
                case "Do":
                    if (_lastName is not null && names.XObject(_lastName) is { } kind) Add(kind, start, op);
                    break;
                case "sh":
                    if (_lastName is not null && names.HasShading(_lastName)) Add(ItemKind.Shading, start, op);
                    break;
                case "BI":
                    InlineImage(at);
                    break;
            }
            _lastStringEmpty = true;
            _arrayHasText = false;
        }

        /// <summary>A small picture written into the instructions themselves: "BI" settings "ID" data "EI".</summary>
        private void InlineImage(int at)
        {
            // The settings, up to ID.
            while (true)
            {
                SkipSpace();
                if (_p >= d.Length) return;
                int s = _p;
                byte b = d[_p];
                if (b == '(') Literal();
                else if (b == '<' && _p + 1 < d.Length && d[_p + 1] == '<') Dictionary();
                else if (b == '<') Hex();
                else if (b == '[') Array();
                else if (b == '/') { _p++; while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++; }
                else
                {
                    while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++;
                    if (_p == s) { _p++; continue; }
                    if (_p - s == 2 && d[s] == 'I' && d[s + 1] == 'D') break;
                }
            }
            if (_p < d.Length && IsSpace(d[_p])) _p++; // the one space after ID
            // The data, up to an EI that stands alone and is followed by something that reads as instructions.
            for (int i = _p; i + 1 < d.Length; i++)
            {
                if (d[i] != 'E' || d[i + 1] != 'I') continue;
                if (i > 0 && !IsSpace(d[i - 1])) continue;
                if (i + 2 < d.Length && !IsSpace(d[i + 2]) && !IsDelimiter(d[i + 2])) continue;
                _p = i + 2;
                Add(ItemKind.Image, at, "BI");
                return;
            }
            _p = d.Length;
        }
    }
}
