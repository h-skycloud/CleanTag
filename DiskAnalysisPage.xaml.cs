using System.Diagnostics;
using CleanTag.Controls;
using CleanTag.Core;
using CleanTag.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

namespace CleanTag;

public sealed partial class DiskAnalysisPage : Page, IWorkspacePage
{
    private StorageAnalysisSnapshot? _snapshot;
    private readonly IStorageScanner _scanner = new StandardStorageScanner();
    private CancellationTokenSource? _scanCts, _queryCts;
    private Task<StorageAnalysisSnapshot>? _scanTask;
    private bool _ready, _active, _scanning, _operation, _updating, _expanded;
    private int _folder, _selected = -1, _offset, _queryVersion, _selectionVersion;
    private int[] _visibleIds = [];
    private FileEntry? _selectedEntry;
    private string? _catalogId;
    private string? _pendingRoot;
    private readonly Dictionary<TreeViewNode, int> _treeIds = [];
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FilePreview _preview = new() { Width = 540, Height = 420 };
    public bool CanLeave => !_operation;
    public bool CanClose => !_operation;
    private sealed record Row(int Id, string Name, string Size, string Percent, string Detail);
    private sealed record Crumb(int Id, string Name) { public override string ToString() => Name; }
    private sealed record ExtensionRow(string Extension, string Label) { public override string ToString() => Label; }
    public DiskAnalysisPage()
    {
        InitializeComponent(); _ready = true;
        DriveSelector.ItemsSource = DriveInfo.GetDrives().Select(d => d.Name).ToArray();
        ExtensionSelector.ItemsSource = new[] { "Tüm uzantılar" }; ExtensionSelector.SelectedIndex = 0;
        _filterTimer.Tick += async (_, _) => { _filterTimer.Stop(); _offset = 0; await RefreshViewAsync(); };
        Map.Selected += async id => await SelectMapNodeAsync(id);
        Map.EnterFolder += async id => await NavigateAsync(id);
        Map.Hovered += text => HoverLabel.Text = text;
        Map.LayoutReady += (time, count) => LayoutLabel.Text = $"{count:N0} görünür alan · Yerleşim {time.TotalMilliseconds:0} ms · Video mavi / görsel yeşil / arşiv turuncu / belge mor / ses pembe";
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _active = true;
        if (_pendingRoot != null) { string root = _pendingRoot; _pendingRoot = null; await AnalyzeAsync(root); return; }
        var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--analyze");
        if (_snapshot == null && PathBox.Text.Length == 0 && index >= 0 && index + 1 < args.Length) await AnalyzeAsync(args[index + 1]);
        else if (_snapshot != null) await RefreshViewAsync();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e) { _active = false; _filterTimer.Stop(); _queryCts?.Cancel(); }
    public async Task PrepareToLeaveAsync()
    { _active = false; _filterTimer.Stop(); _queryVersion++; _selectionVersion++; _scanCts?.Cancel(); _queryCts?.Cancel(); if (_scanTask != null) { try { await _scanTask; } catch { } } await _preview.ReleaseAsync(); }
    public async Task AnalyzeAsync(string root)
    {
        if (!_active) { _pendingRoot = root; return; }
        if (_scanning || _operation) return;
        PathBox.Text = root; _scanning = true; _scanCts?.Dispose(); _scanCts = new();
        _queryVersion++; _selectionVersion++; _queryCts?.Cancel();
        Map.IsHitTestVisible = EntriesList.IsEnabled = FoldersTree.IsEnabled = false;
        SelectedLabel.Text = "Haritadan veya listeden bir öğe seçin."; TagsLabel.Text = HoverLabel.Text = "";
        ScanButton.IsEnabled = PathBox.IsEnabled = DriveSelector.IsEnabled = Commands.IsEnabled = false;
        SelectionCommands.IsEnabled = false; CancelButton.Visibility = ScanProgress.Visibility = Visibility.Visible; MessageBar.IsOpen = false;
        _selectedEntry = null; _selected = -1; _catalogId = null;
        bool reporting = true;
        var progress = new Progress<StorageScanProgress>(p => { if (_active && reporting) SummaryLabel.Text = $"{p.Files:N0} dosya · {p.Folders:N0} klasör · {FileEntry.FormatSize(p.Bytes)} · {p.Elapsed.TotalSeconds:0.0} sn · {p.CurrentPath}"; });
        try
        {
            _scanTask = _scanner.ScanAsync(root, new(), progress, _scanCts.Token); var result = await _scanTask;
            reporting = false;
            if (!_active) return;
            _snapshot = result; _folder = 0; _offset = 0; _updating = true;
            try
            {
                ExtensionSelector.ItemsSource = new[] { "Tüm uzantılar" }.Concat(result.Extensions.Select(e => e.Extension.Length == 0 ? "(uzantısız)" : e.Extension)).ToArray();
                ExtensionSelector.SelectedIndex = 0; SearchBox.Text = ""; CategorySelector.SelectedIndex = 0; MinimumSize.Value = 0;
                ExtensionsList.ItemsSource = result.Extensions.Select(e => new ExtensionRow(e.Extension, $"{(e.Extension.Length == 0 ? "(uzantısız)" : e.Extension)} · {e.Category}\n{FileEntry.FormatSize(e.Bytes)} · %{(result.Nodes[0].Bytes == 0 ? 0 : e.Bytes * 100d / result.Nodes[0].Bytes):0.##} · {e.Files:N0} dosya")).ToArray();
                BuildTree();
            }
            finally { _updating = false; }
            string state = result.Status switch { StorageScanStatus.Cancelled => "İptal edildi · kısmi sonuç", StorageScanStatus.Warnings => "Eksik / atlanan konumlar var", StorageScanStatus.Failed => "Tarama başarısız", _ => "Tamamlandı" };
            SummaryLabel.Text = $"{state} · {FileEntry.FormatSize(result.Nodes[0].Bytes)} mantıksal · {result.Nodes[0].Files:N0} dosya · {result.Nodes[0].Folders:N0} klasör · {result.IssueCount:N0} uyarı · {(result.EnumerationTime + result.AggregationTime).TotalSeconds:0.00} sn";
            DriveLabel.Text = "Fiziksel ayrılmış alan bilinmiyor. Sabit bağlantılar mantıksal olarak her yolda görünür; geri kazanılabilir alan hesaplanmaz.";
            try { var drive = new DriveInfo(Path.GetPathRoot(result.RootPath)!); if (drive.IsReady) DriveLabel.Text += $" Sürücü: {FileEntry.FormatSize(drive.TotalSize)} kapasite / {FileEntry.FormatSize(drive.TotalSize - drive.TotalFreeSpace)} kullanılan / {FileEntry.FormatSize(drive.TotalFreeSpace)} boş. Tarama toplamı sürücü kullanımıyla aynı değildir."; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
            if (result.Status is StorageScanStatus.Failed or StorageScanStatus.Warnings) Message("Tarama uyarıları", "Bazı konumlar toplamda eksik olabilir. Üst menüden atlanan konumları görebilirsiniz.", InfoBarSeverity.Warning);
            _scanning = false; await RefreshViewAsync();
        }
        catch (Exception ex) { if (_active) Message("Analiz tamamlanamadı", ex.Message, InfoBarSeverity.Error); }
        finally
        { _scanning = false; Map.IsHitTestVisible = EntriesList.IsEnabled = FoldersTree.IsEnabled = true; ScanButton.IsEnabled = PathBox.IsEnabled = DriveSelector.IsEnabled = Commands.IsEnabled = SelectionCommands.IsEnabled = true; CancelButton.Visibility = ScanProgress.Visibility = Visibility.Collapsed; UpdateActions(); }
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning || _operation) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync(); if (folder != null) await AnalyzeAsync(folder.Path);
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) { if (PathBox.Text.Trim().Length > 0) await AnalyzeAsync(PathBox.Text.Trim()); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();
    private void Drive_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && DriveSelector.SelectedItem is string drive) PathBox.Text = drive; }
    private Func<StorageNode, bool> CreateFilter()
    {
        string search = CatalogText.Key(SearchBox.Text); string extension = ExtensionSelector.SelectedIndex > 0 ? ExtensionSelector.SelectedItem as string ?? "" : "";
        string category = CategorySelector.SelectedIndex > 0 ? CategorySelector.SelectedItem as string ?? "" : "";
        double minimum = double.IsFinite(MinimumSize.Value) ? Math.Max(0, MinimumSize.Value) * 1024 * 1024 : 0;
        return n => !n.IsDirectory && (search.Length == 0 || CatalogText.Key(n.Name).Contains(search)) && (extension.Length == 0 || n.Extension == (extension == "(uzantısız)" ? "" : extension)) && (category.Length == 0 || n.Category == category) && n.Length >= minimum;
    }
    private bool HasFilter => SearchBox.Text.Trim().Length > 0 || ExtensionSelector.SelectedIndex > 0 || CategorySelector.SelectedIndex > 0 || MinimumSize.Value > 0;
    private async Task RefreshViewAsync()
    {
        if (!_active || _snapshot == null || _scanning) return;
        var snapshot = _snapshot; int folder = _folder, selected = _selected, sort = SortSelector.SelectedIndex, version = ++_queryVersion; bool filtered = HasFilter; var filter = CreateFilter();
        _queryCts?.Cancel(); _queryCts?.Dispose(); _queryCts = new(); var token = _queryCts.Token;
        try
        {
            var ids = await Task.Run(() =>
            {
                IEnumerable<int> query = filtered ? snapshot.Descendants(folder).Where(i => filter(snapshot.Nodes[i])) : snapshot.Children(folder);
                query = sort switch { 1 => query.OrderBy(i => snapshot.Nodes[i].Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i), 2 => query.OrderByDescending(i => snapshot.Nodes[i].Files).ThenBy(i => i), _ => query.OrderByDescending(i => snapshot.Nodes[i].Bytes).ThenBy(i => i) };
                var list = new List<int>(); foreach (int id in query) { token.ThrowIfCancellationRequested(); list.Add(id); }
                return list.ToArray();
            }, token);
            if (version != _queryVersion || !_active) return; _visibleIds = ids;
            if (filtered && selected >= 0 && !filter(snapshot.Nodes[selected]))
            { _selectionVersion++; _selected = selected = -1; _selectedEntry = null; _catalogId = null; SelectedLabel.Text = "Haritadan veya listeden bir öğe seçin."; TagsLabel.Text = ""; UpdateActions(); }
            if (_offset >= ids.Length) _offset = Math.Max(0, (ids.Length - 1) / 200 * 200);
            PublishRows(selected);
            var crumbs = new Stack<Crumb>(); int current = folder; while (current >= 0) { crumbs.Push(new(current, snapshot.Nodes[current].Name)); current = snapshot.Nodes[current].Parent; }
            Breadcrumb.ItemsSource = crumbs.ToArray(); UpButton.IsEnabled = folder > 0;
            Map.SetSnapshot(snapshot, folder, filtered ? filter : null); Map.SelectNode(selected);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message("Görünüm yüklenemedi", ex.Message, InfoBarSeverity.Error); }
    }
    private Row MakeRow(int id)
    {
        var n = _snapshot!.Nodes[id]; long total = _snapshot.Nodes[_folder].Bytes;
        return new(id, n.Name, FileEntry.FormatSize(n.Bytes), $"%{(total == 0 ? 0 : n.Bytes * 100d / total):0.##}", n.IsDirectory ? $"Klasör · {n.Files:N0} dosya / {n.Folders:N0} alt klasör{(n.Incomplete ? " · Eksik" : "")}" : $"{n.Category} · {n.Extension}{(n.Incomplete ? " · Eksik" : "")}");
    }
    private void PublishRows(int selected)
    {
        var rows = _visibleIds.Skip(_offset).Take(200).Select(MakeRow).ToList();
        if (selected >= 0 && !rows.Any(r => r.Id == selected)) rows.Insert(0, MakeRow(selected));
        _updating = true;
        try { EntriesList.ItemsSource = rows; EntriesList.SelectedItem = rows.FirstOrDefault(r => r.Id == selected); }
        finally { _updating = false; }
        ListLabel.Text = $"{_visibleIds.Length:N0} öğe · {_offset / 200 + 1} / {Math.Max(1, (_visibleIds.Length + 199) / 200)}{(HasFilter ? " · Filtreli" : "")}";
    }
    private void BuildTree()
    {
        FoldersTree.RootNodes.Clear(); _treeIds.Clear(); if (_snapshot == null) return;
        var root = MakeTreeNode(0); FoldersTree.RootNodes.Add(root); LoadTreeChildren(root); root.IsExpanded = true;
    }
    private TreeViewNode MakeTreeNode(int id)
    {
        var n = _snapshot!.Nodes[id]; var node = new TreeViewNode { Content = $"{n.Name} · {FileEntry.FormatSize(n.Bytes)}", HasUnrealizedChildren = _snapshot.Children(id).Any(i => _snapshot.Nodes[i].IsDirectory) };
        _treeIds[node] = id; return node;
    }
    private void LoadTreeChildren(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren || _snapshot == null || !_treeIds.TryGetValue(node, out int id)) return;
        foreach (int child in _snapshot.Children(id).Where(i => _snapshot.Nodes[i].IsDirectory).OrderByDescending(i => _snapshot.Nodes[i].Bytes).Take(250)) node.Children.Add(MakeTreeNode(child));
        node.HasUnrealizedChildren = false;
    }
    private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args) => LoadTreeChildren(args.Node);
    private async void Tree_Invoked(TreeView sender, TreeViewItemInvokedEventArgs args) { if (args.InvokedItem is TreeViewNode node && _treeIds.TryGetValue(node, out int id)) await NavigateAsync(id); }
    private async Task NavigateAsync(int id) { if (_snapshot == null || !_snapshot.Nodes[id].IsDirectory || _operation || _scanning) return; _selectionVersion++; _folder = id; _selected = -1; _selectedEntry = null; _catalogId = null; _offset = 0; SelectedLabel.Text = "Haritadan veya listeden bir öğe seçin."; TagsLabel.Text = ""; UpdateActions(); await RefreshViewAsync(); }
    private async void Up_Click(object sender, RoutedEventArgs e) { if (_snapshot != null && _folder > 0) await NavigateAsync(_snapshot.Nodes[_folder].Parent); }
    private async void Root_Click(object sender, RoutedEventArgs e) { if (_snapshot != null) await NavigateAsync(0); }
    private async void Breadcrumb_Click(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) { if (args.Item is Crumb crumb) await NavigateAsync(crumb.Id); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready && !_updating) { _filterTimer.Stop(); _filterTimer.Start(); } }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && !_updating) { _offset = 0; await RefreshViewAsync(); } }
    private async void Minimum_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (_ready && !_updating) { _offset = 0; await RefreshViewAsync(); } }
    private async void ClearFilters_Click(object sender, RoutedEventArgs e)
    { _updating = true; try { SearchBox.Text = ""; ExtensionSelector.SelectedIndex = CategorySelector.SelectedIndex = 0; MinimumSize.Value = 0; } finally { _updating = false; } _offset = 0; await RefreshViewAsync(); }
    private void Extension_Selected(object sender, SelectionChangedEventArgs e) { if (_updating || ExtensionsList.SelectedItem is not ExtensionRow row) return; ExtensionSelector.SelectedItem = row.Extension.Length == 0 ? "(uzantısız)" : row.Extension; }
    private void Previous_Click(object sender, RoutedEventArgs e) { _offset = Math.Max(0, _offset - 200); PublishRows(_selected); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_offset + 200 < _visibleIds.Length) _offset += 200; PublishRows(_selected); }
    private async Task SelectMapNodeAsync(int id) { if (_snapshot == null || _scanning || _operation) return; _selected = id; PublishRows(id); EntriesList.ScrollIntoView(EntriesList.SelectedItem); await LoadSelectionAsync(id); }
    private async void Entry_Selected(object sender, SelectionChangedEventArgs e) { if (_updating || _scanning || _operation || EntriesList.SelectedItem is not Row row) return; _selected = row.Id; Map.SelectNode(row.Id); await LoadSelectionAsync(row.Id); }
    private async void Entry_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (_snapshot != null && _selected >= 0 && _snapshot.Nodes[_selected].IsDirectory) await NavigateAsync(_selected); }
    private async Task LoadSelectionAsync(int id)
    {
        if (_snapshot == null) return; int version = ++_selectionVersion; var snapshot = _snapshot;
        _selectedEntry = null; _catalogId = null; var n = snapshot.Nodes[id]; SelectedLabel.Text = $"{n.Name} · {FileEntry.FormatSize(n.Bytes)} · Ayrılmış alan bilinmiyor"; TagsLabel.Text = "Etiketler okunuyor…"; UpdateActions();
        try
        {
            var entry = await Task.Run(() => snapshot.GetEntry(id) with { NativeId = FileIdentity.TryRead(snapshot.GetPath(id)) });
            var catalogId = await AppWorkspace.Catalog.FindAnalysisItemIdAsync(entry);
            var tags = catalogId == null ? [] : await AppWorkspace.Catalog.GetFileTagsAsync(catalogId);
            if (version != _selectionVersion || !_active) return; _selectedEntry = entry; _catalogId = catalogId;
            TagsLabel.Text = tags.Count > 0 ? string.Join(" · ", tags.Select(t => t.DisplayName)) : "Etiket yok · Etiketle yalnızca bu öğeyi kataloğa kaydeder"; UpdateActions();
        }
        catch (Exception ex) { if (version == _selectionVersion && _active) { TagsLabel.Text = "Öğe okunamadı"; Message("Öğe okunamadı", ex.Message, InfoBarSeverity.Warning); } }
    }
    private void UpdateActions()
    {
        bool ready = _selectedEntry != null && !_scanning && !_operation;
        PreviewButton.IsEnabled = TagButton.IsEnabled = OpenButton.IsEnabled = RevealButton.IsEnabled = PropertiesButton.IsEnabled = ready;
        RemoveTagButton.IsEnabled = ready && _catalogId != null;
        DeleteButton.IsEnabled = ready && !_selectedEntry!.IsDirectory && _selectedEntry.NativeId != null && (_snapshot!.Nodes[_selected].Attributes & (FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline)) == 0;
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || _operation) return; _operation = true; UpdateActions();
        try { var entry = _selectedEntry; _preview = new FilePreview { Width = 480, Height = 420, ReleaseOnUnload = false }; var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = entry.Name, Content = _preview, CloseButtonText = "Kapat" }; _ = _preview.ShowAsync(entry); await dialog.ShowAsync(); dialog.Content = null; }
        finally { await _preview.ReleaseAsync(); _operation = false; UpdateActions(); }
    }
    private async void Tag_Click(object sender, RoutedEventArgs e) => await EditTagsAsync(true);
    private async void RemoveTag_Click(object sender, RoutedEventArgs e) => await EditTagsAsync(false);
    private async Task EditTagsAsync(bool add)
    {
        if (_selectedEntry == null || _operation) return; var entry = _selectedEntry; int id = _selected; _operation = true; UpdateActions();
        try
        {
            if (_catalogId == null && add) _catalogId = await AppWorkspace.Catalog.RegisterAnalysisEntryAsync(entry);
            if (_catalogId == null) return;
            await TagDialog.ShowAsync(XamlRoot, [_catalogId], add, entry.IsDirectory ? "klasör" : "dosya"); await LoadSelectionAsync(id);
        }
        catch (Exception ex) { Message("Etiket işlemi tamamlanamadı", ex.Message, InfoBarSeverity.Error); }
        finally { _operation = false; UpdateActions(); }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    { if (_selectedEntry == null) return; try { Process.Start(new ProcessStartInfo(_selectedEntry.FullPath) { UseShellExecute = true }); } catch (Exception ex) { Message("Açılamadı", ex.Message, InfoBarSeverity.Error); } }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    { if (_selectedEntry == null) return; try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{_selectedEntry.FullPath}\"" }); } catch (Exception ex) { Message("Konum açılamadı", ex.Message, InfoBarSeverity.Error); } }
    private void Properties_Click(object sender, RoutedEventArgs e)
    { if (_selectedEntry == null) return; try { FileProperties.Show(_selectedEntry.FullPath, App.WindowHandle); } catch (Exception ex) { Message("Özellikler açılamadı", ex.Message, InfoBarSeverity.Error); } }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || !DeleteButton.IsEnabled || _snapshot == null) return;
        var entry = _selectedEntry; string root = _snapshot.RootPath; bool recycled = false; _operation = true; UpdateActions();
        try
        {
            var result = await new ContentDialog { XamlRoot = XamlRoot, Title = "Dosya Çöp Kutusu’na gönderilsin mi?", Content = $"{entry.Name}\n{FileEntry.FormatSize(entry.Size)} mantıksal boyut. Geri kazanılacak fiziksel alan bilinmiyor.", PrimaryButtonText = "Çöp Kutusu’na gönder", CloseButtonText = "Vazgeç", DefaultButton = ContentDialogButton.Close }.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            FileSafety.Validate(root, entry); if (FileIdentity.TryRead(entry.FullPath) != entry.NativeId) throw new IOException("Dosyanın kimliği değişti. Yeniden analiz edin.");
            var session = new CatalogReviewSession(AppWorkspace.Catalog); await session.LoadAsync(root); await session.RecordAsync(entry, "viewed");
            await _preview.ReleaseAsync(); await DeletionService.DeleteAsync(root, entry, false, App.WindowHandle); recycled = true; await session.RecordAsync(entry, "recycle");
            _operation = false; await AnalyzeAsync(root);
        }
        catch (Exception ex) { if (recycled) { _operation = false; await AnalyzeAsync(root); Message("Dosya Çöp Kutusu’na gönderildi; işlem kaydı yazılamadı", ex.Message, InfoBarSeverity.Warning); } else Message("Dosya silinemedi", ex.Message, InfoBarSeverity.Error); }
        finally { _operation = false; UpdateActions(); }
    }
    private async void Issues_Click(object sender, RoutedEventArgs e)
    { if (_snapshot == null || _operation) return; _operation = true; try { await new ContentDialog { XamlRoot = XamlRoot, Title = $"{_snapshot.IssueCount:N0} atlanan / okunamayan konum", Content = new ScrollViewer { MaxHeight = 450, Content = new TextBlock { Text = string.Join("\n\n", _snapshot.Issues.Select(i => i.Path + "\n" + i.Reason)), TextWrapping = TextWrapping.Wrap } }, CloseButtonText = "Kapat" }.ShowAsync(); } finally { _operation = false; } }
    private void Expand_Click(object sender, RoutedEventArgs e) { _expanded = !_expanded; UpperRow.Height = new(_expanded ? 0 : 245); Tables.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible; }
    private void Splitter_DragDelta(object sender, DragDeltaEventArgs e) { if (!_expanded) UpperRow.Height = new(Math.Clamp(UpperRow.ActualHeight + e.VerticalChange, 100, Math.Max(100, LayoutRoot.ActualHeight - 350))); }
    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    { if (!_ready) return; bool wide = e.NewSize.Width >= 1000; TreeColumn.Width = new(wide ? 220 : 0); TreePanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed; ExtensionColumn.Width = new(e.NewSize.Width >= 800 ? 240 : 0); ExtensionsPanel.Visibility = e.NewSize.Width >= 800 ? Visibility.Visible : Visibility.Collapsed; }
    private void Message(string title, string text, InfoBarSeverity severity) { MessageBar.Title = title; MessageBar.Message = text; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
}
