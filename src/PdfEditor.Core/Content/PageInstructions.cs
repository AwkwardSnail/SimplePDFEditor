using System.Globalization;
using System.Text;
using PdfEditor.Core.Interop;
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
/// <param name="OperatorAt">Where the painting instruction itself starts (after its operands).</param>
/// <param name="Space">The coordinate space in force when it is painted: from that space to the page's own.</param>
/// <param name="ClipsToo">The path is also used to clip what follows, so it cannot simply be taken out or boxed in.</param>
/// <param name="Clip">What the item is clipped to where it is drawn.</param>
internal sealed record DrawnItem(ItemKind Kind, int Start, int End, string Operator, int OperatorAt, Affine Space, bool ClipsToo, Clip Clip);

/// <summary>
/// The clip in force at some point of a page's instructions: nothing (no box, not complex), an
/// upright box in the page's coordinates, or some other shape that is not followed here.
/// </summary>
internal readonly record struct Clip(PdfRect? Box, bool Complex)
{
    /// <summary>Whether something covering the given box (in the page's coordinates) is sure to be left whole.</summary>
    public bool LeavesWhole(double left, double bottom, double right, double top, double slack = 0.5) =>
        !Complex && (Box is not { } b || left >= b.X - slack && right <= b.Right + slack && bottom >= b.Y - slack && top <= b.Bottom + slack);
}

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
    /// <summary>The page's object in the file, and the objects that hold its content streams, in order.</summary>
    public required int PageObject { get; init; }
    public required IReadOnlyList<int> ContentObjects { get; init; }
    /// <summary>The page's dictionary as read from the file.</summary>
    public required Node PageNode { get; init; }
    public required byte[] PageSource { get; init; }

    /// <summary>Reads a page's instructions, or returns null (with the reason) when they cannot be read reliably.</summary>
    public static PageInstructions? Read(PdfFile file, int pageIndex, out string? problem)
    {
        problem = null;
        try
        {
            var page = FindPage(file, pageIndex);
            if (page is null) { problem = "the page was not found in the file's page tree"; return null; }
            var (pageNode, pageSource, resources, resourcesSource, pageObject) = page.Value;
            var contentObjects = new List<int>();

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
                contentObjects.Add(reference.RefNumber);
                data.Write(unpacked);
            }
            _ = contentsSource;

            var bytes = data.ToArray();
            var names = new ResourceNames(file, resources, resourcesSource);
            return new PageInstructions
            {
                Data = bytes, StreamStarts = starts, Items = new Scanner(bytes, names).Run(),
                PageObject = pageObject, ContentObjects = contentObjects, PageNode = pageNode, PageSource = pageSource,
            };
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            problem = e.Message;
            return null;
        }
    }

    /// <summary>The page with the given index, with the resources it uses (its own, or those it inherits).</summary>
    private static (Node Page, byte[] Source, Node? Resources, byte[]? ResourcesSource, int Number)? FindPage(PdfFile file, int pageIndex)
    {
        var (root, rootSource) = file.Resolve(file.Trailer?["Root"], file.Bytes);
        if (root is null || rootSource is null) return null;
        int seen = 0;
        return Walk(root["Pages"], rootSource, null, null, 0);

        (Node, byte[], Node?, byte[]?, int)? Walk(Node? reference, byte[] source, Node? inherited, byte[]? inheritedSource, int depth)
        {
            var (node, nodeSource) = file.Resolve(reference, source);
            if (node is not { Kind: Kind.Dict } || nodeSource is null || depth > 64) return null;
            if (node["Resources"] is { } own) (inherited, inheritedSource) = file.Resolve(own, nodeSource);
            var (kids, kidsSource) = file.Resolve(node["Kids"], nodeSource);
            if (kids is not { Kind: Kind.Array } || kidsSource is null)
                return seen++ == pageIndex ? (node, nodeSource, inherited, inheritedSource, reference is { Kind: Kind.Ref } ? reference.RefNumber : -1) : null; // a page
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
        private bool _lastIsOpenMove, _clipPending;
        // Numbers waiting for their operator, and the coordinate space (with those saved by "q").
        private readonly List<double> _numbers = [];
        private Affine _space = Affine.Identity;
        private readonly Stack<(Affine Space, Clip Clip)> _saved = new();
        // The clip in force; whether the path being built is one plain rectangle (and which); text that clips.
        private Clip _clip;
        private bool _onlyARectangle, _textClips;
        private int _pathSteps, _textMode;
        private readonly double[] _rectangle = new double[4];

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
                    if (number)
                    {
                        Operand(start);
                        _numbers.Add(double.TryParse(Encoding.ASCII.GetString(d, start, _p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0);
                    }
                    else
                    {
                        Operator(Encoding.Latin1.GetString(d, start, _p - start), start);
                        _operandsStart = -1;
                        _lastName = null;
                        _numbers.Clear();
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

        private void Add(ItemKind kind, int start, string op, int operatorAt, bool clipsToo = false) =>
            _items.Add(new DrawnItem(kind, start, _p, op, operatorAt, _space, clipsToo, _clip));

        /// <summary>The path just finished becomes part of the clip, for everything up to the matching "Q".</summary>
        private void ClipToPath()
        {
            bool upright = Math.Abs(_space.B) < 1e-9 && Math.Abs(_space.C) < 1e-9 || Math.Abs(_space.A) < 1e-9 && Math.Abs(_space.D) < 1e-9;
            if (!_onlyARectangle || !upright) { _clip = _clip with { Complex = true }; return; }
            var box = _space.MapBounds(_rectangle[0], _rectangle[1], _rectangle[0] + _rectangle[2], _rectangle[1] + _rectangle[3]);
            if (_clip.Box is { } old)
            {
                double l = Math.Max(old.X, box.X), b = Math.Max(old.Y, box.Y), r = Math.Min(old.Right, box.Right), t = Math.Min(old.Bottom, box.Bottom);
                box = new PdfRect(l, b, Math.Max(0, r - l), Math.Max(0, t - b));
            }
            _clip = _clip with { Box = box };
        }

        private void Operator(string op, int at)
        {
            int start = _operandsStart >= 0 ? _operandsStart : at;
            switch (op)
            {
                // ---- the coordinate space
                case "q": _saved.Push((_space, _clip)); break;
                case "Q": if (_saved.Count > 0) (_space, _clip) = _saved.Pop(); break;
                case "cm":
                    if (_numbers.Count >= 6)
                    {
                        int n = _numbers.Count;
                        _space = Affine.Concat(new Affine(_numbers[n - 6], _numbers[n - 5], _numbers[n - 4], _numbers[n - 3], _numbers[n - 2], _numbers[n - 1]), _space);
                    }
                    break;
                case "W" or "W*": _clipPending = true; break;

                // ---- building a path
                case "m":
                    _onlyARectangle = false;
                    _pathSteps++;
                    if (_pathStart < 0) _pathStart = start;
                    if (!_lastIsOpenMove) _points++;
                    _lastIsOpenMove = true;
                    break;
                case "l" or "c" or "v" or "y":
                    _onlyARectangle = false;
                    _pathSteps++;
                    if (_points == 0) break; // nothing to continue from: ignored
                    _points += op == "l" ? 1 : 3;
                    _lastIsOpenMove = false;
                    break;
                case "re":
                    _onlyARectangle = _pathSteps == 0 && _numbers.Count >= 4;
                    if (_onlyARectangle) for (int i = 0; i < 4; i++) _rectangle[i] = _numbers[_numbers.Count - 4 + i];
                    _pathSteps++;
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
                    if (op != "n" && points >= 2) Add(ItemKind.Path, _pathStart >= 0 ? _pathStart : start, op, at, _clipPending);
                    if (_clipPending && _pathSteps > 0) ClipToPath(); // the item itself is drawn under the clip as it was before
                    (_pathStart, _points, _lastIsOpenMove, _clipPending, _pathSteps, _onlyARectangle) = (-1, 0, false, false, 0, false);
                    break;
                }

                // ---- text
                case "Tj" or "'" or "\"":
                    if (!_lastStringEmpty) { Add(ItemKind.Text, start, op, at); if (_textMode >= 4) _textClips = true; }
                    break;
                case "TJ":
                    if (_arrayHasText) { Add(ItemKind.Text, start, op, at); if (_textMode >= 4) _textClips = true; }
                    break;
                case "Tr":
                    if (_numbers.Count >= 1) _textMode = (int)_numbers[^1];
                    break;
                case "ET":
                    // Text drawn in a clipping mode becomes part of the clip when its block ends.
                    if (_textClips) _clip = _clip with { Complex = true };
                    _textClips = false;
                    break;

                // ---- pictures, groups, gradients
                case "Do":
                    if (_lastName is not null && names.XObject(_lastName) is { } kind) Add(kind, start, op, at);
                    break;
                case "sh":
                    if (_lastName is not null && names.HasShading(_lastName)) Add(ItemKind.Shading, start, op, at);
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
                Add(ItemKind.Image, at, "BI", at);
                return;
            }
            _p = d.Length;
        }
    }
}
