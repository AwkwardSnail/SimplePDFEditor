using System.Globalization;
using System.Text;

namespace PdfEditor.Core.Rtf;

/// <summary>
/// Reads RTF into an <see cref="RtfDocument"/>. Covers what word processors commonly write:
/// text formatting, paragraphs, lists, tables, PNG/JPEG pictures, page setup, Unicode and
/// code-page text. Headers, footers, footnotes, comments and drawing objects are skipped.
/// </summary>
internal sealed class RtfParser
{
    private enum Dest { Text, Skip, FontTable, ColorTable, Picture }

    private sealed class State
    {
        public Dest Dest = Dest.Text;
        public RtfCharFormat Char = new();
        public RtfParaFormat Para = new();
        public int UnicodeSkip = 1;
        public bool ListText;
        // Font table entry being read
        public int FontIndex = -1;
        public string FontFamily = "nil";
        public int FontCharset;
        // Picture being read
        public PictureInfo? Picture;
        public State Copy() => (State)MemberwiseClone();
    }

    private sealed class PictureInfo
    {
        public string Kind = "";
        public double WidthGoal, HeightGoal, Width, Height, ScaleX = 100, ScaleY = 100;
        public readonly List<byte> Bytes = [];
        public int PendingNibble = -1;
    }

    private readonly string _src;   // the file's bytes as Latin-1 characters
    private int _pos;
    private readonly RtfDocument _doc = new();
    private readonly Stack<State> _stack = new();
    private State _state = new();
    private readonly StringBuilder _text = new();
    private RtfCharFormat? _textFormat;
    private readonly List<byte> _pendingBytes = [];
    private int _skipChars;        // fallback characters still to skip after \u
    private bool _ignorableNext;   // \* seen: the next destination may be skipped if unknown
    private readonly StringBuilder _fontName = new();
    private (int R, int G, int B, bool Set) _color;
    private int _codePage = 1252;
    private int _defaultFont;

    private RtfParagraph _paragraph;
    // Tables
    private List<List<RtfParagraph>>? _cells;
    private List<RtfParagraph> _cell = [];
    private readonly List<double> _cellRights = [];
    private double _rowLeft, _rowGap;
    private double? _rowPadLeft, _rowPadRight;
    private bool _rowBorders;
    private bool _warnedNested;

    static RtfParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private RtfParser(byte[] data)
    {
        _src = Encoding.Latin1.GetString(data);
        _paragraph = new RtfParagraph { Format = _state.Para };
    }

    public static RtfDocument Parse(byte[] data)
    {
        var parser = new RtfParser(data);
        if (!parser._src.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal))
            throw new PdfException("This is not an RTF file.");
        parser.Run();
        return parser._doc;
    }

    // ------------------------------------------------------------------ main loop

    private void Run()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos++];
            switch (c)
            {
                case '{':
                    FlushBytes();
                    _stack.Push(_state);
                    _state = _state.Copy();
                    break;
                case '}':
                    FlushBytes();
                    EndGroup();
                    if (_stack.Count == 0) { FinishDocument(); return; }
                    _state = _stack.Pop();
                    break;
                case '\\':
                    ReadControl();
                    break;
                case '\r' or '\n':
                    break;
                default:
                    FlushBytes();
                    if (_skipChars > 0) { _skipChars--; break; }
                    Literal(c);
                    break;
            }
        }
        FinishDocument();
    }

    private void ReadControl()
    {
        if (_pos >= _src.Length) return;
        char c = _src[_pos];
        if (!char.IsAsciiLetter(c))
        {
            _pos++;
            if (c == '\'')
            {
                // A byte in the current code page.
                if (_pos + 2 <= _src.Length && byte.TryParse(_src.AsSpan(_pos, 2), NumberStyles.HexNumber, null, out byte b))
                {
                    _pos += 2;
                    if (_skipChars > 0) { _skipChars--; return; }
                    if (_state.Dest == Dest.Text) _pendingBytes.Add(b);
                    else if (_state.Dest == Dest.FontTable) _fontName.Append((char)b);
                }
                return;
            }
            FlushBytes();
            if (_skipChars > 0 && c is not ('{' or '}')) { _skipChars--; return; }
            switch (c)
            {
                case '*': _ignorableNext = true; break;
                case '~': Emit(' '); break;
                case '_': Emit('‑'); break;
                case '-': break; // optional hyphen
                case '\r' or '\n': Word("par", null); break;
                case '\\' or '{' or '}': Literal(c); break;
            }
            return;
        }

        int start = _pos;
        while (_pos < _src.Length && char.IsAsciiLetter(_src[_pos])) _pos++;
        string word = _src[start.._pos];
        int? param = null;
        int numStart = _pos;
        if (_pos < _src.Length && (_src[_pos] == '-' || char.IsAsciiDigit(_src[_pos])))
        {
            _pos++;
            while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos])) _pos++;
            if (int.TryParse(_src.AsSpan(numStart, _pos - numStart), out int n)) param = n;
        }
        if (_pos < _src.Length && _src[_pos] == ' ') _pos++;

        if (word == "bin" && param is > 0)
        {
            // Raw binary data: belongs to a picture, or is skipped.
            int length = Math.Min(param.Value, _src.Length - _pos);
            if (_state.Dest == Dest.Picture && _state.Picture is { } pic)
                for (int i = 0; i < length; i++) pic.Bytes.Add((byte)_src[_pos + i]);
            _pos += length;
            return;
        }
        FlushBytes();
        if (_skipChars > 0) { _skipChars--; return; }
        Word(word, param);
    }

    private void Literal(char c)
    {
        switch (_state.Dest)
        {
            case Dest.Text: Emit(c); break;
            case Dest.FontTable:
                if (c == ';') EndFont();
                else _fontName.Append(c);
                break;
            case Dest.ColorTable:
                if (c == ';') EndColor();
                break;
            case Dest.Picture when _state.Picture is { } pic && Uri.IsHexDigit(c):
                int nibble = Convert.ToInt32(c.ToString(), 16);
                if (pic.PendingNibble < 0) pic.PendingNibble = nibble;
                else { pic.Bytes.Add((byte)(pic.PendingNibble * 16 + nibble)); pic.PendingNibble = -1; }
                break;
        }
    }

    // ------------------------------------------------------------------ control words

    private void Word(string word, int? param)
    {
        bool ignorable = _ignorableNext;
        _ignorableNext = false;
        int n = param ?? 0;
        bool on = param is null || param != 0;
        double points = n / 20.0;

        if (_state.Dest == Dest.Skip) return;
        if (_state.Dest == Dest.FontTable) { FontTableWord(word, n); return; }
        if (_state.Dest == Dest.ColorTable) { ColorWord(word, n); return; }
        if (_state.Dest == Dest.Picture)
        {
            if (ignorable) _state.Dest = Dest.Skip; // picture properties, ids and the like
            else PictureWord(word, n);
            return;
        }

        switch (word)
        {
            // ---- destinations
            case "fonttbl": _state.Dest = Dest.FontTable; return;
            case "colortbl": _state.Dest = Dest.ColorTable; _color = default; return;
            case "pict":
                _state.Dest = Dest.Picture;
                _state.Picture = new PictureInfo();
                return;
            case "listtext" or "pntext": _state.ListText = true; return; // the list number or bullet, as text
            case "shppict" or "fldrslt" or "result": return; // their content is the visible text
            case "field" or "upr" or "ud": return;
            case "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf":
                if (!_doc.Warnings.Contains("Headers and footers were left out.")) _doc.Warnings.Add("Headers and footers were left out.");
                _state.Dest = Dest.Skip;
                return;
            case "stylesheet" or "info" or "fldinst" or "nonshppict" or "footnote" or "annotation" or "xe" or "tc" or "objdata"
                or "listtable" or "listoverridetable" or "revtbl" or "rsidtbl" or "generator" or "pn" or "shpinst" or "fldtype"
                or "datafield" or "background" or "atnid" or "atnauthor" or "template" or "bkmkstart" or "bkmkend" or "latentstyles":
                _state.Dest = Dest.Skip;
                return;

            // ---- document and section setup
            case "ansicpg": _codePage = n; return;
            case "deff": _defaultFont = n; _state.Char = _state.Char with { Font = n }; return;
            case "paperw" or "pgwsxn": if (n > 0) _doc.PageWidth = points; return;
            case "paperh" or "pghsxn": if (n > 0) _doc.PageHeight = points; return;
            case "margl" or "marglsxn": _doc.MarginLeft = points; return;
            case "margr" or "margrsxn": _doc.MarginRight = points; return;
            case "margt" or "margtsxn": _doc.MarginTop = points; return;
            case "margb" or "margbsxn": _doc.MarginBottom = points; return;
            case "deftab": if (n > 0) _doc.DefaultTab = points; return;
            case "uc": _state.UnicodeSkip = Math.Max(0, n); return;
            case "u":
                Emit((char)(n < 0 ? n + 65536 : n));
                _skipChars = _state.UnicodeSkip;
                return;

            // ---- paragraphs
            case "par": EndParagraph(); return;
            case "pard": _state.Para = new RtfParaFormat(); return;
            case "ql": SetPara(_state.Para with { Align = RtfAlign.Left }); return;
            case "qc": SetPara(_state.Para with { Align = RtfAlign.Center }); return;
            case "qr": SetPara(_state.Para with { Align = RtfAlign.Right }); return;
            case "qj" or "qd": SetPara(_state.Para with { Align = RtfAlign.Justify }); return;
            case "li": SetPara(_state.Para with { LeftIndent = points }); return;
            case "ri": SetPara(_state.Para with { RightIndent = points }); return;
            case "fi": SetPara(_state.Para with { FirstIndent = points }); return;
            case "sb": SetPara(_state.Para with { SpaceBefore = points }); return;
            case "sa": SetPara(_state.Para with { SpaceAfter = points }); return;
            case "sl": SetPara(_state.Para with { LineSpacing = n }); return;
            case "slmult": SetPara(_state.Para with { LineMultiple = n == 1 }); return;
            case "tx" or "tb": SetPara(_state.Para with { Tabs = _state.Para.Tabs.Append(points).Order().ToList() }); return;
            case "intbl": SetPara(_state.Para with { InTable = true }); return;
            case "s": SetPara(_state.Para with { Style = n }); return;
            case "contextualspace": SetPara(_state.Para with { ContextualSpacing = true }); return;
            case "itap":
                if (n > 1 && !_warnedNested) { _warnedNested = true; _doc.Warnings.Add("Tables inside tables were flattened."); }
                return;
            case "pagebb": SetPara(_state.Para with { PageBreakBefore = true }); return;
            case "page": PageBreak(); return;
            case "sect": PageBreak(); return;
            case "line": Emit('\n'); return;
            case "tab": Emit('\t'); return;

            // ---- characters
            case "plain":
                // Inside a list number LibreOffice resets formatting; keep the paragraph's font and size there.
                _state.Char = _state.ListText ? new RtfCharFormat { Font = _state.Char.Font, Size = _state.Char.Size } : new RtfCharFormat { Font = _defaultFont };
                return;
            case "b": _state.Char = _state.Char with { Bold = on }; return;
            case "i": _state.Char = _state.Char with { Italic = on }; return;
            case "ul" or "uld" or "uldb" or "uldash" or "uldashd" or "uldashdd" or "ulth" or "ulw" or "ulwave" or "uldb0":
                _state.Char = _state.Char with { Underline = on };
                return;
            case "ulnone": _state.Char = _state.Char with { Underline = false }; return;
            case "strike" or "striked": _state.Char = _state.Char with { Strike = on }; return;
            case "v": _state.Char = _state.Char with { Hidden = on }; return;
            case "caps" or "scaps": _state.Char = _state.Char with { Caps = on }; return;
            case "f": _state.Char = _state.Char with { Font = n }; return;
            case "fs": if (n > 0) _state.Char = _state.Char with { Size = n / 2.0 }; return;
            case "cf": _state.Char = _state.Char with { Color = n }; return;
            case "highlight" or "cb" or "chcbpat": _state.Char = _state.Char with { Highlight = n }; return;
            case "super": _state.Char = _state.Char with { Script = 1 }; return;
            case "sub": _state.Char = _state.Char with { Script = -1 }; return;
            case "nosupersub": _state.Char = _state.Char with { Script = 0 }; return;
            case "up": _state.Char = _state.Char with { Raise = param is null ? 3 : n / 2.0 }; return;
            case "dn": _state.Char = _state.Char with { Raise = -(param is null ? 3 : n / 2.0) }; return;

            // ---- special characters
            case "emdash": Emit('—'); return;
            case "endash": Emit('–'); return;
            case "bullet": Emit('•'); return;
            case "lquote": Emit('‘'); return;
            case "rquote": Emit('’'); return;
            case "ldblquote": Emit('“'); return;
            case "rdblquote": Emit('”'); return;
            case "emspace" or "enspace" or "qmspace": Emit(' '); return;
            case "zwj" or "zwnj" or "ltrmark" or "rtlmark": return;

            // ---- tables
            case "trowd":
                _cellRights.Clear();
                _rowLeft = 0;
                _rowGap = 0;
                _rowPadLeft = _rowPadRight = null;
                _rowBorders = false;
                return;
            case "cellx": _cellRights.Add(points); return;
            case "trgaph": _rowGap = points; _rowPadLeft ??= points; _rowPadRight ??= points; return;
            case "trpaddl" or "clpadt": _rowPadLeft = points; return;
            case "trpaddr" or "clpadr": _rowPadRight = points; return;
            case "trleft": _rowLeft = points; return;
            case "clbrdrt" or "clbrdrl" or "clbrdrb" or "clbrdrr" or "trbrdrt" or "trbrdrl" or "trbrdrb" or "trbrdrr" or "trbrdrh" or "trbrdrv":
                _rowBorders = true;
                return;
            case "cell" or "nestcell": EndCell(); return;
            case "row": EndRow(); return;
        }

        // An unknown destination after \* is skipped with everything in it.
        if (ignorable) _state.Dest = Dest.Skip;
    }

    private void SetPara(RtfParaFormat format)
    {
        _state.Para = format;
    }

    private void FontTableWord(string word, int n)
    {
        switch (word)
        {
            case "f":
                _state.FontIndex = n;
                _fontName.Clear();
                break;
            case "fnil" or "froman" or "fswiss" or "fmodern" or "fscript" or "fdecor" or "ftech" or "fbidi":
                _state.FontFamily = word[1..];
                break;
            case "fcharset": _state.FontCharset = n; break;
            default:
                // Alternative names, panose numbers and embedded font data inside the entry.
                if (word is "falt" or "panose" or "fontemb" or "fontfile") _state.Dest = Dest.Skip;
                break;
        }
    }

    private void EndFont()
    {
        if (_state.FontIndex >= 0)
            _doc.Fonts[_state.FontIndex] = new RtfFont(_fontName.ToString().Trim(), _state.FontFamily, _state.FontCharset);
        _fontName.Clear();
    }

    private void ColorWord(string word, int n)
    {
        switch (word)
        {
            case "red": _color = (n, _color.G, _color.B, true); break;
            case "green": _color = (_color.R, n, _color.B, true); break;
            case "blue": _color = (_color.R, _color.G, n, true); break;
        }
    }

    private void EndColor()
    {
        _doc.Colors.Add(_color.Set ? new PdfColor((byte)_color.R, (byte)_color.G, (byte)_color.B) : null);
        _color = default;
    }

    private void PictureWord(string word, int n)
    {
        var pic = _state.Picture!;
        switch (word)
        {
            case "pngblip" or "jpegblip" or "emfblip" or "wmetafile" or "macpict" or "dibitmap" or "wbitmap" or "pmmetafile":
                pic.Kind = word;
                break;
            case "picwgoal": pic.WidthGoal = n / 20.0; break;
            case "pichgoal": pic.HeightGoal = n / 20.0; break;
            case "picw": pic.Width = n; break;
            case "pich": pic.Height = n; break;
            case "picscalex": if (n > 0) pic.ScaleX = n; break;
            case "picscaley": if (n > 0) pic.ScaleY = n; break;
        }
    }

    private void EndGroup()
    {
        var closing = _state;
        var parent = _stack.Count > 0 ? _stack.Peek() : null;
        if (closing.Dest == Dest.FontTable && parent?.Dest != Dest.FontTable && _fontName.Length > 0) EndFont();
        if (closing.Dest == Dest.Picture && parent?.Dest != Dest.Picture && closing.Picture is { } pic)
        {
            if (pic.Kind is "pngblip" or "jpegblip" && pic.Bytes.Count > 0)
            {
                // Display size: the "goal" size in twips, scaled; else the pixel size at 96 dpi.
                double w = (pic.WidthGoal > 0 ? pic.WidthGoal : pic.Width * 0.75) * pic.ScaleX / 100;
                double h = (pic.HeightGoal > 0 ? pic.HeightGoal : pic.Height * 0.75) * pic.ScaleY / 100;
                if (w > 0 && h > 0)
                {
                    FlushText();
                    _paragraph.Content.Add(new RtfPicture(pic.Bytes.ToArray(), w, h));
                }
            }
            else if (pic.Kind != "")
            {
                const string message = "Some pictures were left out: only PNG and JPEG pictures can be imported (not WMF, EMF or bitmaps).";
                if (!_doc.Warnings.Contains(message)) _doc.Warnings.Add(message);
            }
        }
        if (closing.Dest == Dest.Text) FlushText();
    }

    // ------------------------------------------------------------------ text

    private void Emit(char c)
    {
        if (_state.Dest != Dest.Text || _state.Char.Hidden) return;
        // Symbol-font characters written as Unicode land in the private-use area (U+F020-U+F0FF).
        if (c is >= '\uF020' and <= '\uF0FF')
            c = SymbolChar(_doc.Fonts.TryGetValue(_state.Char.Font, out var symbolFont) ? symbolFont.Name : "Symbol", (byte)(c - 0xF000));
        var format = _state.Char;
        if (_textFormat is not null && _textFormat != format) FlushText();
        _textFormat = format;
        _text.Append(format.Caps ? char.ToUpper(c) : c);
    }

    private void FlushText()
    {
        if (_text.Length > 0 && _textFormat is not null)
            _paragraph.Content.Add(new RtfText(_text.ToString(), _textFormat));
        _text.Clear();
        _textFormat = null;
    }

    /// <summary>Decodes \'hh bytes collected so far in the code page of the current font.</summary>
    private void FlushBytes()
    {
        if (_pendingBytes.Count == 0) return;
        var bytes = _pendingBytes.ToArray();
        _pendingBytes.Clear();
        _doc.Fonts.TryGetValue(_state.Char.Font, out var font);
        if (font is { Charset: 2 })
        {
            foreach (var b in bytes) Emit(SymbolChar(font.Name, b));
            return;
        }
        int codePage = font is null ? _codePage : CodePageOf(font.Charset) ?? _codePage;
        string text;
        try { text = Encoding.GetEncoding(codePage).GetString(bytes); }
        catch (ArgumentException) { text = Encoding.Latin1.GetString(bytes); }
        foreach (var c in text) Emit(c);
    }

    private static int? CodePageOf(int charset) => charset switch
    {
        77 => 10000, 128 => 932, 129 => 949, 134 => 936, 136 => 950, 161 => 1253, 162 => 1254, 163 => 1258,
        177 => 1255, 178 => 1256, 186 => 1257, 204 => 1251, 222 => 874, 238 => 1250, _ => null,
    };

    /// <summary>Characters of symbol fonts (as used for list bullets) mapped to Unicode.</summary>
    private static char SymbolChar(string fontName, byte b)
    {
        if (fontName.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase))
            return b switch { 0x6C => '●', 0x6E => '■', 0x71 => '❑', 0x76 => '❖', 0xA7 => '▪', 0xA8 => '◻', 0xD8 => '➢', 0xFC => '✓', 0xFB => '✗', _ => '•' };
        return b switch { 0xB7 => '•', 0xA8 => '♦', 0xD8 => '¬', 0x2D => '−', 0xB0 => '°', 0xB4 => '×', 0xB8 => '÷', 0xAE => '→', _ => b < 0x80 ? (char)b : '•' };
    }

    // ------------------------------------------------------------------ structure

    private void EndParagraph()
    {
        FlushText();
        _paragraph.EndFormat = _state.Char;
        var finished = _paragraph;
        _paragraph = new RtfParagraph { Format = _state.Para };
        // A paragraph's format is the one in force at its end.
        var paragraph = new RtfParagraph { Format = _state.Para, EndFormat = finished.EndFormat };
        paragraph.Content.AddRange(finished.Content);
        if (_state.Para.InTable) _cell.Add(paragraph);
        else
        {
            if (_cells is not null) EndRow(); // a table without a closing \row
            _doc.Blocks.Add(paragraph);
        }
    }

    private void EndCell()
    {
        FlushText();
        if (_paragraph.Content.Count > 0 || _cell.Count == 0) EndParagraphInto(_cell);
        _cells ??= [];
        _cells.Add(_cell);
        _cell = [];
    }

    private void EndParagraphInto(List<RtfParagraph> target)
    {
        var paragraph = new RtfParagraph { Format = _state.Para with { InTable = true }, EndFormat = _state.Char };
        paragraph.Content.AddRange(_paragraph.Content);
        _paragraph = new RtfParagraph { Format = _state.Para };
        target.Add(paragraph);
    }

    private void EndRow()
    {
        FlushText();
        if (_cells is null || _cells.Count == 0) { _cells = null; return; }
        var rights = _cellRights.Count > 0 ? _cellRights.ToList() : EvenColumns(_cells.Count);
        var row = new RtfTableRow
        {
            Left = _rowLeft, PadLeft = _rowPadLeft ?? _rowGap, PadRight = _rowPadRight ?? _rowGap, CellRights = rights, Borders = _rowBorders,
        };
        row.Cells.AddRange(_cells);
        _doc.Blocks.Add(row);
        _cells = null;
    }

    private List<double> EvenColumns(int count)
    {
        double width = (_doc.PageWidth - _doc.MarginLeft - _doc.MarginRight) / count;
        return Enumerable.Range(1, count).Select(i => i * width).ToList();
    }

    private void PageBreak()
    {
        FlushText();
        if (_paragraph.Content.Count > 0) EndParagraph();
        _doc.Blocks.Add(new RtfPageBreak());
    }

    private void FinishDocument()
    {
        FlushBytes();
        FlushText();
        if (_paragraph.Content.Count > 0) EndParagraph();
        if (_cells is not null) EndRow();
        // Word ends every document with an empty paragraph mark that adds nothing visible.
        while (_doc.Blocks.Count > 0 && _doc.Blocks[^1] is RtfParagraph { Content.Count: 0 } or RtfPageBreak)
            _doc.Blocks.RemoveAt(_doc.Blocks.Count - 1);
    }
}
