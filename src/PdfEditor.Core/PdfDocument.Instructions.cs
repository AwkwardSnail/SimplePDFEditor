using System.IO.Compression;
using System.Text;
using PdfEditor.Core.Content;
using PdfEditor.Core.Interop;
using PdfEditor.Core.Preflight;

namespace PdfEditor.Core;

// Edits made in a page's own instructions (see InstructionEdits), and the reload that follows.
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Whether moving and removing items is done by rewriting only their own instructions, where
    /// that is possible. Off, every such change has PDFium write the whole page again, which is
    /// what the editor did before and still does whenever the direct way cannot be used.
    /// </summary>
    internal static bool EditInstructionsDirectly { get; set; } = true;

    /// <summary>How many edits have gone each way since the program started (for tests and tuning).</summary>
    internal static int DirectEdits, RewrittenEdits;

    /// <summary>
    /// Tries to make a change to items of a page in the page's instructions. Returns false,
    /// leaving the document as it was, when it cannot be done that way.
    /// </summary>
    /// <param name="changeFor">For each item: a transform in the page's own coordinates, or null to remove it.</param>
    private bool TryEditInstructions(int pageIndex, IReadOnlyList<int> objectIndices, Func<int, Affine?> changeFor)
    {
        if (!EditInstructionsDirectly || objectIndices.Count == 0) { RewrittenEdits++; return false; }
        try
        {
            CheckPage(pageIndex);
            // What PDFium holds now, written out: the instructions are read from that.
            byte[] plain;
            using (var ms = new MemoryStream())
            {
                SaveAsPdfiumWritesIt(ms);
                plain = ms.ToArray();
            }
            var file = PdfFile.Open(plain);
            if (file.Trailer?["Encrypt"] is not null) { RewrittenEdits++; return false; }
            var read = PageInstructions.Read(file, pageIndex, out _);
            if (read is null || read.ContentObjects.Count == 0 || read.PageObject < 0) { RewrittenEdits++; return false; }

            // The items must be PDFium's objects, one for one; and an item that is clipped must stay inside its clip.
            var changes = new List<InstructionEdits.Change>();
            using (var page = OpenPage(pageIndex))
            {
                int count = Native.FPDFPage_CountObjects(page.Handle);
                if (count != read.Items.Count) { RewrittenEdits++; return false; }
                for (int i = 0; i < count; i++)
                    if (Native.FPDFPageObj_GetType(Native.FPDFPage_GetObject(page.Handle, i)) != (int)read.Items[i].Kind) { RewrittenEdits++; return false; }
                foreach (int index in objectIndices.Distinct())
                {
                    if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(objectIndices));
                    var transform = changeFor(index);
                    if (transform is { } t)
                    {
                        var obj = Native.FPDFPage_GetObject(page.Handle, index);
                        float l = 0, b = 0, r = 0, top = 0;
                        if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &top) == 0) { RewrittenEdits++; return false; }
                        // Boxed in where it is drawn, the item moves but its clip does not: it must still fit inside.
                        var moved = t.MapBounds(l, b, r, top);
                        if (!read.Items[index].Clip.LeavesWhole(moved.X, moved.Y, moved.Right, moved.Bottom)) { RewrittenEdits++; return false; }
                    }
                    changes.Add(new InstructionEdits.Change(index, transform));
                }
            }
            byte[]? data = InstructionEdits.Apply(read, changes);
            if (data is null) { RewrittenEdits++; return false; }

            // The page's content becomes one stream, in place of its first; the page points at that alone.
            byte[] packedData;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
                packedData = ms.ToArray();
            }
            int contentObject = read.ContentObjects[0];
            var stream = new List<byte>(packedData.Length + 64);
            stream.AddRange(Encoding.ASCII.GetBytes($"<</Filter/FlateDecode/Length {packedData.Length}>>stream\n"));
            stream.AddRange(packedData);
            stream.AddRange("\nendstream"u8.ToArray());
            var pageDict = new StringBuilder("<<");
            foreach (var (key, value) in read.PageNode.Entries)
                pageDict.Append('/').Append(key).Append(' ').Append(key == "Contents" ? $"{contentObject} 0 R" : PdfFile.Raw(value, read.PageSource)).Append(' ');
            pageDict.Append(">>");
            var replace = new Dictionary<int, byte[]> { [contentObject] = stream.ToArray(), [read.PageObject] = Encoding.Latin1.GetBytes(pageDict.ToString()) };
            var drop = read.ContentObjects.Skip(1).ToHashSet();

            byte[]? packed = PackObjects.Apply(plain, replace, drop);
            if (packed is null) { RewrittenEdits++; return false; }
            int pages = PageCount;
            Reload(packed);
            if (PageCount != pages)
            {
                // Should never happen; go back to what PDFium wrote and let the older route do the edit.
                Reload(plain);
                RewrittenEdits++;
                return false;
            }
            DirectEdits++;
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            RewrittenEdits++;
            return false;
        }
    }

    /// <summary>Replaces what PDFium holds with a document read from the given bytes: this object stays the same document to its users.</summary>
    private void Reload(byte[] data)
    {
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        var doc = Native.FPDF_LoadMemDocument((void*)pin.AddrOfPinnedObject(), data.Length, null);
        if (doc == IntPtr.Zero)
        {
            pin.Free();
            throw new InvalidDataException("The changed document could not be read back.");
        }
        // Font handles and what was learned about fonts belong to the document being let go.
        foreach (var font in _fonts.Values) Native.FPDFFont_Close(font);
        foreach (var font in _standardFonts.Values) Native.FPDFFont_Close(font);
        _fonts.Clear();
        _standardFonts.Clear();
        _glyphCodes.Clear();
        _charsByFont = null;
        Native.FPDF_CloseDocument(_doc);
        if (_sourcePin.IsAllocated) _sourcePin.Free();
        _doc = doc;
        _sourcePin = pin;
    }
}
