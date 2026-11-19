using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>How a watermark looks. It is drawn large across the middle of the page, see-through, over what is there.</summary>
public sealed record WatermarkOptions
{
    public PdfColor Color { get; init; } = new(120, 120, 120);
    /// <summary>How solid it is, from 0 (invisible) to 1 (solid).</summary>
    public double Opacity { get; init; } = 0.25;
    /// <summary>True runs it corner to corner, rising to the right; false keeps it level.</summary>
    public bool Diagonal { get; init; } = true;
}

// Which item lies in front of which, the line of a shape, and watermarks.
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Moves items one layer up (towards the front) or down (towards the back): past the nearest
    /// item that overlaps them, since passing one that does not overlap changes nothing that can
    /// be seen. Several items given together keep their order among themselves. Returns false
    /// when nothing overlapping lies that way, so the items are already at the front or back.
    /// <para>PDFium keeps existing objects in the order they were first written and only honours
    /// the place of new ones, so each moved item is replaced by a fresh copy of itself in its new
    /// place. Text stays text and shapes stay shapes; a picture is stored again without loss.
    /// Groups, gradients and clipped items cannot be copied and are refused with a reason.</para>
    /// </summary>
    public bool MoveLayer(int pageIndex, IEnumerable<int> objectIndices, bool up)
    {
        using var page = OpenPage(pageIndex);
        var indices = objectIndices.Distinct().OrderBy(i => i).ToList();
        if (indices.Count == 0) return false;
        var moving = indices.Select(i => GetObject(page, i)).ToList();
        var chosen = new HashSet<int>(indices);

        // The area the moving items cover together.
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var obj in moving)
        {
            var b = ObjectBounds(page, obj);
            (left, top, right, bottom) = (Math.Min(left, b.X), Math.Min(top, b.Y), Math.Max(right, b.Right), Math.Max(bottom, b.Bottom));
        }
        bool Overlaps(IntPtr other)
        {
            var b = ObjectBounds(page, other);
            return b.X < right && b.Right > left && b.Y < bottom && b.Bottom > top;
        }

        // The nearest overlapping item beyond the group, in the direction asked for. The drawing
        // order is the order of the objects: a later one is drawn over an earlier one.
        int count = Native.FPDFPage_CountObjects(page.Handle);
        IntPtr passed = IntPtr.Zero;
        if (up)
        {
            for (int i = indices[^1] + 1; i < count && passed == IntPtr.Zero; i++)
                if (!chosen.Contains(i) && Native.FPDFPage_GetObject(page.Handle, i) is var o && Overlaps(o)) passed = o;
        }
        else
        {
            for (int i = indices[0] - 1; i >= 0 && passed == IntPtr.Zero; i--)
                if (!chosen.Contains(i) && Native.FPDFPage_GetObject(page.Handle, i) is var o && Overlaps(o)) passed = o;
        }
        if (passed == IntPtr.Zero) return false;

        // A fresh copy of each item.
        var (glyphs, pageFonts) = ReadPageText(page);
        var copies = new List<IntPtr>();
        string? problem = null;
        foreach (var obj in moving)
        {
            Native.FS_MATRIX om;
            if (Native.FPDFPageObj_GetMatrix(obj, &om) == 0) { problem = "its position cannot be read"; break; }
            var matrix = new Affine(om.a, om.b, om.c, om.d, om.e, om.f);
            if (!ClipIsHarmless(obj, UngroupClipSlack)) { problem = "it is clipped to a shape"; break; }
            IntPtr copy = IntPtr.Zero;
            switch (Native.FPDFPageObj_GetType(obj))
            {
                case Native.PAGEOBJ_TEXT:
                    if (!glyphs.TryGetValue(obj, out var chars) || chars.All(c => char.IsWhiteSpace(c.Ch))) continue; // draws nothing
                    copy = ClashesWithPageFontOf(obj, chars, pageFonts) ? TextAsOutlines(obj, chars, matrix) : Pin(page, obj, chars, always: true, matrix: matrix);
                    if (copy == IntPtr.Zero) problem = "its text could not be copied";
                    break;
                case PageObjPath:
                    copy = CopyPath(obj, matrix);
                    if (copy == IntPtr.Zero) problem = "the shape could not be copied";
                    break;
                case Native.PAGEOBJ_IMAGE:
                    copy = CopyImage(page, obj, matrix);
                    if (copy == IntPtr.Zero) problem = "the picture could not be copied";
                    break;
                case PageObjForm: problem = "it is a group; click it with Select to take the group apart first"; break;
                default: problem = "it is a gradient"; break;
            }
            if (problem is not null) break;
            copies.Add(copy);
        }
        if (problem is not null)
        {
            foreach (var copy in copies) Native.FPDFPageObj_Destroy(copy);
            throw new PdfException("This item cannot be moved to another layer: " + problem + ".");
        }
        if (copies.Count == 0) return false;

        // The copies go in just behind, or just in front of, the item passed; then the originals go.
        int at = -1;
        for (int i = 0; i < count && at < 0; i++) if (Native.FPDFPage_GetObject(page.Handle, i) == passed) at = i;
        if (up) at++;
        for (int k = 0; k < copies.Count; k++)
        {
            Native.FPDFPage_InsertObjectAtIndex(page.Handle, copies[k], (nuint)(at + k));
            page.NewObjects.Add(copies[k]);
        }
        foreach (var obj in moving)
        {
            Native.FPDFPage_RemoveObject(page.Handle, obj);
            Native.FPDFPageObj_Destroy(obj);
        }
        page.Dirty = page.ChangedExisting = true;
        _charsByFont = null;
        return true;

        // A text object clashes with the page's fonts only through other objects' fonts, never its own.
        static bool ClashesWithPageFontOf(IntPtr textObj, List<(char Ch, double X, double Y)> chars, Dictionary<string, List<IntPtr>> fonts) =>
            ClashesWithPageFont(textObj, chars, fonts);
    }

    /// <summary>
    /// How a vector shape is drawn: its line colour and thickness, and its fill (null when it is
    /// see-through). Null when the object is not a vector shape.
    /// </summary>
    public ShapeStyle? GetShapeStyle(int pageIndex, int objectIndex)
    {
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex);
        if (Native.FPDFPageObj_GetType(obj) != PageObjectPath) return null;
        int mode = 0, stroke = 0;
        Native.FPDFPath_GetDrawMode(obj, &mode, &stroke);
        uint r = 0, g = 0, b = 0, a = 255;
        Native.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a);
        var fill = new PdfColor((byte)r, (byte)g, (byte)b, (byte)a);
        (r, g, b, a) = (0, 0, 0, 255);
        Native.FPDFPageObj_GetStrokeColor(obj, &r, &g, &b, &a);
        float width = 1;
        Native.FPDFPageObj_GetStrokeWidth(obj, &width);
        return new ShapeStyle
        {
            // A shape with no line of its own is described by the colour it is filled with.
            Stroke = stroke != 0 ? new PdfColor((byte)r, (byte)g, (byte)b, (byte)a) : fill,
            Width = stroke != 0 ? width : 0,
            Fill = mode != 0 && TakesFill(obj) ? fill : null,
        };
    }

    /// <summary>
    /// Gives vector shapes a line of the given colour and thickness (in points). An arrow's head
    /// takes the colour too. Anything that is not a vector shape is left alone. Returns how many
    /// shapes changed.
    /// </summary>
    public int SetShapeLine(int pageIndex, IEnumerable<int> objectIndices, PdfColor colour, double width)
    {
        if (width <= 0 || width > 200) throw new ArgumentOutOfRangeException(nameof(width), "The line thickness must be between 0 and 200 points.");
        using var page = OpenPage(pageIndex);
        int changed = 0;
        foreach (var obj in objectIndices.Distinct().Select(i => GetObject(page, i)).ToList())
        {
            if (Native.FPDFPageObj_GetType(obj) != PageObjectPath) continue;
            int mode = 0, stroke = 0;
            Native.FPDFPath_GetDrawMode(obj, &mode, &stroke);
            Native.FPDFPageObj_SetStrokeColor(obj, colour.R, colour.G, colour.B, colour.A);
            Native.FPDFPageObj_SetStrokeWidth(obj, (float)width);
            // The solid part of a shape that takes no fill of its own (an arrow's head) belongs to its line.
            if (mode != 0 && !TakesFill(obj)) Native.FPDFPageObj_SetFillColor(obj, colour.R, colour.G, colour.B, colour.A);
            Native.FPDFPath_SetDrawMode(obj, mode, 1);
            changed++;
        }
        if (changed > 0) page.Dirty = page.ChangedExisting = true;
        return changed;
    }

    /// <summary>
    /// Writes a word or two large across the middle of a page, see-through, over what is there
    /// (DRAFT, COPY, a name). It is ordinary text, so it can be selected and deleted later.
    /// </summary>
    public void AddWatermark(int pageIndex, string text, WatermarkOptions? options = null)
    {
        options ??= new WatermarkOptions();
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) throw new ArgumentException("The watermark needs some text.", nameof(text));
        if (options.Opacity <= 0 || options.Opacity > 1) throw new ArgumentOutOfRangeException(nameof(options), "The opacity must be above 0 and at most 1.");

        using var page = OpenPage(pageIndex);
        // Made at a known size first, to measure it; then scaled and turned into place.
        const float trial = 100;
        var style = new TextStyle { Size = trial, Bold = true, Color = options.Color };
        var obj = InsertTextObject(page, text, style, Affine.Identity, out _);
        float l = 0, b = 0, r = 0, t = 0;
        if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0 || r - l < 0.01 || t - b < 0.01)
        {
            Native.FPDFPage_RemoveObject(page.Handle, obj);
            Native.FPDFPageObj_Destroy(obj);
            throw new PdfException("The watermark's text cannot be shown.");
        }

        double width = page.Width, height = page.Height;
        double angle = options.Diagonal ? Math.Atan2(height, width) : 0;
        double room = options.Diagonal ? Math.Sqrt(width * width + height * height) * 0.72 : width * 0.8;
        double scale = Math.Min(room / (r - l), Math.Min(width, height) * 0.3 / (t - b));

        // Its middle to the origin; to size, with y turned over (text runs y up, the page as seen
        // y down); turned to rise to the right; then to the middle of the page.
        var place = Affine.Concat(new Affine(1, 0, 0, 1, -(l + r) / 2, -(b + t) / 2), new Affine(scale, 0, 0, -scale, 0, 0));
        place = Affine.Concat(place, new Affine(Math.Cos(-angle), Math.Sin(-angle), -Math.Sin(-angle), Math.Cos(-angle), 0, 0));
        place = Affine.Concat(place, new Affine(1, 0, 0, 1, width / 2, height / 2));
        var m = Affine.Concat(place, page.ViewToUser).ToNative();
        Native.FPDFPageObj_SetMatrix(obj, &m);
        Native.FPDFPageObj_SetFillColor(obj, options.Color.R, options.Color.G, options.Color.B, (uint)Math.Round(options.Opacity * 255));
        page.NewObjects.Add(obj);
        page.Dirty = true;
        _charsByFont = null;
    }
}
