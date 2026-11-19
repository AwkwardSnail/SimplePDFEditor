using PdfEditor.Core.Interop;
using StbImageSharp;

namespace PdfEditor.Core;

// Working on images already on a page.
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// The pixels of an image as stored in the PDF (top-down BGRA), at its full resolution and
    /// in its stored orientation, whatever size and angle it is shown at on the page.
    /// </summary>
    /// <param name="asDisplayed">
    /// Turn the pixels the way the image is seen on the page (a scan stored sideways on a rotated
    /// page comes out upright). Only for images shown at a multiple of 90 degrees; others come as stored.
    /// </param>
    public RenderedPage GetImagePixels(int pageIndex, int objectIndex, bool asDisplayed = false)
    {
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex, Native.PAGEOBJ_IMAGE, "image");
        var bitmap = Native.FPDFImageObj_GetBitmap(obj);
        if (bitmap == IntPtr.Zero) throw new PdfException("The image's pixels could not be read.");
        try
        {
            int w = Native.FPDFBitmap_GetWidth(bitmap), h = Native.FPDFBitmap_GetHeight(bitmap), stride = Native.FPDFBitmap_GetStride(bitmap);
            int format = Native.FPDFBitmap_GetFormat(bitmap); // 1 grey, 2 BGR, 3 BGRx, 4 BGRA
            byte* pixels = (byte*)Native.FPDFBitmap_GetBuffer(bitmap);
            if (w <= 0 || h <= 0 || pixels == null || format is < 1 or > 4) throw new PdfException("The image is in a form that cannot be read.");
            var bgra = new byte[checked(w * h * 4)];
            for (int y = 0; y < h; y++)
            {
                byte* row = pixels + (long)y * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    switch (format)
                    {
                        case 1: bgra[o] = bgra[o + 1] = bgra[o + 2] = row[x]; bgra[o + 3] = 255; break;
                        case 2: bgra[o] = row[x * 3]; bgra[o + 1] = row[x * 3 + 1]; bgra[o + 2] = row[x * 3 + 2]; bgra[o + 3] = 255; break;
                        default:
                            bgra[o] = row[x * 4]; bgra[o + 1] = row[x * 4 + 1]; bgra[o + 2] = row[x * 4 + 2];
                            bgra[o + 3] = format == 4 ? row[x * 4 + 3] : (byte)255;
                            break;
                    }
                }
            }
            if (asDisplayed && DisplayTurn(page, obj) is { } turn && !turn.IsIdentity)
            {
                var shown = Reorient(bgra, w, h, turn, toDisplayed: true, out int sw, out int sh);
                return new RenderedPage(sw, sh, sw * 4, shown);
            }
            return new RenderedPage(w, h, w * 4, bgra);
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    }

    /// <summary>
    /// Puts a different picture (JPEG, PNG, BMP, GIF or TGA) in an image's place. It keeps the
    /// image's position and orientation; if the new picture has other proportions it is fitted
    /// inside the old outline, centred, without being stretched.
    /// </summary>
    /// <param name="asDisplayed">
    /// The new picture is the way it should be seen on the page; it is turned to match how the
    /// image is stored (see <see cref="GetImagePixels"/>). Without this, a picture replacing a scan
    /// that is stored sideways would itself appear sideways.
    /// </param>
    public void ReplaceImage(int pageIndex, int objectIndex, byte[] imageData, bool asDisplayed = false)
    {
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex, Native.PAGEOBJ_IMAGE, "image");
        uint oldW = 0, oldH = 0;
        Native.FPDFImageObj_GetImagePixelSize(obj, &oldW, &oldH);

        var turn = asDisplayed ? DisplayTurn(page, obj) : null;
        bool turned = turn is { IsIdentity: false };
        // A JPEG goes in as it is, without recompression, unless its pixels have to be turned.
        if (!turned && imageData.Length > 3 && imageData[0] == 0xFF && imageData[1] == 0xD8) LoadJpeg(page, obj, imageData);
        else
        {
            ImageResult decoded;
            try { decoded = ImageResult.FromMemory(imageData, ColorComponents.RedGreenBlueAlpha); }
            catch (Exception e) { throw new PdfException("The image format is not supported or the file is damaged: " + e.Message); }
            var bgra = decoded.Data;
            for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            int pw = decoded.Width, ph = decoded.Height;
            if (turned) bgra = Reorient(bgra, pw, ph, turn!.Value, toDisplayed: false, out pw, out ph);
            SetPixels(page, obj, bgra, pw, ph);
        }
        page.Dirty = page.ChangedExisting = true;

        // Other proportions: shrink the placement along one side so the picture is not distorted.
        // The placement's own axes are used, so this also works for an image shown rotated.
        uint newW = 0, newH = 0;
        Native.FPDFImageObj_GetImagePixelSize(obj, &newW, &newH);
        if (oldW == 0 || oldH == 0 || newW == 0 || newH == 0) return;
        // Same proportions as the picture it replaces (an edited copy, say): leave the placement alone.
        if (Math.Abs(((double)newW / newH) / ((double)oldW / oldH) - 1) < 0.01) return;
        Native.FS_MATRIX m;
        if (Native.FPDFPageObj_GetMatrix(obj, &m) == 0) return;
        double shownWidth = Math.Sqrt(m.a * m.a + m.b * m.b), shownHeight = Math.Sqrt(m.c * m.c + m.d * m.d);
        if (shownWidth <= 0 || shownHeight <= 0) return;
        double change = ((double)newW / newH) / (shownWidth / shownHeight);
        if (Math.Abs(change - 1) < 0.01) return;
        // The image fills the unit square; (a,b) is its width edge and (c,d) its height edge.
        double sx = change < 1 ? change : 1, sy = change < 1 ? 1 : 1 / change;
        var fitted = new Native.FS_MATRIX
        {
            a = (float)(m.a * sx), b = (float)(m.b * sx), c = (float)(m.c * sy), d = (float)(m.d * sy),
            e = (float)(m.e + m.a * (1 - sx) / 2 + m.c * (1 - sy) / 2),
            f = (float)(m.f + m.b * (1 - sx) / 2 + m.d * (1 - sy) / 2),
        };
        Native.FPDFPageObj_SetMatrix(obj, &fitted);
    }

    /// <summary>
    /// How an image's stored pixels relate to what is seen on the page, when it is shown at a
    /// multiple of 90 degrees (possibly mirrored): whether width and height swap, and which
    /// sides are reversed.
    /// </summary>
    private readonly record struct Turn(bool Swap, bool ReverseU, bool ReverseV)
    {
        public bool IsIdentity => !Swap && !ReverseU && !ReverseV;
    }

    /// <summary>The turn an image is shown with, or null when it is at some other angle or skewed.</summary>
    private static Turn? DisplayTurn(PageHandle page, IntPtr obj)
    {
        Native.FS_MATRIX m;
        if (Native.FPDFPageObj_GetMatrix(obj, &m) == 0) return null;
        // In view space: where the image's width edge (u) and height edge (v, pointing to its top) go.
        var p = Affine.Concat(new Affine(m.a, m.b, m.c, m.d, 0, 0), page.UserToView.WithoutTranslation());
        static bool Along(double major, double minor) => Math.Abs(major) > 1e-9 && Math.Abs(minor) < 0.01 * Math.Abs(major);
        if (Along(p.A, p.B) && Along(p.D, p.C))
            return new Turn(false, ReverseU: p.A < 0, ReverseV: p.D > 0);   // upright has u to the right and v up (negative view y)
        if (Along(p.B, p.A) && Along(p.C, p.D))
            return new Turn(true, ReverseU: p.B < 0, ReverseV: p.C < 0);    // u runs down the page, v runs across
        return null;
    }

    /// <summary>Rearranges pixels between the stored layout and the displayed one.</summary>
    private static byte[] Reorient(byte[] source, int width, int height, Turn turn, bool toDisplayed, out int outWidth, out int outHeight)
    {
        // Stored size, whichever way we are converting.
        int storedW = toDisplayed || !turn.Swap ? width : height;
        int storedH = toDisplayed || !turn.Swap ? height : width;
        int shownW = turn.Swap ? storedH : storedW, shownH = turn.Swap ? storedW : storedH;
        (outWidth, outHeight) = toDisplayed ? (shownW, shownH) : (storedW, storedH);
        var result = new byte[source.Length];
        for (int sy = 0; sy < storedH; sy++)
            for (int sx = 0; sx < storedW; sx++)
            {
                // Where stored pixel (sx, sy) is seen. Stored rows run from the image's top (v = 1) down.
                int dx, dy;
                if (!turn.Swap)
                {
                    dx = turn.ReverseU ? storedW - 1 - sx : sx;
                    dy = turn.ReverseV ? storedH - 1 - sy : sy;
                }
                else
                {
                    dy = turn.ReverseU ? storedW - 1 - sx : sx;   // u runs down the page (or up, reversed)
                    dx = turn.ReverseV ? sy : storedH - 1 - sy;   // v (towards the image's top) runs right, so rows run left
                }
                int stored = (sy * storedW + sx) * 4, shown = (dy * shownW + dx) * 4;
                if (toDisplayed) Buffer.BlockCopy(source, stored, result, shown, 4);
                else Buffer.BlockCopy(source, shown, result, stored, 4);
            }
        return result;
    }

    /// <summary>Turns an image about its centre, clockwise as seen on the page, by a multiple of 90 degrees.</summary>
    public void RotateImage(int pageIndex, int objectIndex, int degreesClockwise)
    {
        if (degreesClockwise % 90 != 0) throw new ArgumentException("Images turn in steps of 90 degrees.", nameof(degreesClockwise));
        int turns = ((degreesClockwise / 90) % 4 + 4) % 4;
        if (turns == 0) return;
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex, Native.PAGEOBJ_IMAGE, "image");
        var bounds = ObjectBounds(page, obj);
        Native.FS_MATRIX m;
        if (Native.FPDFPageObj_GetMatrix(obj, &m) == 0) throw new PdfException("Could not rotate the image.");
        double cx = bounds.X + bounds.Width / 2, cy = bounds.Y + bounds.Height / 2;
        // A quarter turn clockwise in view space (y runs down): (x, y) -> (cx - (y - cy), cy + (x - cx)).
        var quarter = new Affine(0, 1, -1, 0, cx + cy, cy - cx);
        var turn = page.UserToView;
        for (int i = 0; i < turns; i++) turn = Affine.Concat(turn, quarter);
        TransformObject(obj, Affine.Concat(turn, page.ViewToUser));
        page.Dirty = page.ChangedExisting = true;
    }
}
