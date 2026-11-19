namespace PdfEditor.Core.Interop;

/// <summary>PDF-style affine matrix: (x', y') = (a·x + c·y + e, b·x + d·y + f).</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Transform applying <paramref name="first"/> and then <paramref name="second"/>.</summary>
    public static Affine Concat(Affine first, Affine second) => new(
        first.A * second.A + first.B * second.C,
        first.A * second.B + first.B * second.D,
        first.C * second.A + first.D * second.C,
        first.C * second.B + first.D * second.D,
        first.E * second.A + first.F * second.C + second.E,
        first.E * second.B + first.F * second.D + second.F);

    public Affine Invert()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) throw new PdfException("Page has a degenerate coordinate system.");
        double a = D / det, b = -B / det, c = -C / det, d = A / det;
        return new Affine(a, b, c, d, -(E * a + F * c), -(E * b + F * d));
    }

    /// <summary>Bounding box of a rectangle (given by two opposite corners) after transformation.</summary>
    public PdfRect MapBounds(double x1, double y1, double x2, double y2)
    {
        var p = new[] { Apply(x1, y1), Apply(x2, y1), Apply(x1, y2), Apply(x2, y2) };
        double minX = p.Min(q => q.X), maxX = p.Max(q => q.X), minY = p.Min(q => q.Y), maxY = p.Max(q => q.Y);
        return new PdfRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>The same transform without its translation.</summary>
    public Affine WithoutTranslation() => this with { E = 0, F = 0 };

    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public Native.FS_MATRIX ToNative() => new() { a = (float)A, b = (float)B, c = (float)C, d = (float)D, e = (float)E, f = (float)F };
}
