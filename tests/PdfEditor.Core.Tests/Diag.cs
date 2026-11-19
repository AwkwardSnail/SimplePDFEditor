using PdfEditor.Core; using PdfEditor.Core.Interop; using Xunit; using Xunit.Abstractions;
public unsafe class Diag(ITestOutputHelper o) { [Fact] public void Run() {
  foreach (var name in new[] { "word-style.pdf", "two-columns.pdf" }) {
    using var doc = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    string before = doc.GetPageText(0);
    doc.AddPage(595, 842, 1);
    using (var page = doc.OpenPage(0)) using (var next = doc.OpenPage(1)) {
      var move = new List<IntPtr>();
      for (int i = 0; i < Native.FPDFPage_CountObjects(page.Handle); i++) { var ob = Native.FPDFPage_GetObject(page.Handle, i); float l, b, r, t; Native.FPDFPageObj_GetBounds(ob, &l, &b, &r, &t); if (t < 600) move.Add(ob); }
      foreach (var ob in move) { Native.FPDFPage_RemoveObject(page.Handle, ob); Native.FPDFPage_InsertObject(next.Handle, ob); }
      page.Dirty = page.ChangedExisting = true; next.Dirty = next.ChangedExisting = true;
      o.WriteLine($"{name}: moved {move.Count} objects");
    }
    using var re = PdfDocument.Open(doc.ToBytes());
    string a = re.GetPageText(0), b2 = re.GetPageText(1);
    o.WriteLine($"{name}: text chars before={before.Length}, page1={a.Length}, page2={b2.Length}; images p1={re.GetImages(0).Count} p2={re.GetImages(1).Count}; page 2 starts: {b2.Substring(0, Math.Min(60, b2.Length)).Replace("\r\n", " | ")}");
    var paras = re.GetParagraphs(1);
    o.WriteLine($"{name}: page 2 paragraphs={paras.Count} first align={paras.FirstOrDefault()?.Alignment} y={paras.FirstOrDefault()?.Bounds.Y:0}");
  }
}}
