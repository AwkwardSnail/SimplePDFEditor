namespace PdfEditor.Core;

public enum TextAlignment { Left, Center, Right, Justify }

/// <summary>A stretch of a paragraph's text that shares one look.</summary>
/// <param name="FontSize">Visible size in points.</param>
public sealed record TextSpan(int Start, int Length, string FontName, double FontSize, PdfColor Color, bool Bold, bool Italic);

/// <summary>
/// A paragraph found on a page: lines of text that belong together. Indices are valid until the
/// page next changes, so list paragraphs again after every edit.
/// </summary>
/// <param name="Text">The paragraph's text with its line breaks removed (words that were hyphenated across lines are joined).</param>
/// <param name="FontSize">The size most of the paragraph uses, in points.</param>
/// <param name="LineSpacing">Distance between baselines, in points.</param>
/// <param name="IsEditable">False when the paragraph cannot be rewritten safely (for example right-to-left script or characters without a Unicode meaning).</param>
public sealed record TextParagraph(
    int Index,
    string Text,
    PdfRect Bounds,
    TextAlignment Alignment,
    double FontSize,
    double LineSpacing,
    int LineCount,
    IReadOnlyList<TextSpan> Spans,
    bool IsEditable);

/// <summary>A formatting change. Properties left null are kept as they are.</summary>
public sealed record TextFormat
{
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    /// <summary>Visible size in points.</summary>
    public double? Size { get; init; }
    public PdfColor? Color { get; init; }
}

public sealed record ReflowOptions
{
    /// <summary>
    /// When the paragraph gets taller or shorter, move the content below it in the same column
    /// by the same amount. Content in the bottom 8% of the page (footers, page numbers) stays put.
    /// </summary>
    public bool PushFollowingContent { get; init; } = true;

    /// <summary>Changes the paragraph's alignment; null keeps it.</summary>
    public TextAlignment? Alignment { get; init; }
}

/// <param name="Bounds">Where the paragraph now is.</param>
/// <param name="HeightChange">How much taller (positive) or shorter the paragraph became, in points.</param>
/// <param name="UsedSubstituteFont">Some characters were not in the original font, so a similar font was used for them.</param>
/// <param name="RunsOffPage">Text or moved content now extends past the bottom of the page.</param>
/// <param name="MovedItems">How many items below the paragraph were moved.</param>
public sealed record ReflowResult(PdfRect Bounds, int LineCount, double HeightChange, bool UsedSubstituteFont, bool RunsOffPage, int MovedItems);
