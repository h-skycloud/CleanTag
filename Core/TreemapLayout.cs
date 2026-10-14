namespace CleanTag.Core;

public readonly record struct MapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}
public readonly record struct MapTile(int NodeId, MapRect Bounds, bool Aggregate, double RepresentedBytes = 0);
public sealed class TreemapLayout
{
    public IReadOnlyList<MapTile> Tiles { get; }
    private readonly Dictionary<(int, int), List<int>> _index = [];
    public TreemapLayout(List<MapTile> tiles)
    {
        Tiles = tiles.AsReadOnly();
        for (int i = 0; i < tiles.Count; i++)
        {
            var r = tiles[i].Bounds;
            for (int x = (int)(r.X / 64); x <= (int)((r.X + r.Width) / 64); x++)
                for (int y = (int)(r.Y / 64); y <= (int)((r.Y + r.Height) / 64); y++)
                { if (!_index.TryGetValue((x, y), out var bucket)) _index[(x, y)] = bucket = []; bucket.Add(i); }
        }
    }
    public MapTile? HitTest(double x, double y)
    { if (_index.TryGetValue(((int)(x / 64), (int)(y / 64)), out var bucket)) foreach (int i in bucket) if (Tiles[i].Bounds.Contains(x, y)) return Tiles[i]; return null; }
}
public static class TreemapLayoutEngine
{
    public static TreemapLayout Build(StorageAnalysisSnapshot snapshot, int root, double width, double height, CancellationToken token = default, Func<StorageNode, bool>? filter = null, int budget = 12000)
    {
        var tiles = new List<MapTile>(); if (width <= 0 || height <= 0 || snapshot.Nodes[root].Bytes <= 0) return new(tiles);
        // Filtered weights remain separate from the immutable snapshot.
        var weights = new double[snapshot.Nodes.Count];
        foreach (int id in snapshot.Descendants(root)) { token.ThrowIfCancellationRequested(); var n = snapshot.Nodes[id]; if (!n.IsDirectory && (filter == null || filter(n))) weights[id] = n.Length; }
        for (int i = weights.Length - 1; i > 0; i--) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); weights[snapshot.Nodes[i].Parent] += weights[i]; }
        var work = new Stack<(int Id, MapRect Bounds)>(); work.Push((root, new(0, 0, width, height)));
        while (work.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var current = work.Pop();
            var children = snapshot.Children(current.Id).Where(i => weights[i] > 0).OrderByDescending(i => weights[i]).ThenBy(i => snapshot.Nodes[i].Name, StringComparer.Ordinal).ThenBy(i => i).ToArray();
            if (children.Length == 0) continue;
            if (current.Bounds.Area < 9 || tiles.Count + work.Count >= budget - 1)
            { tiles.Add(new(current.Id, current.Bounds, true, weights[current.Id])); continue; }
            int capacity = Math.Max(2, Math.Min(4000, budget - tiles.Count - work.Count));
            int count = Math.Min(children.Length, capacity);
            bool grouped = children.Length > count;
            var areas = new double[count];
            for (int i = 0; i < count; i++) areas[i] = weights[children[i]];
            if (grouped) { areas[count - 1] = 0; for (int i = count - 1; i < children.Length; i++) areas[count - 1] += weights[children[i]]; }
            // The aggregate may be larger than every retained child. Squarify still receives descending weights.
            var slots = Enumerable.Range(0,count).OrderByDescending(i => areas[i]).ThenBy(i => i).ToArray();
            var orderedRects = Squarify(slots.Select(i => areas[i]).ToArray(),current.Bounds);
            var rects = new MapRect[count];
            for (int i = 0; i < count; i++) rects[slots[i]] = orderedRects[i];
            if (grouped) tiles.Add(new(current.Id, rects[count - 1], true, areas[count - 1]));
            for (int i = count - (grouped ? 2 : 1); i >= 0; i--)
            {
                int id = children[i]; var bounds = rects[i];
                if (snapshot.Nodes[id].IsDirectory) work.Push((id, bounds));
                else if (bounds.Area > 0) tiles.Add(new(id, bounds, false, weights[id]));
            }
        }
        return new(tiles);
    }
    public static MapRect[] Squarify(IReadOnlyList<double> weights, MapRect bounds)
    {
        var result = new MapRect[weights.Count]; double total = weights.Sum();
        if (total <= 0 || bounds.Area <= 0) return result;
        var area = weights.Select(w => Math.Max(0, w) / total * bounds.Area).ToArray(); int start = 0;
        double Worst(int first, int end, double side)
        { double sum = 0, min = double.MaxValue, max = 0; for (int j = first; j < end; j++) { sum += area[j]; min = Math.Min(min, area[j]); max = Math.Max(max, area[j]); } return min <= 0 || side <= 0 ? double.PositiveInfinity : Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min)); }
        while (start < area.Length)
        {
            if (area[start] == 0) { start++; continue; }
            int end = start + 1; double side = Math.Min(bounds.Width, bounds.Height);
            while (end < area.Length && area[end] > 0 && Worst(start, end + 1, side) <= Worst(start, end, side)) end++;
            double sum = 0; for (int i = start; i < end; i++) sum += area[i];
            bool vertical = bounds.Width >= bounds.Height; double strip = sum / side, offset = 0;
            for (int i = start; i < end; i++)
            {
                double length = area[i] / strip;
                result[i] = vertical ? new(bounds.X, bounds.Y + offset, strip, length) : new(bounds.X + offset, bounds.Y, length, strip); offset += length;
            }
            bounds = vertical ? new(bounds.X + strip, bounds.Y, Math.Max(0, bounds.Width - strip), bounds.Height) : new(bounds.X, bounds.Y + strip, bounds.Width, Math.Max(0, bounds.Height - strip));
            start = end;
        }
        return result;
    }
}
