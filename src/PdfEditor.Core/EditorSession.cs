namespace PdfEditor.Core;

/// <summary>
/// One open document plus what an editor needs around it: the file it came from, whether it
/// has unsaved changes, and undo/redo. Undo works by keeping a saved copy of the document
/// from before each edit, so every edit must go through <see cref="Apply"/>.
/// </summary>
public sealed class EditorSession : IDisposable
{
    private const int MaxUndoSteps = 30;
    private readonly LinkedList<byte[]> _undo = new();
    private readonly Stack<byte[]> _redo = new();

    public PdfDocument Document { get; private set; }
    public string? FilePath { get; private set; }
    /// <summary>A file name to offer when the document has no file yet (for example "Notes.pdf" for an imported "Notes.rtf").</summary>
    public string SuggestedFileName { get; private set; } = "Untitled.pdf";
    public bool IsModified { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after the document changes (edit, undo or redo) or is saved.</summary>
    public event EventHandler? Changed;

    private EditorSession(PdfDocument document, string? filePath)
    {
        Document = document;
        FilePath = filePath;
    }

    /// <summary>A new document with one blank A4 page.</summary>
    public static EditorSession New()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        return new EditorSession(doc, null);
    }

    public static EditorSession Open(string path, string? password = null) =>
        new(PdfDocument.Open(path, password), Path.GetFullPath(path));

    /// <summary>A new, unsaved PDF made from an RTF file.</summary>
    public static EditorSession FromRtf(string path, out RtfImportResult result, RtfImportOptions? options = null)
    {
        var doc = PdfDocument.FromRtf(File.ReadAllBytes(path), out result, options);
        return new EditorSession(doc, null) { SuggestedFileName = Path.ChangeExtension(Path.GetFileName(path), ".pdf"), IsModified = true };
    }

    /// <summary>Runs an edit as one undoable step. If the edit throws, the document is left as it was.</summary>
    public void Apply(Action<PdfDocument> edit)
    {
        byte[] before = Document.ToBytes();
        try { edit(Document); }
        catch
        {
            Replace(before);
            throw;
        }
        _undo.AddLast(before);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveFirst();
        _redo.Clear();
        IsModified = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Document.ToBytes());
        Replace(_undo.Last!.Value);
        _undo.RemoveLast();
        IsModified = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.AddLast(Document.ToBytes());
        Replace(_redo.Pop());
        IsModified = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves to <paramref name="path"/>, or to the current file when null.</summary>
    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("This document has no file yet; a path is needed.");
        Document.Save(path);
        FilePath = Path.GetFullPath(path);
        IsModified = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Replace(byte[] data)
    {
        var replacement = PdfDocument.Open(data);
        Document.Dispose();
        Document = replacement;
    }

    public void Dispose() => Document.Dispose();
}
