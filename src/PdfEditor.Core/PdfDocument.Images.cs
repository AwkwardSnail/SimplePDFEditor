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
    public RenderedPage GetImagePixels(int pageIndex, int objectIndex)
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
            return new RenderedPage(w, h, w * 4, bgra);
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    }

    /// <summary>
    /// Puts a different picture (JPEG, PNG, BMP, GIF or TGA) in an image's place. It keeps the
    /// image's position and orientation; if the new picture has other proportions it is fitted
    /// inside the old outline, centred, without being stretched.
    /// </summary>
    public void ReplaceImage(int pageIndex, int objectIndex, byte[] imageData)
    {
        using var page = OpenPage(pageIndex);
        var obj = GetObject(page, objectIndex, Native.PAGEOBJ_IMAGE, "image");
        uint oldW = 0, oldH = 0;
        Native.FPDFImageObj_GetImagePixelSize(obj, &oldW, &oldH);

        if (imageData.Length > 3 && imageData[0] == 0xFF && imageData[1] == 0xD8) LoadJpeg(page, obj, imageData);
        else
        {
            ImageResult decoded;
            try { decoded = ImageResult.FromMemory(imageData, ColorComponents.RedGreenBlueAlpha); }
            catch (Exception e) { throw new PdfException("The image format is not supported or the file is damaged: " + e.Message); }
            var bgra = decoded.Data;
            for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            SetPixels(page, obj, bgra, decoded.Width, decoded.Height);
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
        var placement = Affine.Concat(new Affine(m.a, m.b, m.c, m.d, m.e, m.f), page.UserToView);
        for (int i = 0; i < turns; i++) placement = Affine.Concat(placement, quarter);
        var turned = Affine.Concat(placement, page.ViewToUser).ToNative();
        Native.FPDFPageObj_SetMatrix(obj, &turned);
        page.Dirty = page.ChangedExisting = true;
    }
}
