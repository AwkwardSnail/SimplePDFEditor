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
    /// Tries to move, turn or remove items of a page in the page's instructions. Returns false,
    /// leaving the document as it was, when it cannot be done that way.
    /// </summary>
    /// <param name="changeFor">For each item: a transform in the page's own coordinates, or null to remove it.</param>
    private bool TryEditInstructions(int pageIndex, IReadOnlyList<int> objectIndices, Func<int, Affine?> changeFor)
    {
        if (objectIndices.Count == 0) { RewrittenEdits++; return false; }
        return TryRewriteInstructions(pageIndex, (read, facts) =>
        {
            var changes = new List<InstructionEdits.Change>();
            foreach (int index in objectIndices.Distinct())
            {
                if (index < 0 || index >= read.Items.Count) throw new ArgumentOutOfRangeException(nameof(objectIndices));
                changes.Add(new InstructionEdits.Change(index, changeFor(index)));
            }
            return InstructionEdits.Apply(read, changes, facts);
        });
    }

    /// <summary>
    /// Removes some existing objects of a page and moves others, after a page has been closed
    /// (see <see cref="PageHandle.RemoveLater"/>): in the page's instructions if possible,
    /// otherwise through PDFium, which then writes the page again.
    /// </summary>
    /// <param name="added">New objects (at the end of the page) to put where the first of the removed ones stood.</param>
    /// <param name="spaced">New text to be given a letter spacing, or failing that to be squeezed to the same width.</param>
    private void RemoveAndMove(int pageIndex, List<int> remove, List<(int Index, Affine By)> move, List<int> added, List<(int Index, double Spacing, double Squeeze)> spaced)
    {
        var gone = remove.ToHashSet();
        var moves = move.Where(m => !gone.Contains(m.Index)).GroupBy(m => m.Index).ToDictionary(g => g.Key, g => g.Last().By);
        bool direct = TryRewriteInstructions(pageIndex, (read, facts) =>
        {
            var changes = gone.Select(i => new InstructionEdits.Change(i, null)).Concat(moves.Select(m => new InstructionEdits.Change(m.Key, m.Value))).ToList();
            bool place = added.Count > 0 && gone.Count > 0;
            return place || spaced.Count > 0
                ? InstructionEdits.ApplyAndPlace(read, changes, place ? added : [], place ? gone.Min() : -1, facts, spaced.ToDictionary(s => s.Index, s => s.Spacing))
                : InstructionEdits.Apply(read, changes, facts);
        });
        if (direct) return;

        using var page = OpenPage(pageIndex);
        // Look every object up before removing any: removing one shifts the indices after it.
        var toRemove = gone.Select(i => GetObject(page, i)).ToList();
        var toMove = moves.Select(m => (Obj: GetObject(page, m.Key), m.Value)).ToList();
        // PDFium writes no letter spacing: the new text is made as wide as it was meant to be instead.
        foreach (var (index, _, squeeze) in spaced)
        {
            var obj = GetObject(page, index);
            Native.FS_MATRIX m;
            if (Native.FPDFPageObj_GetMatrix(obj, &m) == 0) continue;
            m.a = (float)(m.a * squeeze);
            m.b = (float)(m.b * squeeze);
            Native.FPDFPageObj_SetMatrix(obj, &m);
        }
        foreach (var (obj, by) in toMove) TransformObject(obj, by);
        foreach (var obj in toRemove)
        {
            Native.FPDFPage_RemoveObject(page.Handle, obj);
            Native.FPDFPageObj_Destroy(obj);
        }
        page.Dirty = page.ChangedExisting = true;
    }

    /// <summary>
    /// Reads the page's instructions, checks them against PDFium's objects, has
    /// <paramref name="edit"/> produce new instructions, and puts those in the page's place.
    /// Returns false, with the document as it was, when any step cannot be done.
    /// </summary>
    private bool TryRewriteInstructions(int pageIndex, Func<PageInstructions, InstructionEdits.Facts, byte[]?> edit)
    {
        // With a page held open the document cannot be read afresh: its handles would be left dangling.
        if (!EditInstructionsDirectly || _openPages > 0) { RewrittenEdits++; return false; }
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

            // The items must be PDFium's objects, one for one. What PDFium knows of each is noted:
            // where it has every piece of text, and the room every item takes.
            var placed = new Affine?[read.Items.Count];
            var bounds = new (double, double, double, double)?[read.Items.Count];
            using (var page = OpenPage(pageIndex))
            {
                int count = Native.FPDFPage_CountObjects(page.Handle);
                if (count != read.Items.Count) { RewrittenEdits++; return false; }
                for (int i = 0; i < count; i++)
                {
                    var obj = Native.FPDFPage_GetObject(page.Handle, i);
                    if (Native.FPDFPageObj_GetType(obj) != (int)read.Items[i].Kind) { RewrittenEdits++; return false; }
                    float l = 0, b = 0, r = 0, top = 0;
                    if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &top) != 0) bounds[i] = (l, b, r, top);
                    Native.FS_MATRIX m;
                    if (read.Items[i].Kind == Content.ItemKind.Text && Native.FPDFPageObj_GetMatrix(obj, &m) != 0) placed[i] = new Affine(m.a, m.b, m.c, m.d, m.e, m.f);
                }
            }
            var facts = new InstructionEdits.Facts(i => i >= 0 && i < placed.Length ? placed[i] : null, i => i >= 0 && i < bounds.Length ? bounds[i] : null);
            byte[]? data = edit(read, facts);
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
            int pages = PageCount, items = read.Items.Count;
            Reload(packed);
            // The changed page must read back as the same number of items or fewer, never garbled into more.
            bool sound = PageCount == pages;
            if (sound)
            {
                using var check = OpenPage(pageIndex);
                sound = Native.FPDFPage_CountObjects(check.Handle) <= items;
            }
            if (!sound)
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
    /// <summary>
    /// Moves a comment, highlight or other annotation one step up or down among the page's
    /// annotations (the later one lies over the earlier). Annotations always lie over what is
    /// printed on the page itself; this only orders them among themselves. Returns false when it
    /// is already the topmost or the lowest.
    /// </summary>
    public bool MoveAnnotationLayer(int pageIndex, int annotationIndex, bool up)
    {
        CheckPage(pageIndex);
        int count;
        using (var page = OpenPage(pageIndex)) count = Native.FPDFPage_GetAnnotCount(page.Handle);
        if (annotationIndex < 0 || annotationIndex >= count) throw new ArgumentOutOfRangeException(nameof(annotationIndex));
        int other = up ? annotationIndex + 1 : annotationIndex - 1;
        if (other < 0 || other >= count) return false;
        if (_openPages > 0) throw new PdfException("The order cannot be changed while the page is being edited.");
        try
        {
            // PDFium has no way to reorder annotations: the page's list of them is rewritten in the file itself.
            byte[] plain;
            using (var ms = new MemoryStream())
            {
                SaveAsPdfiumWritesIt(ms);
                plain = ms.ToArray();
            }
            var file = PdfFile.Open(plain);
            if (file.Trailer?["Encrypt"] is not null) throw new PdfException("The order of comments and highlights cannot be changed in a password-protected file.");
            var read = PageInstructions.Read(file, pageIndex, out _) ?? throw new PdfException("The page could not be read to change the order.");
            var listed = read.PageNode["Annots"];
            var (list, source) = file.Resolve(listed, read.PageSource);
            if (list is not { Kind: PdfFile.Kind.Array } || source is null || list.Items.Count != count) throw new PdfException("The page's comments and highlights could not be read to change the order.");
            var items = list.Items.Select(n => PdfFile.Raw(n, source)).ToList();
            (items[annotationIndex], items[other]) = (items[other], items[annotationIndex]);
            string reordered = "[" + string.Join(" ", items) + "]";

            var replace = new Dictionary<int, byte[]>();
            if (listed is { Kind: PdfFile.Kind.Ref }) replace[listed.RefNumber] = Encoding.Latin1.GetBytes(reordered);
            else
            {
                var pageDict = new StringBuilder("<<");
                foreach (var (key, value) in read.PageNode.Entries)
                    pageDict.Append('/').Append(key).Append(' ').Append(key == "Annots" ? reordered : PdfFile.Raw(value, read.PageSource)).Append(' ');
                pageDict.Append(">>");
                replace[read.PageObject] = Encoding.Latin1.GetBytes(pageDict.ToString());
            }
            byte[] packed = PackObjects.Apply(plain, replace, new HashSet<int>()) ?? throw new PdfException("The order could not be changed in this file.");
            Reload(packed);
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new PdfException("The order could not be changed in this file.");
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
