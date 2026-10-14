using CleanTag.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Windows.Foundation;

namespace CleanTag.Controls;

public sealed partial class StorageTreemap : UserControl
{
    private StorageAnalysisSnapshot? _snapshot;
    private int _root, _selected = -1, _hover = -1, _version;
    private Func<StorageNode, bool>? _filter;
    private TreemapLayout? _layout;
    private CancellationTokenSource? _layoutCts;
    private bool _active;
    private readonly Dictionary<(int Root, double Width, double Height), TreemapLayout> _cache = [];
    private readonly Queue<(int Root, double Width, double Height)> _cacheOrder = [];
    public event Action<int>? Selected;
    public event Action<int>? EnterFolder;
    public event Action<string>? Hovered;
    public event Action<TimeSpan, int>? LayoutReady;
    public StorageTreemap() => InitializeComponent();
    public void SetSnapshot(StorageAnalysisSnapshot snapshot, int root, Func<StorageNode, bool>? filter)
    { if (!ReferenceEquals(_snapshot, snapshot)) { _cache.Clear(); _cacheOrder.Clear(); } _layout = null; _snapshot = snapshot; _root = root; _filter = filter; _selected = _hover = -1; Surface.Invalidate(); Rebuild(); }
    public void SelectNode(int id) { _selected = id; Surface.Invalidate(); }
    private void Control_Loaded(object sender, RoutedEventArgs e) { _active = true; Rebuild(); }
    private void Control_Unloaded(object sender, RoutedEventArgs e) { _active = false; _version++; _layoutCts?.Cancel(); }
    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();
    private async void Rebuild()
    {
        if (!_active || _snapshot == null || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0) return;
        int version = ++_version; _layoutCts?.Cancel(); _layoutCts?.Dispose(); _layoutCts = new(); var token = _layoutCts.Token;
        var snapshot = _snapshot; int root = _root; double w = Surface.ActualWidth, h = Surface.ActualHeight; var filter = _filter;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var key = (root, w, h);
            var layout = filter == null && _cache.TryGetValue(key, out var cached) ? cached : await Task.Run(() => TreemapLayoutEngine.Build(snapshot, root, w, h, token, filter), token);
            if (version != _version || !_active) return;
            if (filter == null && !_cache.ContainsKey(key)) { while (_cache.Count >= 6) _cache.Remove(_cacheOrder.Dequeue()); _cache[key] = layout; _cacheOrder.Enqueue(key); }
            _layout = layout; EmptyLabel.Text = "Bu görünümde pozitif boyutlu dosya yok. Sıfır baytlı öğeler listede görünür.";
            EmptyLabel.Visibility = layout.Tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Surface.Invalidate(); LayoutReady?.Invoke(watch.Elapsed, layout.Tiles.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _version) { EmptyLabel.Text = "Harita oluşturulamadı: " + ex.Message; EmptyLabel.Visibility = Visibility.Visible; } }
    }
    public static Color CategoryColor(string category) => category switch
    {
        "Videolar" => Color.FromArgb(255, 40, 100, 174),
        "Görseller" => Color.FromArgb(255, 34, 126, 91),
        "Arşivler" => Color.FromArgb(255, 168, 97, 34),
        "Belgeler" => Color.FromArgb(255, 113, 70, 164),
        "Sesler" => Color.FromArgb(255, 160, 65, 121),
        "Tasarım / model" => Color.FromArgb(255, 24, 116, 136),
        "Klasörler" => Color.FromArgb(255, 78, 88, 105),
        _ => Color.FromArgb(255, 93, 105, 120)
    };
    private void Surface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_layout == null || _snapshot == null) return;
        using var format = new CanvasTextFormat { FontSize = 11, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };
        foreach (var tile in _layout.Tiles)
        {
            var r = tile.Bounds; var n = _snapshot.Nodes[tile.NodeId];
            var rect = new Rect(r.X, r.Y, Math.Max(0, r.Width - .7), Math.Max(0, r.Height - .7));
            args.DrawingSession.FillRectangle(rect, CategoryColor(n.Category));
            if (r.Width > 65 && r.Height > 25)
            { args.DrawingSession.DrawText(tile.Aggregate ? "Küçük öğeler / …" : n.Name, new Rect(r.X + 4, r.Y + 3, r.Width - 8, 17), Colors.White, format); }
            if (tile.NodeId == _selected || tile.NodeId == _hover)
            { args.DrawingSession.DrawRectangle(new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2)), Colors.White, tile.NodeId == _selected ? 3 : 1.5f); }
        }
    }
    private MapTile? At(Point p) => _layout?.HitTest(p.X, p.Y);
    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var tile = At(e.GetCurrentPoint(Surface).Position); int next = tile?.NodeId ?? -1;
        if (next == _hover) return; _hover = next; Surface.Invalidate();
        if (tile == null || _snapshot == null) { Hovered?.Invoke(""); return; }
        var n = _snapshot.Nodes[next]; double value = tile.Value.RepresentedBytes; double percent = _snapshot.Nodes[_root].Bytes > 0 ? value * 100d / _snapshot.Nodes[_root].Bytes : 0;
        Hovered?.Invoke($"{(tile.Value.Aggregate ? "Gruplanmış küçük öğeler" : n.Name)} · {FileEntry.FormatSize((long)value)} · %{percent:0.##} (bu klasörün tamamı) · {n.Category} · {n.Extension}{(tile.Value.Aggregate ? " · Liste ve filtrelerle ayrıntılandırın" : "")}\n{_snapshot.GetPath(next)}");
    }
    private void Surface_PointerExited(object sender, PointerRoutedEventArgs e) { _hover = -1; Surface.Invalidate(); Hovered?.Invoke(""); }
    private void Surface_Tapped(object sender, TappedRoutedEventArgs e) { var tile = At(e.GetPosition(Surface)); if (tile != null) { _selected = tile.Value.NodeId; Surface.Invalidate(); Selected?.Invoke(_selected); } }
    private void Surface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tile = At(e.GetPosition(Surface)); if (tile == null || _snapshot == null) return;
        int id = tile.Value.NodeId; EnterFolder?.Invoke(_snapshot.Nodes[id].IsDirectory ? id : _snapshot.Nodes[id].Parent);
    }
}
