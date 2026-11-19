using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfEditor.Core.Interop;
using StbImageSharp;

namespace PdfEditor.Core;

// Page content: text and images.
public sealed unsafe partial class PdfDocument
{
    private const double LineSpacing = 1.2;
    private const double AscentRatio = 0.8; // distance from the top of a line to its baseline, per unit of font size

    // ------------------------------------------------------------------ adding text

    /// <summary>
    /// Adds text with its top-left corner at (x, y). Line breaks in <paramref name="text"/> start new lines.
    /// </summary>
    public void AddText(int pageIndex, double x, double y, string text, TextStyle? style = null)
    {
        style ??= new TextStyle();
        if (style.Size <= 0) throw new ArgumentOutOfRangeException(nameof(style), "Font size must be positive.");
        using var page = OpenPage(pageIndex);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            double baseline = y + style.Size * AscentRatio + i * style.Size * LineSpacing;
            // Text space has y up; flip it so the glyphs are upright in view space, then go to user space.
            var matrix = Affine.Concat(new Affine(1, 0, 0, -1, x, baseline), page.ViewToUser);
            InsertTextObject(page, lines[i], style, matrix, out _);
        }
    }

    /// <summary>
    /// Creates and inserts one text object, falling back to an embedded system font when the
    /// requested font cannot show every character. Returns the new object.
    /// </summary>
    private IntPtr InsertTextObject(PageHandle page, string text, TextStyle style, Affine matrix, out bool usedFallbackFont)
    {
        usedFallbackFont = false;
        IntPtr obj = style.FontFile is null
            ? Native.FPDFPageObj_NewTextObj(Doc, StandardFontName(style), style.Size)
            : Native.FPDFPageObj_CreateTextObj(Doc, LoadFont(style.FontFile), style.Size);
        if (obj == IntPtr.Zero) throw new PdfException("Could not create the text.");

        Place(obj);
        if (!ShowsText(page, obj, text) && style.FontFile is null && FindSystemFont(style) is { } fallback)
        {
            Native.FPDFPage_RemoveObject(page.Handle, obj);
            Native.FPDFPageObj_Destroy(obj);
            obj = Native.FPDFPageObj_CreateTextObj(Doc, LoadFont(fallback), style.Size);
            if (obj == IntPtr.Zero) throw new PdfException("Could not create the text.");
            Place(obj);
            usedFallbackFont = true;
        }
        page.Dirty = true;
        return obj;

        void Place(IntPtr o)
        {
            Native.FPDFText_SetText(o, text);
            var m = matrix.ToNative();
            Native.FPDFPageObj_SetMatrix(o, &m);
            Native.FPDFPageObj_SetFillColor(o, style.Color.R, style.Color.G, style.Color.B, style.Color.A);
            Native.FPDFPage_InsertObject(page.Handle, o);
        }
    }

    /// <summary>True when the object, as PDFium reads it back, contains exactly the wanted text.</summary>
    private static bool ShowsText(PageHandle page, IntPtr textObj, string expected)
    {
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == IntPtr.Zero) return false;
        try { return Normalize(ReadObjectText(textObj, textPage)) == Normalize(expected); }
        finally { Native.FPDFText_ClosePage(textPage); }

        static string Normalize(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c) && c != '\0'));
    }

    internal static string StandardFontName(TextStyle s) => (s.Font, s.Bold, s.Italic) switch
    {
        (StandardFont.Times, false, false) => "Times-Roman",
        (StandardFont.Times, true, false) => "Times-Bold",
        (StandardFont.Times, false, true) => "Times-Italic",
        (StandardFont.Times, true, true) => "Times-BoldItalic",
        (StandardFont.Courier, false, false) => "Courier",
        (StandardFont.Courier, true, false) => "Courier-Bold",
        (StandardFont.Courier, false, true) => "Courier-Oblique",
        (StandardFont.Courier, true, true) => "Courier-BoldOblique",
        (_, false, false) => "Helvetica",
        (_, true, false) => "Helvetica-Bold",
        (_, false, true) => "Helvetica-Oblique",
        (_, true, true) => "Helvetica-BoldOblique",
    };

    /// <summary>Loads (once per document) and embeds a TrueType font file.</summary>
    internal IntPtr LoadFont(string path)
    {
        string key = Path.GetFullPath(path);
        if (_fonts.TryGetValue(key, out var font)) return font;
        byte[] data = File.ReadAllBytes(key);
        fixed (byte* p = data)
            font = Native.FPDFText_LoadFont(Doc, p, (uint)data.Length, Native.FPDF_FONT_TRUETYPE, 1);
        if (font == IntPtr.Zero) throw new PdfException($"'{Path.GetFileName(path)}' is not a usable TrueType font.");
        _fonts[key] = font;
        return font;
    }

    /// <summary>Finds a broad-coverage font installed on this machine, matching the style where possible.</summary>
    internal static string? FindSystemFont(TextStyle style)
    {
        string suffix = (style.Bold, style.Italic) switch { (true, true) => "bi", (true, false) => "bd", (false, true) => "i", _ => "" };
        string dejaVu = (style.Bold, style.Italic) switch { (true, true) => "-BoldOblique", (true, false) => "-Bold", (false, true) => "-Oblique", _ => "" };
        string liberation = (style.Bold, style.Italic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "Regular" };
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] sans =
        [
            Path.Combine(windows, $"arial{suffix}.ttf"),
            Path.Combine(windows, "arial.ttf"),
            Path.Combine(windows, "segoeui.ttf"),
            $"/usr/share/fonts/truetype/dejavu/DejaVuSans{dejaVu}.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
            "/Library/Fonts/Arial Unicode.ttf",
        ];
        // Keep the look of the text: serif for serif, monospaced for monospaced, falling back to sans.
        string[] family = style.Font switch
        {
            StandardFont.Times =>
            [
                Path.Combine(windows, $"times{suffix}.ttf"),
                Path.Combine(windows, "times.ttf"),
                $"/usr/share/fonts/truetype/liberation/LiberationSerif-{liberation}.ttf",
                $"/usr/share/fonts/truetype/dejavu/DejaVuSerif{dejaVu.Replace("Oblique", "Italic")}.ttf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf",
                "/System/Library/Fonts/Supplemental/Times New Roman.ttf",
            ],
            StandardFont.Courier =>
            [
                Path.Combine(windows, $"cour{suffix}.ttf"),
                Path.Combine(windows, "cour.ttf"),
                $"/usr/share/fonts/truetype/liberation/LiberationMono-{liberation}.ttf",
                $"/usr/share/fonts/truetype/dejavu/DejaVuSansMono{dejaVu}.ttf",
                "/System/Library/Fonts/Supplemental/Courier New.ttf",
            ],
            _ => [],
        };
        string[] candidates = [.. family, .. sans];
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(Path.GetDirectoryName(c)) && File.Exists(c));
    }

    // ------------------------------------------------------------------ reading and editing text

    /// <summary>Whole text of a page in reading order.</summary>
    public string GetPageText(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == IntPtr.Zero) return "";
        try
        {
            int count = Native.FPDFText_CountChars(textPage);
            if (count <= 0) return "";
            var buffer = new char[count + 1];
            int written;
            fixed (char* p = buffer) written = Native.FPDFText_GetText(textPage, 0, count, p);
            return new string(buffer, 0, Math.Max(0, written - 1));
        }
        finally { Native.FPDFText_ClosePage(textPage); }
    }

    /// <summary>
    /// Lists the editable text runs on a page. Object indices are valid until the page's
    /// content is next changed, so list again after every edit.
    /// </summary>
    public IReadOnlyList<TextRun> GetTextRuns(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var runs = new List<TextRun>();
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        try
        {
            int count = Native.FPDFPage_CountObjects(page.Handle);
            for (int i = 0; i < count; i++)
            {
                var obj = Native.FPDFPage_GetObject(page.Handle, i);
                if (Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_TEXT) continue;
                float size = 0;
                Native.FPDFTextObj_GetFontSize(obj, &size);
                uint r = 0, g = 0, b = 0, a = 255;
                Native.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a);
                string text = textPage == IntPtr.Zero ? "" : ReadObjectText(obj, textPage).TrimEnd('\r', '\n', '\0');
                runs.Add(new TextRun(i, text, ObjectBounds(page, obj), FontName(obj), size, new PdfColor((byte)r, (byte)g, (byte)b, (byte)a)));
            }
        }
        finally { if (textPage != IntPtr.Zero) Native.FPDFText_ClosePage(textPage); }
        return runs;
    }

    /// <summary>The text run under a point, or null. Useful for click-to-edit.</summary>
    public TextRun? FindTextRunAt(int pageIndex, double x, double y) =>
        GetTextRuns(pageIndex).LastOrDefault(r => r.Bounds.Contains(x, y));

    /// <summary>
    /// Replaces the text of an existing run, keeping its position, size and colour. The original
    /// font is kept when it contains the needed characters (embedded fonts are often cut down to
    /// the characters already used); otherwise a similar font is substituted. Text does not reflow.
    /// </summary>
    public TextEditResult EditText(int pageIndex, int objectIndex, string newText)
    {
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex, Native.PAGEOBJ_TEXT, "text");
        page.Dirty = page.ChangedExisting = true;

        if (string.IsNullOrEmpty(newText))
        {
            Native.FPDFPage_RemoveObject(page.Handle, obj);
            Native.FPDFPageObj_Destroy(obj);
            return TextEditResult.Removed;
        }

        // Capture the run's look before touching it, in case the font has to be replaced.
        string fontName = FontName(obj);
        float size = 12;
        Native.FPDFTextObj_GetFontSize(obj, &size);
        uint r = 0, g = 0, b = 0, a = 255;
        Native.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a);
        Native.FS_MATRIX m;
        Native.FPDFPageObj_GetMatrix(obj, &m);

        if (Native.FPDFText_SetText(obj, newText) != 0 && ShowsText(page, obj, newText))
            return TextEditResult.KeptFont;

        Native.FPDFPage_RemoveObject(page.Handle, obj);
        Native.FPDFPageObj_Destroy(obj);
        var style = new TextStyle
        {
            Font = fontName.Contains("Courier", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Mono", StringComparison.OrdinalIgnoreCase) ? StandardFont.Courier
                 : fontName.Contains("Times", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !fontName.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? StandardFont.Times
                 : StandardFont.Helvetica,
            Bold = fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase),
            Italic = fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase),
            Size = size <= 0 ? 12 : size,
            Color = new PdfColor((byte)r, (byte)g, (byte)b, (byte)a),
        };
        InsertTextObject(page, newText, style, new Affine(m.a, m.b, m.c, m.d, m.e, m.f), out _);
        return TextEditResult.SubstitutedFont;
    }

    internal static string ReadObjectText(IntPtr textObj, IntPtr textPage)
    {
        int bytes = (int)Native.FPDFTextObj_GetText(textObj, textPage, null, new CULong(0)).Value;
        if (bytes <= 2) return "";
        var buffer = new char[bytes / 2];
        fixed (char* p = buffer) Native.FPDFTextObj_GetText(textObj, textPage, p, new CULong((uint)bytes));
        return new string(buffer, 0, buffer.Length - 1);
    }

    internal static string FontName(IntPtr textObj)
    {
        var font = Native.FPDFTextObj_GetFont(textObj);
        if (font == IntPtr.Zero) return "";
        int length = (int)Native.FPDFFont_GetBaseFontName(font, null, 0);
        if (length <= 1) return "";
        var buffer = new byte[length];
        fixed (byte* p = buffer) Native.FPDFFont_GetBaseFontName(font, p, (nuint)length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length - 1);
    }

    // ------------------------------------------------------------------ images

    /// <summary>
    /// Places an image (JPEG, PNG, BMP, GIF or TGA) on a page. Give a width, a height, both or
    /// neither: missing dimensions keep the aspect ratio, and with neither the image is placed
    /// at 72 pixels per inch.
    /// </summary>
    public void AddImage(int pageIndex, byte[] imageData, double x, double y, double? width = null, double? height = null)
    {
        using var page = OpenPage(pageIndex);
        var obj = Native.FPDFPageObj_NewImageObj(Doc);
        if (obj == IntPtr.Zero) throw new PdfException("Could not create the image.");
        try
        {
            bool isJpeg = imageData.Length > 3 && imageData[0] == 0xFF && imageData[1] == 0xD8;
            if (isJpeg) LoadJpeg(page, obj, imageData); // stored as-is, without recompression
            else
            {
                ImageResult decoded;
                try { decoded = ImageResult.FromMemory(imageData, ColorComponents.RedGreenBlueAlpha); }
                catch (Exception e) { throw new PdfException("The image format is not supported or the file is damaged: " + e.Message); }
                var bgra = decoded.Data;
                for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                SetPixels(page, obj, bgra, decoded.Width, decoded.Height);
            }
            PlaceImage(page, obj, x, y, width, height);
        }
        catch
        {
            Native.FPDFPageObj_Destroy(obj);
            throw;
        }
    }

    /// <summary>Places a PNG/JPEG/BMP/GIF image into a rectangle (view space) on an open page.</summary>
    internal void DrawImage(PageHandle page, byte[] imageData, double x, double y, double width, double height)
    {
        var obj = Native.FPDFPageObj_NewImageObj(Doc);
        if (obj == IntPtr.Zero) throw new PdfException("Could not create the image.");
        try
        {
            if (imageData.Length > 3 && imageData[0] == 0xFF && imageData[1] == 0xD8) LoadJpeg(page, obj, imageData);
            else
            {
                var decoded = ImageResult.FromMemory(imageData, ColorComponents.RedGreenBlueAlpha);
                var bgra = decoded.Data;
                for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                SetPixels(page, obj, bgra, decoded.Width, decoded.Height);
            }
            PlaceImage(page, obj, x, y, width, height);
        }
        catch
        {
            Native.FPDFPageObj_Destroy(obj);
            throw;
        }
    }

    /// <summary>Places an image given as top-down BGRA pixels (4 bytes per pixel).</summary>
    public void AddImage(int pageIndex, byte[] bgraPixels, int pixelWidth, int pixelHeight, double x, double y, double? width = null, double? height = null)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || bgraPixels.Length < (long)pixelWidth * pixelHeight * 4)
            throw new ArgumentException("Pixel buffer does not match the stated image size.");
        using var page = OpenPage(pageIndex);
        var obj = Native.FPDFPageObj_NewImageObj(Doc);
        if (obj == IntPtr.Zero) throw new PdfException("Could not create the image.");
        try
        {
            SetPixels(page, obj, bgraPixels, pixelWidth, pixelHeight);
            PlaceImage(page, obj, x, y, width, height);
        }
        catch
        {
            Native.FPDFPageObj_Destroy(obj);
            throw;
        }
    }

    private static void SetPixels(PageHandle page, IntPtr obj, byte[] bgra, int w, int h)
    {
        fixed (byte* p = bgra)
        {
            var bitmap = Native.FPDFBitmap_CreateEx(w, h, Native.FPDFBitmap_BGRA, p, w * 4);
            if (bitmap == IntPtr.Zero) throw new PdfException("Could not read the image pixels.");
            try
            {
                var handle = page.Handle;
                if (Native.FPDFImageObj_SetBitmap(&handle, 1, obj, bitmap) == 0)
                    throw new PdfException("Could not embed the image.");
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        }
    }

    private static void LoadJpeg(PageHandle page, IntPtr obj, byte[] jpeg)
    {
        var pin = GCHandle.Alloc(jpeg);
        try
        {
            var access = new Native.FPDF_FILEACCESS
            {
                m_FileLen = new CULong((uint)jpeg.Length),
                m_GetBlock = &ReadBlock,
                m_Param = (void*)GCHandle.ToIntPtr(pin),
            };
            var handle = page.Handle;
            if (Native.FPDFImageObj_LoadJpegFileInline(&handle, 1, obj, &access) == 0)
                throw new PdfException("The JPEG image is damaged or unsupported.");
        }
        finally { pin.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadBlock(void* param, CULong position, byte* buffer, CULong size)
    {
        try
        {
            var data = (byte[])GCHandle.FromIntPtr((IntPtr)param).Target!;
            long pos = (long)position.Value, len = (long)size.Value;
            if (pos + len > data.Length) return 0;
            new ReadOnlySpan<byte>(data, (int)pos, (int)len).CopyTo(new Span<byte>(buffer, (int)len));
            return 1;
        }
        catch { return 0; }
    }

    private static void PlaceImage(PageHandle page, IntPtr obj, double x, double y, double? width, double? height)
    {
        uint pw = 0, ph = 0;
        if (Native.FPDFImageObj_GetImagePixelSize(obj, &pw, &ph) == 0 || pw == 0 || ph == 0)
            throw new PdfException("The image has no pixels.");
        double w = width ?? (height is { } hh ? hh * pw / ph : pw);
        double h = height ?? (width is { } ww ? ww * ph / pw : ph);
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");

        // An image fills the unit square with y up; map that onto the target rectangle in view space.
        var m = Affine.Concat(new Affine(w, 0, 0, -h, x, y + h), page.ViewToUser).ToNative();
        Native.FPDFPageObj_SetMatrix(obj, &m);
        Native.FPDFPage_InsertObject(page.Handle, obj);
        page.Dirty = true;
    }

    /// <summary>Moves and resizes an existing image to fill the given rectangle.</summary>
    public void SetImageBounds(int pageIndex, int objectIndex, PdfRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds), "Image size must be positive.");
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex, Native.PAGEOBJ_IMAGE, "image");
        var m = Affine.Concat(new Affine(bounds.Width, 0, 0, -bounds.Height, bounds.X, bounds.Bottom), page.ViewToUser).ToNative();
        Native.FPDFPageObj_SetMatrix(obj, &m);
        page.Dirty = page.ChangedExisting = true;
    }

    public IReadOnlyList<ImageInfo> GetImages(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var images = new List<ImageInfo>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            if (Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_IMAGE) continue;
            uint pw = 0, ph = 0;
            Native.FPDFImageObj_GetImagePixelSize(obj, &pw, &ph);
            images.Add(new ImageInfo(i, ObjectBounds(page, obj), (int)pw, (int)ph));
        }
        return images;
    }

    // ------------------------------------------------------------------ shared object operations

    /// <summary>Removes a text run or image by its object index.</summary>
    public void RemoveObject(int pageIndex, int objectIndex)
    {
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex);
        if (Native.FPDFPage_RemoveObject(page.Handle, obj) == 0) throw new PdfException("Could not remove the item.");
        Native.FPDFPageObj_Destroy(obj);
        page.Dirty = page.ChangedExisting = true;
    }

    /// <summary>Moves a text run or image by an offset in view space.</summary>
    public void MoveObject(int pageIndex, int objectIndex, double dx, double dy)
    {
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex);
        Native.FS_MATRIX m;
        if (Native.FPDFPageObj_GetMatrix(obj, &m) == 0) throw new PdfException("Could not move the item.");
        // Only the direction of the offset changes between spaces, not the origin.
        var v = page.ViewToUser;
        m.e += (float)(v.A * dx + v.C * dy);
        m.f += (float)(v.B * dx + v.D * dy);
        Native.FPDFPageObj_SetMatrix(obj, &m);
        page.Dirty = page.ChangedExisting = true;
    }

    private static IntPtr GetObject(PageHandle page, int objectIndex, int? requiredType = null, string what = "item")
    {
        if (objectIndex < 0 || objectIndex >= Native.FPDFPage_CountObjects(page.Handle))
            throw new ArgumentOutOfRangeException(nameof(objectIndex));
        var obj = Native.FPDFPage_GetObject(page.Handle, objectIndex);
        if (obj == IntPtr.Zero || (requiredType is { } t && Native.FPDFPageObj_GetType(obj) != t))
            throw new ArgumentException($"Object {objectIndex} is not a {what} item.", nameof(objectIndex));
        return obj;
    }

    private static PdfRect ObjectBounds(PageHandle page, IntPtr obj)
    {
        float l = 0, b = 0, r = 0, t = 0;
        Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t);
        return page.UserToView.MapBounds(l, b, r, t);
    }
}
