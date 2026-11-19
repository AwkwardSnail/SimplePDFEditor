using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>A point in view space: points from the top-left of the page as displayed.</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>
/// The shapes that can be drawn on a page. A line, arrow, box and ellipse are given by two
/// points (the ends, or opposite corners); a spline is a smooth curve through all its points.
/// </summary>
public enum ShapeKind { Line, Arrow, Box, Ellipse, Spline }

/// <summary>How a drawn shape looks. Without a fill it is see-through.</summary>
public sealed record ShapeStyle
{
    public PdfColor Stroke { get; init; } = PdfColor.Black;
    /// <summary>Line thickness in points.</summary>
    public double Width { get; init; } = 2;
    /// <summary>The colour inside a box, ellipse or spline; null leaves it see-through. Lines and arrows take no fill.</summary>
    public PdfColor? Fill { get; init; }
}

/// <summary>
/// One step of a shape's outline: 'M' moves to <see cref="End"/>, 'L' draws a line to it, 'C'
/// draws a curve to it bending towards the two control points, and 'Z' closes the outline.
/// </summary>
public readonly record struct ShapeStep(char Kind, PdfPoint End, PdfPoint Control1 = default, PdfPoint Control2 = default);

// Shapes drawn by the user. Each is one ordinary vector path, so it prints sharp at any size
// and can be selected, moved and deleted like any other drawing on the page.
public sealed unsafe partial class PdfDocument
{
    private const int PageObjectPath = 2;
    private const int SegmentMoveTo = 2;

    /// <summary>
    /// The outline of a shape, in the same units as its points. The editor draws its preview
    /// from this, so what is seen while drawing is what lands on the page. Empty when the points
    /// do not make a shape (fewer than two, or all in one place).
    /// </summary>
    public static IReadOnlyList<ShapeStep> ShapeOutline(ShapeKind kind, IReadOnlyList<PdfPoint> points, double lineWidth)
    {
        // Points that repeat the one before add nothing (a double-click gives two the same).
        var p = new List<PdfPoint>();
        foreach (var point in points)
            if (p.Count == 0 || Distance(p[^1], point) > 0.01) p.Add(point);
        if (p.Count < 2) return [];
        var steps = new List<ShapeStep>();
        PdfPoint a = p[0], b = p[^1];

        switch (kind)
        {
            case ShapeKind.Line:
                steps.Add(new('M', a));
                steps.Add(new('L', b));
                break;

            case ShapeKind.Arrow:
            {
                double length = Distance(a, b);
                double head = Math.Min(length, Math.Max(9, 4.5 * lineWidth)), half = head * 0.42;
                double ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
                var foot = new PdfPoint(b.X - ux * head, b.Y - uy * head); // where the head starts
                if (length > head)
                {
                    // The line stops a little inside the head, so its square end never shows at the tip.
                    steps.Add(new('M', a));
                    steps.Add(new('L', new PdfPoint(foot.X + ux * head * 0.25, foot.Y + uy * head * 0.25)));
                }
                steps.Add(new('M', b));
                steps.Add(new('L', new PdfPoint(foot.X - uy * half, foot.Y + ux * half)));
                steps.Add(new('L', new PdfPoint(foot.X + uy * half, foot.Y - ux * half)));
                steps.Add(new('Z', b));
                break;
            }

            case ShapeKind.Box:
                steps.Add(new('M', a));
                steps.Add(new('L', new PdfPoint(b.X, a.Y)));
                steps.Add(new('L', b));
                steps.Add(new('L', new PdfPoint(a.X, b.Y)));
                steps.Add(new('Z', a));
                break;

            case ShapeKind.Ellipse:
            {
                // Four curves, one a quarter; 0.5523 is the usual constant that makes them round.
                double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2, rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                double ox = rx * 0.5523, oy = ry * 0.5523;
                steps.Add(new('M', new PdfPoint(cx + rx, cy)));
                steps.Add(new('C', new PdfPoint(cx, cy + ry), new PdfPoint(cx + rx, cy + oy), new PdfPoint(cx + ox, cy + ry)));
                steps.Add(new('C', new PdfPoint(cx - rx, cy), new PdfPoint(cx - ox, cy + ry), new PdfPoint(cx - rx, cy + oy)));
                steps.Add(new('C', new PdfPoint(cx, cy - ry), new PdfPoint(cx - rx, cy - oy), new PdfPoint(cx - ox, cy - ry)));
                steps.Add(new('C', new PdfPoint(cx + rx, cy), new PdfPoint(cx + ox, cy - ry), new PdfPoint(cx + rx, cy - oy)));
                steps.Add(new('Z', new PdfPoint(cx + rx, cy)));
                break;
            }

            case ShapeKind.Spline:
                // A smooth curve through every point: at each one it runs parallel to the line
                // joining its two neighbours (a Catmull-Rom spline, written as Bézier curves).
                steps.Add(new('M', p[0]));
                for (int i = 0; i + 1 < p.Count; i++)
                {
                    PdfPoint before = p[Math.Max(0, i - 1)], from = p[i], to = p[i + 1], after = p[Math.Min(p.Count - 1, i + 2)];
                    steps.Add(new('C', to,
                        new PdfPoint(from.X + (to.X - before.X) / 6, from.Y + (to.Y - before.Y) / 6),
                        new PdfPoint(to.X - (after.X - from.X) / 6, to.Y - (after.Y - from.Y) / 6)));
                }
                break;
        }
        return steps;

        static double Distance(PdfPoint x, PdfPoint y) => Math.Sqrt((x.X - y.X) * (x.X - y.X) + (x.Y - y.Y) * (x.Y - y.Y));
    }

    /// <summary>
    /// Draws a shape on a page, on top of what is there. Points are in view space. Returns the
    /// new object's index.
    /// </summary>
    public int AddShape(int pageIndex, ShapeKind kind, IReadOnlyList<PdfPoint> points, ShapeStyle? style = null)
    {
        style ??= new ShapeStyle();
        if (style.Width <= 0 || style.Width > 200) throw new ArgumentOutOfRangeException(nameof(style), "The line thickness must be between 0 and 200 points.");
        var outline = ShapeOutline(kind, points, style.Width);
        if (outline.Count == 0) throw new ArgumentException("A shape needs at least two different points.", nameof(points));

        using var page = OpenPage(pageIndex);
        IntPtr path = IntPtr.Zero;
        (float X, float Y) User(PdfPoint p)
        {
            var (x, y) = page.ViewToUser.Apply(p.X, p.Y);
            return ((float)x, (float)y);
        }
        foreach (var step in outline)
        {
            var (x, y) = User(step.End);
            switch (step.Kind)
            {
                case 'M' when path == IntPtr.Zero:
                    path = Native.FPDFPageObj_CreateNewPath(x, y);
                    if (path == IntPtr.Zero) throw new PdfException("Could not draw the shape.");
                    break;
                case 'M': Native.FPDFPath_MoveTo(path, x, y); break;
                case 'L': Native.FPDFPath_LineTo(path, x, y); break;
                case 'C':
                    var (x1, y1) = User(step.Control1);
                    var (x2, y2) = User(step.Control2);
                    Native.FPDFPath_BezierTo(path, x1, y1, x2, y2, x, y);
                    break;
                case 'Z': Native.FPDFPath_Close(path); break;
            }
        }

        // An arrow's head is solid in the line's own colour; lines take no fill at all.
        PdfColor? fill = kind switch { ShapeKind.Arrow => style.Stroke, ShapeKind.Line => null, _ => style.Fill };
        if (fill is { } f) Native.FPDFPageObj_SetFillColor(path, f.R, f.G, f.B, f.A);
        Native.FPDFPageObj_SetStrokeColor(path, style.Stroke.R, style.Stroke.G, style.Stroke.B, style.Stroke.A);
        Native.FPDFPageObj_SetStrokeWidth(path, (float)style.Width);
        // Round ends and corners, except a box, whose corners stay square.
        Native.FPDFPageObj_SetLineCap(path, kind == ShapeKind.Box ? 0 : 1);
        Native.FPDFPageObj_SetLineJoin(path, kind == ShapeKind.Box ? 0 : 1);
        Native.FPDFPath_SetDrawMode(path, fill is null ? 0 : 1, 1);
        Native.FPDFPage_InsertObject(page.Handle, path);
        page.NewObjects.Add(path);
        page.Dirty = true;
        return Native.FPDFPage_CountObjects(page.Handle) - 1;
    }

    /// <summary>
    /// Fills shapes with a colour, or with null makes them see-through. Lines and arrows are left
    /// as they are, and so is anything that is not a vector shape. A shape that was only a filled
    /// area keeps an outline in its old colour when it is made see-through, so it does not vanish.
    /// Returns how many shapes changed.
    /// </summary>
    public int SetShapeFill(int pageIndex, IEnumerable<int> objectIndices, PdfColor? fill)
    {
        using var page = OpenPage(pageIndex);
        int changed = 0;
        foreach (var obj in objectIndices.Distinct().Select(i => GetObject(page, i)).ToList())
        {
            if (Native.FPDFPageObj_GetType(obj) != PageObjectPath || !TakesFill(obj)) continue;
            int mode = 0, stroke = 0;
            Native.FPDFPath_GetDrawMode(obj, &mode, &stroke);
            if (fill is { } colour)
            {
                Native.FPDFPageObj_SetFillColor(obj, colour.R, colour.G, colour.B, colour.A);
                Native.FPDFPath_SetDrawMode(obj, mode == 0 ? 1 : mode, stroke);
            }
            else
            {
                if (mode == 0) continue; // see-through already
                if (stroke == 0)
                {
                    uint r = 0, g = 0, b = 0, a = 255;
                    Native.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a);
                    Native.FPDFPageObj_SetStrokeColor(obj, r, g, b, a);
                    Native.FPDFPageObj_SetStrokeWidth(obj, 1);
                }
                Native.FPDFPath_SetDrawMode(obj, 0, 1);
            }
            changed++;
        }
        if (changed > 0) page.Dirty = page.ChangedExisting = true;
        return changed;
    }

    /// <summary>
    /// Whether a fill makes sense for a path: not for a plain line (one open stroke of two points,
    /// or several open strokes) and not for an arrow (an open stroke with a closed head).
    /// </summary>
    private static bool TakesFill(IntPtr path)
    {
        int segments = Native.FPDFPath_CountSegments(path), parts = 0, closed = 0;
        for (int i = 0; i < segments; i++)
        {
            var segment = Native.FPDFPath_GetPathSegment(path, i);
            if (Native.FPDFPathSegment_GetType(segment) == SegmentMoveTo) parts++;
            if (Native.FPDFPathSegment_GetClose(segment) != 0) closed++;
        }
        if (closed == 0) return parts == 1 && segments >= 3;
        return closed >= parts;
    }
}
