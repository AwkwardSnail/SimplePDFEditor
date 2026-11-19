using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>
/// An editable PDF document. Pages are addressed by zero-based index and all coordinates are
/// in view space (see <see cref="PdfRect"/>). Not thread-safe: use from one thread at a time.
/// </summary>
public sealed unsafe partial class PdfDocument : IDisposable
{
    private IntPtr _doc;
    private GCHandle _sourcePin; // PDFium reads lazily from the original buffer, so keep it pinned
    private readonly Dictionary<string, IntPtr> _fonts = new(StringComparer.OrdinalIgnoreCase);

    static PdfDocument() => Native.FPDF_InitLibrary();

    private PdfDocument(IntPtr doc, GCHandle pin = default)
    {
        _doc = doc;
        _sourcePin = pin;
    }

    // ------------------------------------------------------------------ open / save

    /// <summary>Creates an empty document with no pages.</summary>
    public static PdfDocument Create()
    {
        var doc = Native.FPDF_CreateNewDocument();
        if (doc == IntPtr.Zero) throw new PdfException("Could not create a new PDF document.");
        return new PdfDocument(doc);
    }

    public static PdfDocument Open(string path, string? password = null) => Open(File.ReadAllBytes(path), password);

    public static PdfDocument Open(byte[] data, string? password = null)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        var doc = Native.FPDF_LoadMemDocument((void*)pin.AddrOfPinnedObject(), data.Length, password);
        if (doc == IntPtr.Zero)
        {
            pin.Free();
            throw new PdfException(Native.FPDF_GetLastError().Value switch
            {
                3 => "The file is not a valid PDF or is corrupted.",
                4 => "The PDF is password protected and the password is missing or wrong.",
                5 => "The PDF uses an unsupported security scheme.",
                _ => "The PDF could not be opened.",
            });
        }
        return new PdfDocument(doc, pin);
    }

    public void Save(string path)
    {
        // Write to memory first so a failed save never truncates an existing file.
        File.WriteAllBytes(path, ToBytes());
    }

    public void Save(Stream stream)
    {
        var handle = GCHandle.Alloc(stream);
        try
        {
            var writer = new Native.FPDF_FILEWRITE { version = 1, WriteBlock = &WriteBlock, context = GCHandle.ToIntPtr(handle) };
            if (Native.FPDF_SaveAsCopy(Doc, &writer, Native.FPDF_NO_INCREMENTAL) == 0)
                throw new PdfException("Saving the PDF failed.");
        }
        finally { handle.Free(); }
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        Save(ms);
        return ms.ToArray();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(Native.FPDF_FILEWRITE* self, void* data, CULong size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->context).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (_doc == IntPtr.Zero) return;
        foreach (var font in _fonts.Values) Native.FPDFFont_Close(font);
        foreach (var font in _standardFonts.Values) Native.FPDFFont_Close(font);
        _fonts.Clear();
        _standardFonts.Clear();
        Native.FPDF_CloseDocument(_doc);
        _doc = IntPtr.Zero;
        if (_sourcePin.IsAllocated) _sourcePin.Free();
    }

    internal IntPtr Doc => _doc != IntPtr.Zero ? _doc : throw new ObjectDisposedException(nameof(PdfDocument));

    // ------------------------------------------------------------------ pages

    public int PageCount => Native.FPDF_GetPageCount(Doc);

    public PdfSize GetPageSize(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        return new PdfSize(page.Width, page.Height);
    }

    /// <summary>Adds a blank page (A4 by default). Returns its index.</summary>
    /// <param name="index">Position to insert at; null appends at the end.</param>
    public int AddPage(double width = 595, double height = 842, int? index = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Page size must be positive.");
        int at = index ?? PageCount;
        if (at < 0 || at > PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        var page = Native.FPDFPage_New(Doc, at, width, height);
        if (page == IntPtr.Zero) throw new PdfException("Could not add a page.");
        Native.FPDFPage_GenerateContent(page);
        Native.FPDF_ClosePage(page);
        return at;
    }

    public void DeletePage(int pageIndex)
    {
        CheckPage(pageIndex);
        Native.FPDFPage_Delete(Doc, pageIndex);
    }

    /// <summary>Moves a page so that it ends up at <paramref name="toIndex"/>.</summary>
    public void MovePage(int fromIndex, int toIndex)
    {
        CheckPage(fromIndex);
        CheckPage(toIndex);
        if (fromIndex == toIndex) return;
        if (Native.FPDF_MovePages(Doc, &fromIndex, new CULong(1), toIndex) == 0)
            throw new PdfException("Could not move the page.");
    }

    /// <summary>Sets the page rotation to 0, 90, 180 or 270 degrees clockwise.</summary>
    public void SetPageRotation(int pageIndex, int degrees)
    {
        if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90.", nameof(degrees));
        using var page = OpenPage(pageIndex);
        Native.FPDFPage_SetRotation(page.Handle, ((degrees / 90) % 4 + 4) % 4);
    }

    public int GetPageRotation(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        return Native.FPDFPage_GetRotation(page.Handle) * 90;
    }

    /// <summary>Copies pages from another document into this one.</summary>
    /// <param name="pageRange">One-based range such as "1,3,5-7"; null copies every page.</param>
    /// <param name="index">Position to insert at; null appends at the end.</param>
    public void ImportPages(PdfDocument source, string? pageRange = null, int? index = null)
    {
        if (Native.FPDF_ImportPages(Doc, source.Doc, pageRange, index ?? PageCount) == 0)
            throw new PdfException("Could not import pages (check the page range).");
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Renders a page to BGRA pixels. A scale of 1 gives 72 DPI; 2 gives 144 DPI.</summary>
    public RenderedPage Render(int pageIndex, double scale = 1, bool showAnnotations = true)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        using var page = OpenPage(pageIndex);
        int width = Math.Max(1, (int)Math.Round(page.Width * scale));
        int height = Math.Max(1, (int)Math.Round(page.Height * scale));
        int stride = width * 4;
        var pixels = new byte[checked(stride * height)];
        fixed (byte* p = pixels)
        {
            var bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.FPDFBitmap_BGRA, p, stride);
            if (bitmap == IntPtr.Zero) throw new PdfException("Could not allocate the render bitmap.");
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                Native.FPDF_RenderPageBitmap(bitmap, page.Handle, 0, 0, width, height, 0, showAnnotations ? Native.FPDF_ANNOT : 0);
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        }
        return new RenderedPage(width, height, stride, pixels);
    }

    // ------------------------------------------------------------------ page plumbing

    private void CheckPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index {pageIndex} is outside 0..{PageCount - 1}.");
    }

    internal PageHandle OpenPage(int pageIndex)
    {
        CheckPage(pageIndex);
        var handle = Native.FPDF_LoadPage(Doc, pageIndex);
        if (handle == IntPtr.Zero) throw new PdfException($"Could not load page {pageIndex + 1}.");
        return new PageHandle(handle, this);
    }

    /// <summary>A loaded page plus the mapping between view space and the PDF's own user space.</summary>
    internal sealed class PageHandle : IDisposable
    {
        public IntPtr Handle { get; }
        public double Width { get; }
        public double Height { get; }
        public Affine ViewToUser { get; }
        public Affine UserToView { get; }
        /// <summary>Set when page objects changed and the content stream must be regenerated.</summary>
        public bool Dirty { get; set; }
        /// <summary>
        /// Set when an object that was already on the page changed or went away. PDFium then
        /// rewrites the page's original content, so other text needs protecting (see PreserveTextLayout).
        /// </summary>
        public bool ChangedExisting { get; set; }
        private readonly PdfDocument _owner;

        public PageHandle(IntPtr handle, PdfDocument owner)
        {
            Handle = handle;
            _owner = owner;
            Width = Native.FPDF_GetPageWidthF(handle);
            Height = Native.FPDF_GetPageHeightF(handle);

            // PDFium only maps integer device pixels, so probe at a large scale to get an exact
            // affine map. This accounts for page rotation and a crop box that is not at the origin.
            const double probe = 1000;
            int sx = (int)Math.Round(Width * probe), sy = (int)Math.Round(Height * probe);
            double x0, y0, x1, y1, x2, y2;
            Native.FPDF_DeviceToPage(handle, 0, 0, sx, sy, 0, 0, 0, &x0, &y0);
            Native.FPDF_DeviceToPage(handle, 0, 0, sx, sy, 0, sx, 0, &x1, &y1);
            Native.FPDF_DeviceToPage(handle, 0, 0, sx, sy, 0, 0, sy, &x2, &y2);
            ViewToUser = new Affine((x1 - x0) / Width, (y1 - y0) / Width, (x2 - x0) / Height, (y2 - y0) / Height, x0, y0);
            UserToView = ViewToUser.Invert();
        }

        public void Dispose()
        {
            if (Dirty)
            {
                if (ChangedExisting) _owner.PreserveTextLayout(this);
                Native.FPDFPage_GenerateContent(Handle);
            }
            Native.FPDF_ClosePage(Handle);
        }
    }
}
