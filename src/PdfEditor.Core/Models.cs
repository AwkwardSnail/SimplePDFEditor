namespace PdfEditor.Core;

/// <summary>RGBA colour, 0-255 per channel.</summary>
public readonly record struct PdfColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor Yellow = new(255, 235, 60);
}

/// <summary>
/// Rectangle in "view space": PDF points (1/72 inch), origin at the top-left of the page
/// as displayed (after rotation), y growing downwards. All public coordinates use this space.
/// </summary>
public readonly record struct PdfRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

public readonly record struct PdfSize(double Width, double Height);

/// <summary>A rendered page: top-down BGRA pixels, 4 bytes per pixel.</summary>
public sealed record RenderedPage(int Width, int Height, int Stride, byte[] Pixels);

public enum StandardFont { Helvetica, Times, Courier }

/// <summary>Appearance of text added to a page.</summary>
public sealed record TextStyle
{
    public StandardFont Font { get; init; } = StandardFont.Helvetica;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    /// <summary>
    /// Optional path to a TrueType font to embed. Needed for text outside Western European
    /// characters; when null and such characters are present a system font is picked.
    /// </summary>
    public string? FontFile { get; init; }
    public float Size { get; init; } = 12;
    public PdfColor Color { get; init; } = PdfColor.Black;
}

/// <summary>One piece of existing text on a page (PDFs store text as separate positioned runs).</summary>
public sealed record TextRun(int ObjectIndex, string Text, PdfRect Bounds, string FontName, float FontSize, PdfColor Color);

/// <summary>An image placed on a page.</summary>
public sealed record ImageInfo(int ObjectIndex, PdfRect Bounds, int PixelWidth, int PixelHeight);

/// <summary>A vector drawing: a group of shapes that sit together on the page.</summary>
public sealed record DrawingInfo(IReadOnlyList<int> ObjectIndices, PdfRect Bounds);

public enum TextEditResult
{
    /// <summary>The text was changed and the original font kept.</summary>
    KeptFont,
    /// <summary>The original font lacked the needed characters, so a replacement font was used.</summary>
    SubstitutedFont,
    /// <summary>The run was removed because the new text was empty.</summary>
    Removed,
}

public enum AnnotationKind { Comment, Highlight, Underline, Strikeout, Squiggly, FreeText, Other }

public sealed record AnnotationInfo(int Index, AnnotationKind Kind, PdfRect Bounds, string Contents, string Author, PdfColor Color);

/// <summary>A search hit: character range plus the rectangles covering it (one per line).</summary>
public sealed record TextMatch(int CharIndex, int Length, IReadOnlyList<PdfRect> Rects);

public sealed class PdfException(string message) : Exception(message);
