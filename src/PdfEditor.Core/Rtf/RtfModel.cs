namespace PdfEditor.Core.Rtf;

/// <summary>Character formatting. Sizes are in points.</summary>
internal sealed record RtfCharFormat
{
    public int Font { get; init; }
    public double Size { get; init; } = 12;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public bool Hidden { get; init; }
    public bool Caps { get; init; }
    public int Color { get; init; }          // index into the colour table; 0 is "automatic"
    public int Highlight { get; init; }      // index into the colour table; 0 is none
    public int Script { get; init; }         // 1 superscript, -1 subscript
    public double Raise { get; init; }       // explicit raise (positive) or lower, in points
}

internal enum RtfAlign { Left, Center, Right, Justify }

/// <summary>Paragraph formatting. Lengths are in points.</summary>
internal sealed record RtfParaFormat
{
    public RtfAlign Align { get; init; }
    public double LeftIndent { get; init; }
    public double RightIndent { get; init; }
    public double FirstIndent { get; init; }
    public double SpaceBefore { get; init; }
    public double SpaceAfter { get; init; }
    /// <summary>RTF \sl: positive is "at least" (or a multiple of single spacing with <see cref="LineMultiple"/>), negative is "exactly", in twips.</summary>
    public int LineSpacing { get; init; }
    public bool LineMultiple { get; init; }
    public IReadOnlyList<double> Tabs { get; init; } = [];
    public bool InTable { get; init; }
    public bool PageBreakBefore { get; init; }
    public int Style { get; init; }
    /// <summary>Leave out space before and after between paragraphs of the same style (as Word does for lists).</summary>
    public bool ContextualSpacing { get; init; }
}

internal abstract record RtfInline;

/// <summary>Text in one format. May contain '\t' (tab) and '\n' (line break).</summary>
internal sealed record RtfText(string Text, RtfCharFormat Format) : RtfInline;

/// <summary>A PNG or JPEG picture shown at the given size in points.</summary>
internal sealed record RtfPicture(byte[] Data, double Width, double Height) : RtfInline;

internal sealed class RtfParagraph
{
    public required RtfParaFormat Format { get; init; }
    public List<RtfInline> Content { get; } = [];
    /// <summary>Format of the paragraph mark, which sets the height of an empty paragraph.</summary>
    public RtfCharFormat EndFormat { get; set; } = new();
}

internal sealed class RtfTableRow
{
    public double Left { get; init; }                       // from the left margin, points
    public double PadLeft { get; init; }                    // space inside each cell, points
    public double PadRight { get; init; }
    public IReadOnlyList<double> CellRights { get; init; } = []; // right edge of each cell, from the left margin
    public bool Borders { get; init; }
    public List<List<RtfParagraph>> Cells { get; } = [];
}

internal sealed record RtfPageBreak;

internal sealed record RtfFont(string Name, string Family, int Charset);

internal sealed class RtfDocument
{
    public double PageWidth { get; set; } = 612;
    public double PageHeight { get; set; } = 792;
    public double MarginLeft { get; set; } = 90;
    public double MarginRight { get; set; } = 90;
    public double MarginTop { get; set; } = 72;
    public double MarginBottom { get; set; } = 72;
    public double DefaultTab { get; set; } = 36;
    public Dictionary<int, RtfFont> Fonts { get; } = [];
    public List<PdfColor?> Colors { get; } = [];
    /// <summary>Paragraphs, table rows and page breaks, in order.</summary>
    public List<object> Blocks { get; } = [];
    public List<string> Warnings { get; } = [];
}
