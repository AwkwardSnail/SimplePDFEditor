using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>One drawn shape on a page (a line, a box, a curve, a shaded area): where it stands in the page's order, and the room it takes.</summary>
public sealed record ShapeInfo(int ObjectIndex, PdfRect Bounds);

/// <summary>A shape near a point.</summary>
/// <param name="OnInk">The point is on what the shape shows: its filling, or its line (within reach).</param>
/// <param name="Inside">The point is within the shape's outline, filled or not. For a shape with no closed outline, the same as <paramref name="OnInk"/>.</param>
public sealed record ShapeHit(int ObjectIndex, PdfRect Bounds, bool OnInk, bool Inside);

// Finding the shape under a point by its real outline, not the box around it.
public sealed unsafe partial class PdfDocument
{
    /// <summary>Whether an object counts as a drawn shape: not text, not a picture, and not something covering most of the page (a background, a page frame).</summary>
    private static bool IsShape(PageHandle page, IntPtr obj, out PdfRect bounds)
    {
        bounds = default;
        int type = Native.FPDFPageObj_GetType(obj);
        if (type is Native.PAGEOBJ_TEXT or Native.PAGEOBJ_IMAGE) return false;
        bounds = ObjectBounds(page, obj);
        if (bounds.Width <= 0 && bounds.Height <= 0) return false;
        return bounds.Width * bounds.Height <= 0.5 * page.Width * page.Height;
    }

    /// <summary>Every drawn shape on a page, one by one (see <see cref="GetDrawings"/> for shapes gathered into figures).</summary>
    public IReadOnlyList<ShapeInfo> GetShapes(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var shapes = new List<ShapeInfo>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
            if (IsShape(page, Native.FPDFPage_GetObject(page.Handle, i), out var bounds)) shapes.Add(new ShapeInfo(i, bounds));
        return shapes;
    }

    /// <summary>
    /// The shapes at or near a point, in the order they are drawn (the last lies on top), each
    /// with whether the point is on what the shape shows and whether it is within its outline.
    /// </summary>
    /// <param name="reach">How near a line counts as on it, in points.</param>
    public IReadOnlyList<ShapeHit> HitShapes(int pageIndex, double x, double y, double reach)
    {
        using var page = OpenPage(pageIndex);
        var hits = new List<ShapeHit>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            if (!IsShape(page, obj, out var bounds)) continue;
            if (x < bounds.X - reach || x > bounds.Right + reach || y < bounds.Y - reach || y > bounds.Bottom + reach) continue;
            if (Native.FPDFPageObj_GetType(obj) != PageObjectPath)
            {
                // A shaded area or a group: only the room it takes is known.
                bool within = bounds.Contains(x, y);
                if (within) hits.Add(new ShapeHit(i, bounds, within, within));
                continue;
            }

            // The outline as seen, curves taken as short straight pieces.
            Native.FS_MATRIX m;
            if (Native.FPDFPageObj_GetMatrix(obj, &m) == 0) continue;
            var toView = Affine.Concat(new Affine(m.a, m.b, m.c, m.d, m.e, m.f), page.UserToView);
            var parts = new List<(List<(double X, double Y)> Points, bool Closed)>();
            List<(double X, double Y)>? part = null;
            var curve = new List<(double X, double Y)>();
            int segments = Native.FPDFPath_CountSegments(obj);
            for (int s = 0; s < segments; s++)
            {
                var segment = Native.FPDFPath_GetPathSegment(obj, s);
                float px = 0, py = 0;
                if (segment == IntPtr.Zero || Native.FPDFPathSegment_GetPoint(segment, &px, &py) == 0) continue;
                var point = toView.Apply(px, py);
                int kind = Native.FPDFPathSegment_GetType(segment); // 0 line to, 1 curve to, 2 move to
                if (kind == 2 || part is null)
                {
                    part = [point];
                    parts.Add((part, false));
                    curve.Clear();
                }
                else if (kind == 1)
                {
                    curve.Add(point);
                    if (curve.Count == 3)
                    {
                        var from = part[^1];
                        for (int step = 1; step <= 12; step++)
                        {
                            double t = step / 12.0, u = 1 - t;
                            part.Add((u * u * u * from.X + 3 * u * u * t * curve[0].X + 3 * u * t * t * curve[1].X + t * t * t * curve[2].X,
                                      u * u * u * from.Y + 3 * u * u * t * curve[0].Y + 3 * u * t * t * curve[1].Y + t * t * t * curve[2].Y));
                        }
                        curve.Clear();
                    }
                }
                else part.Add(point);
                if (Native.FPDFPathSegment_GetClose(segment) != 0 && curve.Count == 0) parts[^1] = (part, true);
            }
            if (parts.Count == 0) continue;

            int fillMode = 0, stroke = 0;
            Native.FPDFPath_GetDrawMode(obj, &fillMode, &stroke);
            float width = 1;
            Native.FPDFPageObj_GetStrokeWidth(obj, &width);
            double seenWidth = width * Math.Sqrt(Math.Abs((double)m.a * m.d - (double)m.b * m.c));

            // Within the outline: how many times it winds round the point, each part closed.
            int winding = 0, crossings = 0;
            double nearest = double.MaxValue;
            foreach (var (points, closed) in parts)
            {
                for (int k = 0; k < points.Count; k++)
                {
                    bool last = k == points.Count - 1;
                    var a = points[k];
                    var b = points[last ? 0 : k + 1];
                    if ((a.Y <= y) != (b.Y <= y))
                    {
                        double at = a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                        if (at > x) { crossings++; winding += b.Y > a.Y ? 1 : -1; }
                    }
                    if (last && !closed) continue; // the closing piece of an open part is not drawn
                    double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
                    double along = length < 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
                    double ox = a.X + along * dx - x, oy = a.Y + along * dy - y;
                    nearest = Math.Min(nearest, Math.Sqrt(ox * ox + oy * oy));
                }
            }
            bool wound = winding != 0, filled = fillMode == 1 ? crossings % 2 == 1 : fillMode == 2 && wound;
            bool onLine = nearest <= reach + (stroke != 0 ? seenWidth / 2 : 0);
            bool onInk = filled || (stroke != 0 || fillMode == 0) && onLine;
            bool hasOutline = fillMode != 0 || parts.Any(p => p.Closed);
            bool inside = onInk || hasOutline && wound;
            if (onInk || inside) hits.Add(new ShapeHit(i, bounds, onInk, inside));
        }
        return hits;
    }
}
