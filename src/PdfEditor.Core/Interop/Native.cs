using System.Runtime.InteropServices;

namespace PdfEditor.Core.Interop;

/// <summary>
/// Minimal P/Invoke surface over the PDFium C API (only what the editor uses).
/// "unsigned long" is 32-bit on Windows and 64-bit elsewhere, hence <see cref="CULong"/>.
/// </summary>
internal static unsafe class Native
{
    private const string Lib = "pdfium";

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_MATRIX { public float a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF { public float left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF { public float x1, y1, x2, y2, x3, y3, x4, y4; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int version;
        public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, void*, CULong, int> WriteBlock;
        public IntPtr context; // our own trailing field: GCHandle of the target stream
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public CULong m_FileLen;
        public delegate* unmanaged[Cdecl]<void*, CULong, byte*, CULong, int> m_GetBlock;
        public void* m_Param;
    }

    public const int PAGEOBJ_TEXT = 1;
    public const int PAGEOBJ_IMAGE = 3;
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDFBitmap_BGRA = 4;
    public const uint FPDF_NO_INCREMENTAL = 2;
    public const int FPDF_FONT_TRUETYPE = 2;
    public const int FPDF_ANNOT_FLAG_PRINT = 4;
    public const uint FPDF_MATCHCASE = 1;

    // ---- fpdfview.h ----
    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument(void* data, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] public static extern CULong FPDF_GetLastError();
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] public static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, double* pageX, double* pageY);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [DllImport(Lib)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // ---- fpdf_edit.h ----
    [DllImport(Lib)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Lib)] public static extern IntPtr FPDFPage_New(IntPtr doc, int index, double width, double height);
    [DllImport(Lib)] public static extern void FPDFPage_Delete(IntPtr doc, int index);
    [DllImport(Lib)] public static extern int FPDF_MovePages(IntPtr doc, int* indices, CULong count, int destIndex);
    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Lib)] public static extern void FPDFPage_InsertObject(IntPtr page, IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetType(IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetBounds(IntPtr obj, float* left, float* bottom, float* right, float* top);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetMatrix(IntPtr obj, FS_MATRIX* matrix);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetMatrix(IntPtr obj, FS_MATRIX* matrix);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetFillColor(IntPtr obj, uint* r, uint* g, uint* b, uint* a);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewTextObj(IntPtr doc, [MarshalAs(UnmanagedType.LPStr)] string font, float fontSize);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr doc, IntPtr font, float fontSize);
    [DllImport(Lib)] public static extern int FPDFText_SetText(IntPtr textObj, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadFont(IntPtr doc, byte* data, uint size, int fontType, int cid);
    [DllImport(Lib)] public static extern void FPDFFont_Close(IntPtr font);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetFontSize(IntPtr textObj, float* size);
    [DllImport(Lib)] public static extern CULong FPDFTextObj_GetText(IntPtr textObj, IntPtr textPage, char* buffer, CULong lengthBytes);
    [DllImport(Lib)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr textObj);
    [DllImport(Lib)] public static extern nuint FPDFFont_GetBaseFontName(IntPtr font, byte* buffer, nuint length);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr doc);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetBitmap(IntPtr* pages, int count, IntPtr imageObj, IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFImageObj_LoadJpegFileInline(IntPtr* pages, int count, IntPtr imageObj, FPDF_FILEACCESS* file);
    [DllImport(Lib)] public static extern int FPDFImageObj_GetImagePixelSize(IntPtr imageObj, uint* width, uint* height);

    // ---- fpdf_ppo.h / fpdf_save.h ----
    [DllImport(Lib)] public static extern int FPDF_ImportPages(IntPtr dest, IntPtr src, [MarshalAs(UnmanagedType.LPStr)] string? range, int index);
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr doc, FPDF_FILEWRITE* writer, uint flags);

    // ---- fpdf_text.h ----
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] public static extern int FPDFText_GetText(IntPtr textPage, int start, int count, char* result);
    [DllImport(Lib)] public static extern int FPDFText_CountRects(IntPtr textPage, int start, int count);
    [DllImport(Lib)] public static extern int FPDFText_GetRect(IntPtr textPage, int index, double* left, double* top, double* right, double* bottom);
    [DllImport(Lib)] public static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);
    [DllImport(Lib)] public static extern IntPtr FPDFText_FindStart(IntPtr textPage, [MarshalAs(UnmanagedType.LPWStr)] string what, CULong flags, int startIndex);
    [DllImport(Lib)] public static extern int FPDFText_FindNext(IntPtr search);
    [DllImport(Lib)] public static extern int FPDFText_GetSchResultIndex(IntPtr search);
    [DllImport(Lib)] public static extern int FPDFText_GetSchCount(IntPtr search);
    [DllImport(Lib)] public static extern void FPDFText_FindClose(IntPtr search);

    // ---- fpdf_annot.h ----
    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, FS_RECTF* rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetRect(IntPtr annot, FS_RECTF* rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, FS_QUADPOINTSF* quad);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, uint* r, uint* g, uint* b, uint* a);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib)] public static extern CULong FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, char* buffer, CULong lengthBytes);
}
