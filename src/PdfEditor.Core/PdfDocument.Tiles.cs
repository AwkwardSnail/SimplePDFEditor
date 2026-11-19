using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>How a large page is spread over sheets of ordinary paper. Lengths are in points.</summary>
public sealed record TileOptions
{
    /// <summary>The paper the tiles are printed on, the way it will be fed (so swap the two for landscape).</summary>
    public double PaperWidth { get; init; } = 595.28;
    public double PaperHeight { get; init; } = 841.89;

    /// <summary>The edge of the paper the printer cannot reach; it is left blank.</summary>
    public double Margin { get; init; } = 5 * 72 / 25.4;

    /// <summary>How much of the picture neighbouring sheets share, for gluing one over the other.</summary>
    public double Overlap { get; init; } = 10 * 72 / 25.4;

    /// <summary>1 prints the page at its real size; 2 doubles its width and height.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Small marks in the margins showing where to trim and where the next sheet goes, and each sheet's place.</summary>
    public bool CutMarks { get; init; } = true;
}

/// <summary>What a set of tile options comes to for a page: how many sheets, and how big the result is.</summary>
public readonly record struct TileLayout(int Columns, int Rows, double Scale, double PosterWidth, double PosterHeight)
{
    public int Sheets => Columns * Rows;
}

public sealed unsafe partial class PdfDocument
{
    /// <summary>The part of each sheet that carries picture, and how far apart neighbouring sheets start.</summary>
    private static (double WindowW, double WindowH, double StepX, double StepY) TileGrid(TileOptions o)
    {
        if (o.Margin < 0 || o.Overlap < 0 || o.Scale <= 0) throw new ArgumentException("The margin, overlap and scale cannot be negative.");
        double windowW = o.PaperWidth - 2 * o.Margin, windowH = o.PaperHeight - 2 * o.Margin;
        double stepX = windowW - o.Overlap, stepY = windowH - o.Overlap;
        if (stepX < 36 || stepY < 36) throw new ArgumentException("The margin and overlap leave too little of the paper to print on.");
        return (windowW, windowH, stepX, stepY);
    }

    /// <summary>How many sheets a page of the given size needs with these options.</summary>
    public static TileLayout PlanTiles(PdfSize page, TileOptions options)
    {
        var (_, _, stepX, stepY) = TileGrid(options);
        double w = page.Width * options.Scale, h = page.Height * options.Scale;
        int columns = Math.Max(1, (int)Math.Ceiling((w - options.Overlap) / stepX - 1e-6));
        int rows = Math.Max(1, (int)Math.Ceiling((h - options.Overlap) / stepY - 1e-6));
        return new TileLayout(columns, rows, options.Scale, w, h);
    }

    /// <summary>The largest scale at which a page fits on a given number of sheets across and down.</summary>
    public static double TileScaleToFit(PdfSize page, TileOptions options, int across, int down)
    {
        if (across < 1 || down < 1) throw new ArgumentException("At least one sheet each way is needed.");
        var (_, _, stepX, stepY) = TileGrid(options);
        return Math.Min((across * stepX + options.Overlap) / page.Width, (down * stepY + options.Overlap) / page.Height);
    }

    /// <summary>
    /// Makes a new document that prints one page of this one across several sheets of paper, to be
    /// trimmed and joined into a poster. This document is not changed. The page is stored once in
    /// the new file and each sheet shows its own part of it, so the file stays about as small as
    /// the page itself however many sheets there are. Sheets run left to right, top row first.
    /// </summary>
    public PdfDocument CreateTiles(int pageIndex, TileOptions options)
    {
        var size = GetPageSize(pageIndex);
        var layout = PlanTiles(size, options);
        var (windowW, windowH, stepX, stepY) = TileGrid(options);
        if (layout.Sheets > 400) throw new PdfException($"That would take {layout.Sheets} sheets. Choose a smaller size or larger paper.");
        double margin = options.Margin, paperW = options.PaperWidth, paperH = options.PaperHeight;

        // The whole poster sits centred on the area the sheets cover together.
        double offsetX = (layout.Columns * stepX + options.Overlap - layout.PosterWidth) / 2;
        double offsetY = (layout.Rows * stepY + options.Overlap - layout.PosterHeight) / 2;

        // What is on the page, as one box in the page as seen (top-left origin). Used below to find
        // out where PDFium's copy of the page puts things.
        double seenLeft = double.MaxValue, seenTop = double.MaxValue, seenRight = double.MinValue, seenBottom = double.MinValue;
        using (var source = OpenPage(pageIndex))
        {
            int count = Native.FPDFPage_CountObjects(source.Handle);
            for (int i = 0; i < count; i++)
            {
                float l = 0, b = 0, r = 0, t = 0;
                if (Native.FPDFPageObj_GetBounds(Native.FPDFPage_GetObject(source.Handle, i), &l, &b, &r, &t) == 0) continue;
                var box = source.UserToView.MapBounds(l, b, r, t);
                seenLeft = Math.Min(seenLeft, box.X);
                seenTop = Math.Min(seenTop, box.Y);
                seenRight = Math.Max(seenRight, box.Right);
                seenBottom = Math.Max(seenBottom, box.Bottom);
            }
        }

        var result = Create();
        IntPtr shared = IntPtr.Zero;
        try
        {
            shared = Native.FPDF_NewXObjectFromPage(result.Doc, Doc, pageIndex);
            if (shared == IntPtr.Zero) throw new PdfException("The page could not be prepared for printing as tiles.");

            // PDFium's copy shows the page the way it is seen (turned pages are turned), with y
            // running up, but where its origin lies is not documented. Measuring where the copy
            // puts the page's content tells: a point seen at (x, y) is at (x + shiftX, shiftY - y).
            double shiftX = 0, shiftY = size.Height;
            if (seenRight > seenLeft)
            {
                var probe = Native.FPDF_NewFormObjectFromXObject(shared);
                float l = 0, b = 0, r = 0, t = 0;
                bool measured = probe != IntPtr.Zero && Native.FPDFPageObj_GetBounds(probe, &l, &b, &r, &t) != 0;
                if (probe != IntPtr.Zero) Native.FPDFPageObj_Destroy(probe);
                if (measured && Math.Abs((r - l) - (seenRight - seenLeft)) < 2 && Math.Abs((t - b) - (seenBottom - seenTop)) < 2)
                {
                    shiftX = l - seenLeft;
                    shiftY = t + seenTop;
                }
            }

            for (int row = 0; row < layout.Rows; row++)
                for (int column = 0; column < layout.Columns; column++)
                {
                    int index = row * layout.Columns + column;
                    var sheet = Native.FPDFPage_New(result.Doc, index, paperW, paperH);
                    if (sheet == IntPtr.Zero) throw new PdfException("Could not add a sheet.");
                    try
                    {
                        // Where the poster's top-left corner falls on this sheet, measured from the sheet's top-left.
                        double left = margin + offsetX - column * stepX, top = margin + offsetY - row * stepY;

                        var picture = Native.FPDF_NewFormObjectFromXObject(shared);
                        if (picture == IntPtr.Zero) throw new PdfException("The page could not be placed on a sheet.");
                        // Scaled and moved into place, in the sheet's coordinates (which start at its bottom-left).
                        double scale = options.Scale;
                        var m = new Native.FS_MATRIX
                        {
                            a = (float)scale, d = (float)scale,
                            e = (float)(left - scale * shiftX), f = (float)(paperH - top - scale * shiftY),
                        };
                        Native.FPDFPageObj_SetMatrix(picture, &m);
                        Native.FPDFPage_InsertObject(sheet, picture);

                        // Blank the edge the printer cannot reach, so nothing is half printed there.
                        if (margin > 0)
                        {
                            Box(sheet, 0, 0, paperW, margin, paperH);
                            Box(sheet, 0, paperH - margin, paperW, margin, paperH);
                            Box(sheet, 0, 0, margin, paperH, paperH);
                            Box(sheet, paperW - margin, 0, margin, paperH, paperH);
                        }

                        if (options.CutMarks && margin >= 8)
                        {
                            bool lastColumn = column == layout.Columns - 1, lastRow = row == layout.Rows - 1;
                            // Trim the left and top edges; the right and bottom marks show where the
                            // next sheet's trimmed edge lands (or, on the outside, where the poster ends).
                            double trimLeft = column == 0 ? left : margin, trimTop = row == 0 ? top : margin;
                            double nextLeft = lastColumn ? left + layout.PosterWidth : margin + stepX;
                            double nextTop = lastRow ? top + layout.PosterHeight : margin + stepY;
                            foreach (double x in new[] { trimLeft, nextLeft })
                            {
                                if (x < margin - 0.01 || x > paperW - margin + 0.01) continue;
                                Mark(sheet, x, 2, x, margin - 2, paperH);
                                Mark(sheet, x, paperH - margin + 2, x, paperH - 2, paperH);
                            }
                            foreach (double y in new[] { trimTop, nextTop })
                            {
                                if (y < margin - 0.01 || y > paperH - margin + 0.01) continue;
                                Mark(sheet, 2, y, margin - 2, y, paperH);
                                Mark(sheet, paperW - margin + 2, y, paperW - 2, y, paperH);
                            }
                        }
                        Native.FPDFPage_GenerateContent(sheet);
                    }
                    finally { Native.FPDF_ClosePage(sheet); }

                    if (options.CutMarks && margin >= 8 && layout.Sheets > 1)
                        result.AddText(index, margin + 14, Math.Max(1, (margin - 6) / 2 - 1), $"Row {row + 1}, column {column + 1}",
                            new TextStyle { Size = 6, Color = new PdfColor(110, 110, 110) });
                }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally
        {
            if (shared != IntPtr.Zero) Native.FPDF_CloseXObject(shared);
        }

        // Both helpers take positions measured from the sheet's top-left corner.
        static void Box(IntPtr sheet, double x, double y, double w, double h, double sheetHeight)
        {
            var box = Native.FPDFPageObj_CreateNewRect((float)x, (float)(sheetHeight - y - h), (float)w, (float)h);
            Native.FPDFPageObj_SetFillColor(box, 255, 255, 255, 255);
            Native.FPDFPath_SetDrawMode(box, 1, 0);
            Native.FPDFPage_InsertObject(sheet, box);
        }

        static void Mark(IntPtr sheet, double x1, double y1, double x2, double y2, double sheetHeight)
        {
            var line = Native.FPDFPageObj_CreateNewPath((float)x1, (float)(sheetHeight - y1));
            Native.FPDFPath_LineTo(line, (float)x2, (float)(sheetHeight - y2));
            Native.FPDFPageObj_SetStrokeColor(line, 0, 0, 0, 255);
            Native.FPDFPageObj_SetStrokeWidth(line, 0.5f);
            Native.FPDFPath_SetDrawMode(line, 0, 1);
            Native.FPDFPage_InsertObject(sheet, line);
        }
    }
}
