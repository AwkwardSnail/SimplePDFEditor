using PdfEditor.Core;

namespace PdfEditor.App;

/// <summary>
/// Remembers, while a document is open, which shapes the user has taken out of their figure
/// (ungrouped) and which they have joined into one. A PDF has no groups of its own: shapes that
/// touch are taken as one figure, and this is what the user has said differently.
///
/// A shape has no lasting name in the file (its place in the page's order changes as things are
/// added and removed), so shapes are known here by the room they take on the page. Each time a
/// page is shown again, what is there is compared with what was there before: a shape that has
/// gone and one that has come in its place (a shape that was moved) keeps what was said about it;
/// a single new shape (one just drawn) starts on its own.
/// </summary>
internal sealed class ShapeGroups
{
    private readonly record struct Place(long X, long Y, long Width, long Height);

    private sealed class PageState
    {
        public List<(int Index, Place Place)> Seen = [];
        public readonly HashSet<Place> Apart = [];
        public readonly List<HashSet<Place>> Joined = [];
    }

    private readonly Dictionary<int, PageState> _pages = [];

    private static Place Of(PdfRect r) => new((long)Math.Round(r.X * 20), (long)Math.Round(r.Y * 20), (long)Math.Round(r.Width * 20), (long)Math.Round(r.Height * 20));

    /// <summary>Forgets everything: for a new document, or when pages are added, removed or moved.</summary>
    public void Clear() => _pages.Clear();

    /// <summary>
    /// Takes note of the shapes now on a page, and says which of them are to be kept apart and
    /// which sets are to be taken as one (by their place in the page's order).
    /// </summary>
    public (IReadOnlyCollection<int> Apart, IReadOnlyList<IReadOnlyCollection<int>> Together) Update(int page, IReadOnlyList<ShapeInfo> shapes)
    {
        var now = shapes.Select(s => (Index: s.ObjectIndex, Place: Of(s.Bounds))).ToList();
        if (!_pages.TryGetValue(page, out var state)) _pages[page] = state = new PageState();
        else
        {
            var gone = Unmatched(state.Seen, now);
            var came = Unmatched(now, state.Seen);
            if (gone.Count > 0 && gone.Count == came.Count)
            {
                // Moved (or changed in size): each keeps what was said about it. Both lists are in the page's order.
                var movedTo = new Dictionary<Place, Place>();
                for (int i = 0; i < gone.Count; i++) movedTo[gone[i]] = came[i];
                Remap(state.Apart, movedTo);
                foreach (var set in state.Joined) Remap(set, movedTo);
            }
            else
            {
                state.Apart.ExceptWith(gone);
                foreach (var set in state.Joined) set.ExceptWith(gone);
                if (gone.Count == 0 && came.Count == 1) state.Apart.Add(came[0]); // a shape just drawn
            }
            state.Joined.RemoveAll(set => set.Count < 2);
        }
        state.Seen = now;

        var apart = now.Where(n => state.Apart.Contains(n.Place)).Select(n => n.Index).ToList();
        var together = state.Joined.Select(set => (IReadOnlyCollection<int>)now.Where(n => set.Contains(n.Place)).Select(n => n.Index).ToList()).Where(set => set.Count > 1).ToList();
        return (apart, together);
    }

    /// <summary>The given shapes are each on their own from now on.</summary>
    public void Ungroup(int page, IEnumerable<PdfRect> shapes)
    {
        if (!_pages.TryGetValue(page, out var state)) return;
        var places = shapes.Select(Of).ToHashSet();
        state.Apart.UnionWith(places);
        foreach (var set in state.Joined) set.ExceptWith(places);
        state.Joined.RemoveAll(set => set.Count < 2);
    }

    /// <summary>The given shapes are one figure from now on.</summary>
    public void Group(int page, IEnumerable<PdfRect> shapes)
    {
        if (!_pages.TryGetValue(page, out var state)) return;
        var places = shapes.Select(Of).ToHashSet();
        if (places.Count < 2) return;
        state.Apart.ExceptWith(places);
        foreach (var set in state.Joined.Where(set => set.Overlaps(places)).ToList())
        {
            places.UnionWith(set);
            state.Joined.Remove(set);
        }
        state.Joined.Add(places);
    }

    /// <summary>Whether a shape has been taken out of its figure.</summary>
    public bool IsApart(int page, PdfRect shape) => _pages.TryGetValue(page, out var state) && state.Apart.Contains(Of(shape));

    /// <summary>The places in <paramref name="a"/> with no partner in <paramref name="b"/>, counting repeats, in order.</summary>
    private static List<Place> Unmatched(List<(int Index, Place Place)> a, List<(int Index, Place Place)> b)
    {
        var left = new Dictionary<Place, int>();
        foreach (var (_, place) in b) left[place] = left.GetValueOrDefault(place) + 1;
        var result = new List<Place>();
        foreach (var (_, place) in a)
        {
            if (left.GetValueOrDefault(place) > 0) left[place]--;
            else result.Add(place);
        }
        return result;
    }

    private static void Remap(HashSet<Place> set, Dictionary<Place, Place> movedTo)
    {
        var moved = set.Where(movedTo.ContainsKey).ToList();
        foreach (var place in moved) set.Remove(place);
        foreach (var place in moved) set.Add(movedTo[place]);
    }
}
