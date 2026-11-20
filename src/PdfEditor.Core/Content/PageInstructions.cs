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
internal sealed record DrawnItem(ItemKind Kind, int Start, int End, string Operator, int OperatorAt, Affine Space, bool ClipsToo, Clip Clip)
{
    /// <summary>For a piece of text: the state of the text machinery where it is shown.</summary>
    public TextState? Text { get; init; }
    /// <summary>Every setting in force where the item is drawn, as the instructions that made it so.</summary>
    public DrawState State { get; init; } = DrawState.Initial;
    /// <summary>Drawn between the instructions that open and close a block of text.</summary>
    public bool InTextBlock { get; init; }
    /// <summary>The saved-state sections ("q" to "Q") the item is drawn inside, outermost first.</summary>
    public IReadOnlyList<Section> Sections { get; init; } = [];
}

/// <summary>
/// A stretch of instructions between a "q" and its "Q": whatever is set inside it (space, clip,
/// colours) is undone at the end, so the settings in force just before it and just after it are the same.
/// </summary>
internal sealed class Section(int start, DrawState state, Affine space, Clip clip, bool inTextBlock)
{
    public int Start { get; } = start;            // where its "q" is
    public int End { get; set; } = -1;            // just past its "Q"
    public DrawState State { get; } = state;      // what is in force outside it
    public Affine Space { get; } = space;
    public Clip Clip { get; } = clip;
    public bool InTextBlock { get; } = inTextBlock;
}

/// <summary>One step of the clip in force: a path (with the instruction that makes it a clip), and the space it was given in.</summary>
internal sealed record ClipStep(string Path, Affine Space);

/// <summary>
/// The drawing settings in force at some point of a page, each kept as the instruction that set
/// it (null where nothing has, so the default holds). With these an item can be drawn again
/// somewhere else on the page looking exactly as it did: the settings are simply given again.
/// </summary>
internal sealed record DrawState(
    string? FillSpace, string? FillColour, string? StrokeSpace, string? StrokeColour,
    string? LineWidth, string? Cap, string? Join, string? Mitre, string? Dash, string? Intent, string? Flatness,
    string? Font, string? LetterSpace, string? WordSpace, string? Stretch, string? Leading, string? Mode, string? Rise,
    IReadOnlyList<string> Settings, IReadOnlyList<ClipStep> Clips, bool CannotBeRepeated)
{
    public static readonly DrawState Initial = new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, [], [], false);
}

/// <summary>
/// What a piece of text depends on besides its own instruction. Pieces of text in one block share
/// a writing position: each starts where the one before left off unless the position is set anew.
/// </summary>
/// <param name="LineStart">The start of the current line (the "line matrix"): what moves to a new line are measured from.</param>
/// <param name="FontSize">The size the font is set at.</param>
/// <param name="Stretch">Horizontal stretching, 1 for none.</param>
/// <param name="Rise">How far the text is raised above its line.</param>
/// <param name="Mode">How the text is drawn; 4 and above also clip what follows.</param>
/// <param name="StringAt">Where the text itself starts among the instruction's operands.</param>
/// <param name="NextFollowsOn">The next piece of text starts where this one ends, with nothing setting the position in between.</param>
/// <param name="ShiftBeforeNext">How far instructions between this piece and the next shift the writing position along the line.</param>
internal sealed record TextState(Affine LineStart, double FontSize, double Stretch, double Rise, int Mode, int StringAt, bool NextFollowsOn = false, double ShiftBeforeNext = 0)
{
    /// <summary>The distance between lines in force (what the quote instructions move down by before showing).</summary>
    public double Leading { get; init; }
}

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
        private readonly Stack<(Affine Space, Clip Clip, double FontSize, double Stretch, double Rise, double Leading, int TextMode, DrawState State)> _saved = new();
        private DrawState _state = DrawState.Initial;
        private bool _inText;
        private readonly List<Section> _open = [];
        private Section[] _sections = [];
        // The clip in force; whether the path being built is one plain rectangle (and which); text that clips.
        private Clip _clip;
        private bool _onlyARectangle, _textClips;
        private int _pathSteps, _textMode;
        private readonly double[] _rectangle = new double[4];
        // Text: the start of the line, the font's size, stretch, rise and line spacing; and the last piece shown,
        // while nothing has set the position anew since.
        private Affine _lineStart = Affine.Identity;
        private double _fontSize, _stretch = 1, _rise, _leading, _shift;
        private int _lastShown = -1, _stringAt = -1;
        private double _arraySum; // the numbers of the array just read, added up

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
                else if (b == '(') { Operand(start); _stringAt = start; _lastStringEmpty = Literal(); }
                else if (b == '<' && _p + 1 < d.Length && d[_p + 1] == '<') { Operand(start); Dictionary(); }
                else if (b == '<') { Operand(start); _stringAt = start; _lastStringEmpty = Hex(); }
                else if (b == '[') { Operand(start); _stringAt = start; Array(); }
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
            foreach (var section in _open) section.End = d.Length; // never closed: it runs to the end
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
            _arraySum = 0;
            while (true)
            {
                SkipSpace();
                if (_p >= d.Length) return;
                byte b = d[_p];
                if (b == ']') { _p++; return; }
                if (b == '(') { if (!Literal()) _arrayHasText = true; }
                else if (b == '<' && _p + 1 < d.Length && d[_p + 1] == '<') Dictionary();
                else if (b == '<') { if (!Hex()) _arrayHasText = true; }
                else if (b == '[') { bool outer = _arrayHasText; double sum = _arraySum; Array(); (_arrayHasText, _arraySum) = (outer, sum); }
                else if (b == '/') { _p++; while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++; }
                else
                {
                    int s = _p;
                    while (_p < d.Length && !IsSpace(d[_p]) && !IsDelimiter(d[_p])) _p++;
                    if (_p == s) _p++;
                    else if (double.TryParse(Encoding.ASCII.GetString(d, s, _p - s), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) _arraySum += number;
                }
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
            _items.Add(new DrawnItem(kind, start, _p, op, operatorAt, _space, clipsToo, _clip) { State = _state, InTextBlock = _inText, Sections = _sections });

        /// <summary>An instruction as it is written, operands and all.</summary>
        private string Written(int start) => Encoding.Latin1.GetString(d, start, _p - start);

        /// <summary>A piece of text is shown. The one before it, if nothing set the position since, is noted as followed on.</summary>
        private void Show(int start, string op, int operatorAt)
        {
            if (_lastShown >= 0)
            {
                var before = _items[_lastShown];
                _items[_lastShown] = before with { Text = before.Text! with { NextFollowsOn = true, ShiftBeforeNext = _shift } };
            }
            _items.Add(new DrawnItem(ItemKind.Text, start, _p, op, operatorAt, _space, false, _clip)
            {
                State = _state, InTextBlock = _inText, Sections = _sections,
                Text = new TextState(_lineStart, _fontSize, _stretch, _rise, _textMode, _stringAt >= start ? _stringAt : start) { Leading = _leading },
            });
            _lastShown = _items.Count - 1;
            _shift = 0;
            if (_textMode >= 4) _textClips = true;
        }

        private void NewLine(double x, double y)
        {
            _lineStart = Affine.Concat(new Affine(1, 0, 0, 1, x, y), _lineStart);
            _lastShown = -1;
        }

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
                // "q" saves the whole drawing state, the text settings among it; "Q" brings it back.
                case "q":
                    _saved.Push((_space, _clip, _fontSize, _stretch, _rise, _leading, _textMode, _state));
                    _open.Add(new Section(at, _state, _space, _clip, _inText));
                    _sections = [.. _open];
                    break;
                case "Q":
                    if (_saved.Count == 0) break;
                    (_space, _clip, _fontSize, _stretch, _rise, _leading, _textMode, _state) = _saved.Pop();
                    _open[^1].End = _p;
                    _open.RemoveAt(_open.Count - 1);
                    _sections = [.. _open];
                    break;

                // ---- settings, kept as written so they can be given again elsewhere
                case "g" or "rg" or "k" or "cs": _state = _state with { FillSpace = Written(start), FillColour = null }; break;
                case "G" or "RG" or "K" or "CS": _state = _state with { StrokeSpace = Written(start), StrokeColour = null }; break;
                case "sc" or "scn": _state = _state with { FillColour = Written(start) }; break;
                case "SC" or "SCN": _state = _state with { StrokeColour = Written(start) }; break;
                case "w": _state = _state with { LineWidth = Written(start) }; break;
                case "J": _state = _state with { Cap = Written(start) }; break;
                case "j": _state = _state with { Join = Written(start) }; break;
                case "M": _state = _state with { Mitre = Written(start) }; break;
                case "d": _state = _state with { Dash = Written(start) }; break;
                case "ri": _state = _state with { Intent = Written(start) }; break;
                case "i": _state = _state with { Flatness = Written(start) }; break;
                case "gs": _state = _state with { Settings = [.. _state.Settings, Written(start)] }; break;
                case "Tc": _state = _state with { LetterSpace = Written(start) }; break;
                case "Tw": _state = _state with { WordSpace = Written(start) }; break;
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
                    if (_clipPending && _pathSteps > 0)
                    {
                        ClipToPath(); // the item itself is drawn under the clip as it was before
                        // The path as written, ending in "n": clipping without painting.
                        if (_pathStart >= 0) _state = _state with { Clips = [.. _state.Clips, new ClipStep(Encoding.Latin1.GetString(d, _pathStart, at - _pathStart) + " n", _space)] };
                        else _state = _state with { CannotBeRepeated = true };
                    }
                    (_pathStart, _points, _lastIsOpenMove, _clipPending, _pathSteps, _onlyARectangle) = (-1, 0, false, false, 0, false);
                    break;
                }

                // ---- text
                case "Tj":
                    if (!_lastStringEmpty) Show(start, op, at);
                    break;
                case "'" or "\"":
                    NewLine(0, -_leading); // these move to the next line first, then show
                    if (op == "\"" && _numbers.Count >= 2) // and the double quote sets the spacing of words and letters
                        _state = _state with { WordSpace = $"{_numbers[^2].ToString("0.######", CultureInfo.InvariantCulture)} Tw", LetterSpace = $"{_numbers[^1].ToString("0.######", CultureInfo.InvariantCulture)} Tc" };
                    if (!_lastStringEmpty) Show(start, op, at);
                    break;
                case "TJ":
                    if (_arrayHasText) Show(start, op, at);
                    else _shift += -_arraySum / 1000 * _fontSize * _stretch; // numbers alone only shift the position
                    break;
                case "BT":
                    _lineStart = Affine.Identity;
                    (_lastShown, _shift) = (-1, 0);
                    _inText = true;
                    break;
                case "Td" or "TD":
                    if (_numbers.Count >= 2)
                    {
                        if (op == "TD")
                        {
                            _leading = -_numbers[^1];
                            _state = _state with { Leading = $"{_leading.ToString("0.######", CultureInfo.InvariantCulture)} TL" };
                        }
                        NewLine(_numbers[^2], _numbers[^1]);
                    }
                    break;
                case "T*": NewLine(0, -_leading); break;
                case "Tm":
                    if (_numbers.Count >= 6)
                    {
                        int n = _numbers.Count;
                        _lineStart = new Affine(_numbers[n - 6], _numbers[n - 5], _numbers[n - 4], _numbers[n - 3], _numbers[n - 2], _numbers[n - 1]);
                        _lastShown = -1;
                    }
                    break;
                case "Tf": if (_numbers.Count >= 1) _fontSize = _numbers[^1]; _state = _state with { Font = Written(start) }; break;
                case "Tz": if (_numbers.Count >= 1) _stretch = _numbers[^1] / 100; _state = _state with { Stretch = Written(start) }; break;
                case "Ts": if (_numbers.Count >= 1) _rise = _numbers[^1]; _state = _state with { Rise = Written(start) }; break;
                case "TL": if (_numbers.Count >= 1) _leading = _numbers[^1]; _state = _state with { Leading = Written(start) }; break;
                case "Tr":
                    if (_numbers.Count >= 1) _textMode = (int)_numbers[^1];
                    _state = _state with { Mode = Written(start) };
                    break;
                case "ET":
                    // Text drawn in a clipping mode becomes part of the clip when its block ends.
                    if (_textClips)
                    {
                        _clip = _clip with { Complex = true };
                        _state = _state with { CannotBeRepeated = true }; // a clip made of letters is not followed here
                    }
                    _textClips = false;
                    _lastShown = -1;
                    _inText = false;
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
