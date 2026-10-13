using System.Diagnostics;
using CleanTag.Controls;
using CleanTag.Core;
using CleanTag.Services;
using CleanTag.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace CleanTag;

public sealed partial class LibraryPage : Page, IWorkspacePage
{
    public LibraryViewModel Model { get; } = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _watchPending;
    private readonly ThumbnailCache _thumbnails = new();
    private CancellationTokenSource? _scanCts, _thumbnailCts;
    private IReadOnlyList<CatalogSource> _sources = [];
    private bool _ready, _busy, _loadingFilters, _active, _updatingItems, _filtersOpen;
    private int _queryVersion;
    private TagChange? _lastTagChange;
    private CatalogFile? _selected;
    private TaskCompletionSource? _operationDone;
    public bool CanLeave => !_busy;
    public bool CanClose => !_busy || CancelScanButton.Visibility == Visibility.Visible;
    private bool FoldersOnly => TaggingMode.SelectedIndex == 1;
    private string ItemLabel => FoldersOnly ? "klasör" : "dosya";

    public LibraryPage()
    {
        InitializeComponent();
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); Model.Offset = 0; await LoadPageAsync(); };
        _watchTimer.Tick += async (_, _) => await RefreshWatchedResultsAsync();
        _ready = true;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _active = true;
        AppWorkspace.Monitor.Updated += Monitor_Updated;
        MonitoringButton.IsChecked = AppWorkspace.Monitor.Enabled;
        try { await ReloadFiltersAsync(); await LoadPageAsync(); }
        catch (Exception ex) { ShowError("Kütüphane açılamadı", ex); }
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    { AppWorkspace.Monitor.Updated -= Monitor_Updated; _watchTimer.Stop(); _active = false; _queryVersion++; _searchTimer.Stop(); _thumbnailCts?.Cancel(); PreviewSurface.Clear(); _lastTagChange = null; UndoTagsButton.IsEnabled = false; }
    public async Task PrepareToLeaveAsync()
    { _active = false; _watchTimer.Stop(); _searchTimer.Stop(); _queryVersion++; _scanCts?.Cancel(); if (_busy && _operationDone != null) await _operationDone.Task; _thumbnailCts?.Cancel(); await PreviewSurface.ReleaseAsync(); await AppWorkspace.Catalog.FlushAsync(); }

    private void Monitor_Updated(MonitorUpdate update) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_active) return;
        if (update.Warning) { MessageBar.Title = "Otomatik güncelleme"; MessageBar.Message = update.Message; MessageBar.Severity = InfoBarSeverity.Warning; MessageBar.IsOpen = true; }
        if (update.Changes > 0) { _watchPending = true; _watchTimer.Start(); }
    });
    private async Task RefreshWatchedResultsAsync()
    {
        if (!_active) { _watchTimer.Stop(); return; }
        if (!_watchPending) { _watchTimer.Stop(); return; }
        if (_busy || _loadingFilters || _filtersOpen || _searchTimer.IsEnabled || TaggingMode.IsDropDownOpen) return;
        if (SelectedItems.Count > 0) { UpdateResultsButton.Visibility = Visibility.Visible; _watchTimer.Stop(); return; }
        _watchPending = false; _watchTimer.Stop(); UpdateResultsButton.Visibility = Visibility.Collapsed;
        try { await ReloadFiltersAsync(); await LoadPageAsync(); }
        catch (Exception ex) { ShowError("Güncel sonuçlar yüklenemedi", ex); }
    }
    private async void UpdateResults_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _watchPending = false; _watchTimer.Stop(); UpdateResultsButton.Visibility = Visibility.Collapsed;
        await PreviewSurface.ReleaseAsync(); await ReloadFiltersAsync(); await LoadPageAsync();
    }
    private async void Monitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true);
        try { await AppWorkspace.Monitor.SetEnabledAsync(MonitoringButton.IsChecked == true); }
        catch (Exception ex) { MonitoringButton.IsChecked = AppWorkspace.Monitor.Enabled; ShowError("İzleme tercihi kaydedilemedi", ex); }
        finally { SetBusy(false); }
    }

    private async Task ReloadFiltersAsync()
    {
        string? sourceId = (SourceFilter.SelectedItem as CatalogSource)?.Id;
        string? tagId = (TagFilter.SelectedItem as CatalogTag)?.Id;
        _loadingFilters = true;
        try
        {
            _sources = await AppWorkspace.Catalog.GetSourcesAsync();
            var sourceOptions = new[] { new CatalogSource("", "", "Tüm konumlar", "") }.Concat(_sources).ToList();
            if (!SourceFilter.Items.Cast<CatalogSource>().SequenceEqual(sourceOptions))
            {
                SourceFilter.ItemsSource = sourceOptions;
                SourceFilter.SelectedItem = sourceOptions.FirstOrDefault(s => s.Id == sourceId) ?? sourceOptions[0];
            }
            var tags = await AppWorkspace.Catalog.GetTagsAsync();
            var tagOptions = new[] { new CatalogTag("", "Tüm etiketler", "", "", "", 0) }.Concat(tags.Select(t => t with { Count = 0 })).ToList();
            if (!TagFilter.Items.Cast<CatalogTag>().SequenceEqual(tagOptions))
            {
                TagFilter.ItemsSource = tagOptions;
                TagFilter.SelectedItem = tagOptions.FirstOrDefault(t => t.Id == tagId) ?? tagOptions[0];
            }
        }
        finally { _loadingFilters = false; }
    }

    private async Task LoadPageAsync()
    {
        if (!_ready || !_active) return;
        int version = ++_queryVersion;
        _thumbnailCts?.Cancel(); _thumbnailCts?.Dispose(); _thumbnailCts = new();
        _selected = null; PreviewSurface.Clear();
        var query = new CatalogQuery(SearchBox.Text, (SourceFilter.SelectedItem as CatalogSource)?.Id,
            (TagFilter.SelectedItem as CatalogTag)?.Id, TypeFilter.SelectedIndex > 0 ? TypeFilter.SelectedItem as string ?? "" : "",
            FoldersOnly ? StateFilter.SelectedIndex switch { 1 => "untagged", 2 => "missing", _ => "" } : StateFilter.SelectedIndex switch { 1 => "keep", 2 => "defer", 3 => "pending", 4 => "untagged", 5 => "missing", _ => "" },
            Model.Offset, LibraryViewModel.PageSize, SortFilter.SelectedIndex switch { 1 => "size", 2 => "newest", _ => "name" }, FoldersOnly);
        try
        {
            var page = await AppWorkspace.Catalog.SearchAsync(query);
            if (version != _queryVersion || !_active) return;
            if (page.Total > 0 && Model.Offset >= page.Total)
            { Model.Offset = Math.Max(0, ((page.Total - 1) / LibraryViewModel.PageSize) * LibraryViewModel.PageSize); await LoadPageAsync(); return; }
            _updatingItems = true;
            try
            {
                if (!Model.Files.Select(item => item.File.Id).SequenceEqual(page.Files.Select(file => file.Id)))
                {
                    // Finish selection/focus cleanup before WinUI replaces selected gallery containers.
                    // Metadata-only updates keep the existing objects and selection intact.
                    FileList.SelectedItems.Clear(); FileGrid.SelectedItems.Clear();
                    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => idle.TrySetResult())) await idle.Task;
                    if (version != _queryVersion || !_active) return;
                }
                Model.SetPage(page);
            }
            finally { _updatingItems = false; }
            EmptyPanel.Visibility = page.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = _sources.Count == 0 ? "Kütüphaneni oluştur" : $"{(FoldersOnly ? "Klasör" : "Dosya")} bulunamadı";
            EmptyDescription.Text = _sources.Count == 0 ? "Klasör ekle ile dosyalarını burada topla." : FoldersOnly ? "Klasör ekle ile doğrudan kaydet. Alt klasörler için üst menüde Alt klasörleri tara'yı kullan." : "Aramayı veya seçili filtreleri değiştir.";
            ResultLabel.Text = $"{page.Total:N0} {ItemLabel}";
            int filterCount = (TypeFilter.SelectedIndex > 0 ? 1 : 0) + (StateFilter.SelectedIndex > 0 ? 1 : 0);
            FiltersButton.Content = filterCount == 0 ? "Filtreler" : $"Filtreler · {filterCount}";
            PageLabel.Text = page.Total == 0 ? "0 / 0" : $"{Model.Offset / LibraryViewModel.PageSize + 1} / {(page.Total + LibraryViewModel.PageSize - 1) / LibraryViewModel.PageSize}";
            PreviousButton.IsEnabled = Model.Offset > 0 && !_busy;
            NextButton.IsEnabled = Model.Offset + LibraryViewModel.PageSize < page.Total && !_busy;
            UpdateSelection();
        }
        catch (Exception ex) { if (version == _queryVersion) ShowError("Arama tamamlanamadı", ex); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    { if (!_ready || _busy) return; _searchTimer.Stop(); _searchTimer.Start(); }
    private async void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _busy) return;
        _searchTimer.Stop(); _loadingFilters = true;
        try
        {
            if (StateFilter.ItemsSource == null) StateFilter.Items.Clear();
            StateFilter.ItemsSource = FoldersOnly ? new[] { "Tüm klasörler", "Etiketsizler", "Bulunamayanlar" } : new[] { "Tüm dosyalar", "Tutulanlar", "Ertelenenler", "İncelenmeyenler", "Etiketsizler", "Bulunamayanlar" };
            StateFilter.SelectedIndex = 0; TypeFilter.SelectedIndex = 0;
            TypeFilter.Visibility = FoldersOnly ? Visibility.Collapsed : Visibility.Visible;
            ScanFoldersButton.Visibility = FoldersOnly ? Visibility.Visible : Visibility.Collapsed;
            ModeDescription.Text = FoldersOnly ? "Klasör etiketleri içindeki dosyalara aktarılmaz." : "Dosyaları seçerek etiketleyin.";
            SearchBox.PlaceholderText = $"{(FoldersOnly ? "Klasör" : "Dosya")}, etiket veya konum ara";
            _lastTagChange = null; UndoTagsButton.IsEnabled = false; Model.Offset = 0;
            await PreviewSurface.ReleaseAsync(); await LoadPageAsync();
        }
        catch (Exception ex) { ShowError("Mod değiştirilemedi", ex); }
        finally { _loadingFilters = false; }
    }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loadingFilters || _busy) return;
        if (BrowserPane.DisplayMode == SplitViewDisplayMode.Overlay && (ReferenceEquals(sender, SourceFilter) || ReferenceEquals(sender, TagFilter))) BrowserPane.IsPaneOpen = false;
        _searchTimer.Stop(); Model.Offset = 0; await LoadPageAsync();
    }
    private void Browse_Click(object sender, RoutedEventArgs e) => BrowserPane.IsPaneOpen = !BrowserPane.IsPaneOpen;
    private void Filters_Opened(object sender, object e) => _filtersOpen = true;
    private void Filters_Closed(object sender, object e) { _filtersOpen = false; if (_watchPending) _watchTimer.Start(); }
    private async void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _loadingFilters = true;
        try { SourceFilter.SelectedIndex = 0; TagFilter.SelectedIndex = 0; TypeFilter.SelectedIndex = 0; StateFilter.SelectedIndex = 0; SortFilter.SelectedIndex = 0; SearchBox.Text = ""; }
        finally { _loadingFilters = false; }
        _searchTimer.Stop(); FiltersFlyout.Hide(); Model.Offset = 0; await LoadPageAsync();
    }
    private void View_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        FileList.SelectedItems.Clear(); FileGrid.SelectedItems.Clear();
        FileList.Visibility = ViewFilter.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        FileGrid.Visibility = ViewFilter.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }
    private IReadOnlyList<LibraryItem> SelectedItems => (ViewFilter.SelectedIndex == 0 ? FileList.SelectedItems : FileGrid.SelectedItems).Cast<LibraryItem>().ToArray();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && !_updatingItems) UpdateSelection(); }
    private void UpdateSelection()
    {
        var items = SelectedItems;
        _selected = items.Count == 1 ? items[0].File : null;
        AddTagsButton.IsEnabled = RemoveTagsButton.IsEnabled = items.Count > 0 && !_busy;
        SelectionCommands.Visibility = items.Count > 0 || _lastTagChange != null ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = RevealButton.IsEnabled = _selected is { Available: true } && !_busy;
        SelectedName.Text = _selected?.Name ?? (items.Count > 1 ? $"{items.Count} {ItemLabel} seçildi" : "Bir öğe seçin");
        SelectedInfo.Text = _selected == null ? "" : $"{_selected.TypeLabel} · {_selected.SizeLabel} · {_selected.StatusLabel}";
        SelectedTags.Text = _selected != null ? (_selected.Tags.Count == 0 ? "Etiketsiz" : "Etiketler") : (items.Count > 1 ? "Seçili öğelere birlikte etiket ekleyebilir veya kaldırabilirsiniz." : "");
        OpenButton.Content = FoldersOnly ? "Klasörü aç" : "Dosyayı aç";
        SelectedTagChips.ItemsSource = _selected?.Tags;
        SelectedPath.Text = _selected?.Path ?? "";
        if (_selected != null && DetailPanel.Visibility == Visibility.Visible) _ = PreviewSurface.ShowAsync(_selected.Entry);
        else PreviewSurface.Clear();
    }

    private async void Container_Changed(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not LibraryItem item || item.LoadingThumbnail || _thumbnailCts == null) return;
        item.LoadingThumbnail = true; var token = _thumbnailCts.Token;
        try
        {
            if (!item.File.Available || item.File.IsFolder) return;
            var bytes = await _thumbnails.GetAsync(item.File.Entry, token, 320);
            if (bytes == null || token.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(token); }
            stream.Seek(0); var image = new BitmapImage { DecodePixelWidth = 320 }; await image.SetSourceAsync(stream).AsTask(token);
            if (!token.IsCancellationRequested) item.Thumbnail = image;
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void SetBusy(bool busy, bool disableContent = true)
    {
        if (busy && !_busy) _operationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!busy) _operationDone?.TrySetResult();
        _busy = busy;
        if (busy && !disableContent) return; // ContentDialog owns input; preserve the selector and command focus while it closes.
        MainCommands.IsEnabled = SearchBox.IsEnabled = TaggingMode.IsEnabled = SelectionCommands.IsEnabled = !busy;
        SourceFilter.IsEnabled = TagFilter.IsEnabled = FiltersButton.IsEnabled = BrowseButton.IsEnabled = !busy;
        foreach (var control in FilterPanel.Children.OfType<Control>()) control.IsEnabled = !busy;
        FileList.IsEnabled = FileGrid.IsEnabled = !busy;
        PreviousButton.IsEnabled = !busy && Model.Offset > 0;
        NextButton.IsEnabled = !busy && Model.Offset + LibraryViewModel.PageSize < Model.Total;
        OpenButton.IsEnabled = RevealButton.IsEnabled = !busy && _selected is { Available: true };
        if (!busy) UpdateSelection();
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true);
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder }; picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            var source = await AppWorkspace.Catalog.AddSourceAsync(folder.Path);
            await ReloadFiltersAsync();
            SourceFilter.SelectedItem = SourceFilter.Items.Cast<CatalogSource>().First(s => s.Id == source.Id);
            if (FoldersOnly) await AppWorkspace.Catalog.AddFolderAsync(folder.Path);
            else await ScanAsync([source]);
            await ReloadFiltersAsync();
            Model.Offset = 0; await LoadPageAsync();
        }
        catch (Exception ex) { ShowError("Klasör eklenemedi", ex); }
        finally { SetBusy(false); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true);
        try { await PreviewSurface.ReleaseAsync(); if (FoldersOnly) await AppWorkspace.Catalog.RefreshFoldersAsync(); else await ScanAsync(_sources); await ReloadFiltersAsync(); await LoadPageAsync(); }
        catch (Exception ex) { ShowError("Yenileme tamamlanamadı", ex); }
        finally { SetBusy(false); }
    }
    private async void ScanFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !FoldersOnly) return; SetBusy(true);
        try
        {
            var selected = SourceFilter.SelectedItem as CatalogSource;
            var sources = string.IsNullOrEmpty(selected?.Id) ? _sources : _sources.Where(s => s.Id == selected.Id).ToArray();
            await PreviewSurface.ReleaseAsync(); await ScanAsync(sources, true); await ReloadFiltersAsync(); await LoadPageAsync();
        }
        catch (Exception ex) { ShowError("Alt klasörler taranamadı", ex); }
        finally { SetBusy(false); }
    }
    private async Task ScanAsync(IReadOnlyList<CatalogSource> sources, bool foldersOnly = false)
    {
        _scanCts?.Dispose(); _scanCts = new(); var token = _scanCts.Token; CancelScanButton.Visibility = Visibility.Visible;
        int errors = 0;
        try
        {
            foreach (var source in sources)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    bool firstBatch = true;
                    async Task PublishFirstBatch(IReadOnlyList<FileEntry> entries)
                    {
                        if (!firstBatch) return; firstBatch = false;
                        await AppWorkspace.Catalog.IndexScanAsync(source.Path, new(entries.ToList(), [], 0, true), foldersOnly);
                        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (DispatcherQueue.TryEnqueue(async () => { try { await LoadPageAsync(); shown.TrySetResult(); } catch (Exception ex) { shown.TrySetException(ex); } })) await shown.Task;
                    }
                    var result = await FileScanner.ScanAsync(source.Path,
                        new Progress<ScanProgress>(p => ResultLabel.Text = $"{source.Name} · {p.Files:N0} {(foldersOnly ? "klasör" : "dosya")} tarandı"), token, [AppWorkspace.StateDirectory], foldersOnly, PublishFirstBatch);
                    await AppWorkspace.Catalog.IndexScanAsync(source.Path, result, foldersOnly);
                    errors += result.Errors.Count;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { errors++; MessageBar.Title = source.Name; MessageBar.Message = ex.Message; MessageBar.Severity = InfoBarSeverity.Warning; MessageBar.IsOpen = true; }
            }
            if (errors > 0) { MessageBar.Title = "Bazı konumlar okunamadı"; MessageBar.Message = "Önceki kayıtlar ve etiketler korundu. Erişimi kontrol edip yeniden deneyebilirsiniz."; MessageBar.Severity = InfoBarSeverity.Warning; MessageBar.IsOpen = true; }
        }
        finally { CancelScanButton.Visibility = Visibility.Collapsed; }
    }
    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private async void AddTags_Click(object sender, RoutedEventArgs e) => await EditTagsAsync(true);
    private async void RemoveTags_Click(object sender, RoutedEventArgs e) => await EditTagsAsync(false);
    private async Task EditTagsAsync(bool add)
    {
        if (_busy || SelectedItems.Count == 0) return;
        string[] ids = SelectedItems.Select(i => i.File.Id).ToArray(); SetBusy(true, disableContent: false);
        try
        {
            var change = await TagDialog.ShowAsync(XamlRoot, ids, add, ItemLabel);
            if (change == null) return;
            // Let the dialog's queued focus/layout work finish before replacing selection containers.
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => idle.TrySetResult())) await idle.Task;
            _lastTagChange = change; UndoTagsButton.IsEnabled = change.Added.Count + change.Removed.Count > 0;
            await ReloadFiltersAsync(); await LoadPageAsync();
            ResultLabel.Text = $"{Model.Total:N0} {ItemLabel} · Etiketler kaydedildi";
        }
        catch (Exception ex) { ShowError("Etiket işlemi tamamlanamadı", ex); }
        finally { SetBusy(false); }
    }
    private async void UndoTags_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _lastTagChange == null) return; SetBusy(true);
        try { await AppWorkspace.Catalog.UndoTagsAsync(_lastTagChange); _lastTagChange = null; UndoTagsButton.IsEnabled = false; await ReloadFiltersAsync(); await LoadPageAsync(); }
        catch (Exception ex) { ShowError("Etiket işlemi geri alınamadı", ex); }
        finally { SetBusy(false); }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) { if (!_busy) { Model.Offset = Math.Max(0, Model.Offset - LibraryViewModel.PageSize); await LoadPageAsync(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (!_busy && Model.Offset + LibraryViewModel.PageSize < Model.Total) { Model.Offset += LibraryViewModel.PageSize; await LoadPageAsync(); } }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null) return;
        try { if (!(_selected.IsFolder ? Directory.Exists(_selected.Path) : File.Exists(_selected.Path))) throw new FileNotFoundException("Öğe taşınmış veya silinmiş. Yenile'yi kullanın."); Process.Start(new ProcessStartInfo(_selected.Path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Dosya açılamadı", ex); }
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null) return;
        try { var start = new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{_selected.Path}\"" }; Process.Start(start); }
        catch (Exception ex) { ShowError("Konum açılamadı", ex); }
    }
    private void File_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Open_Click(sender, e);
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null) return; SetBusy(true);
        var preview = new FilePreview { Height = 440, Width = 540 };
        var file = _selected;
        try
        {
            await PreviewSurface.ReleaseAsync();
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = file.Name, Content = preview, CloseButtonText = "Kapat" };
            _ = preview.ShowAsync(file.Entry); await dialog.ShowAsync();
        }
        finally { await preview.ReleaseAsync(); SetBusy(false); UpdateSelection(); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true);
        try
        {
            string path = Path.Combine(AppWorkspace.StateDirectory, "backups", $"catalog-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
            await AppWorkspace.Catalog.BackupAsync(path); ShowSuccess("Katalog yedeklendi", path);
            var start = new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" }; Process.Start(start);
        }
        catch (Exception ex) { ShowError("Yedek alınamadı", ex); }
        finally { SetBusy(false); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true);
        try
        {
            string path = Path.Combine(AppWorkspace.StateDirectory, $"catalog-export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await AppWorkspace.Catalog.ExportCatalogAsync(path); ShowSuccess("Katalog dışa aktarıldı", path);
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" });
        }
        catch (Exception ex) { ShowError("Dışa aktarma başarısız", ex); }
        finally { SetBusy(false); }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true);
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".sqlite");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var file = await picker.PickSingleFileAsync(); if (file == null) return;
            var answer = await new ContentDialog { XamlRoot = XamlRoot, Title = "Katalog yedeği geri yüklensin mi?",
                Content = "Etiketler ve katalog seçtiğiniz yedeğe döner. Mevcut kataloğun otomatik yedeği alınır. Dosyaların içeriği etkilenmez.",
                PrimaryButtonText = "Geri yükle", CloseButtonText = "Vazgeç", DefaultButton = ContentDialogButton.Close }.ShowAsync();
            if (answer != ContentDialogResult.Primary) return;
            await PreviewSurface.ReleaseAsync();
            bool monitoring = AppWorkspace.Monitor.Enabled;
            await AppWorkspace.Monitor.SetEnabledAsync(false, persist: false);
            try
            {
                await AppWorkspace.Catalog.RestoreAsync(file.Path);
                await AppWorkspace.Monitor.SetEnabledAsync(await AppWorkspace.Catalog.GetMonitoringEnabledAsync(), persist: false);
            }
            catch { await AppWorkspace.Monitor.SetEnabledAsync(monitoring, persist: false); throw; }
            MonitoringButton.IsChecked = AppWorkspace.Monitor.Enabled;
            _lastTagChange = null; UndoTagsButton.IsEnabled = false; Model.Offset = 0;
            await ReloadFiltersAsync(); await LoadPageAsync(); ShowSuccess("Katalog geri yüklendi", "Konumları güncellemek için Yenile'yi kullanabilirsiniz.");
        }
        catch (Exception ex) { ShowError("Yedek geri yüklenemedi", ex); }
        finally { SetBusy(false); }
    }
    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready) return;
        bool wide = e.NewSize.Width >= 1080;
        DetailColumn.Width = new GridLength(wide ? Math.Clamp(e.NewSize.Width * .25, 290, 360) : 0);
        DetailPanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        bool inline = e.NewSize.Width >= 800;
        var paneMode = inline ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        if (BrowserPane.DisplayMode != paneMode) { BrowserPane.DisplayMode = paneMode; BrowserPane.IsPaneOpen = inline; }
        LayoutRoot.Padding = new Thickness(e.NewSize.Width >= 900 ? 20 : 12, 12, e.NewSize.Width >= 900 ? 20 : 12, 12);
        if (!wide) PreviewSurface.Clear(); else UpdateSelection();
    }
    private void ShowError(string title, Exception ex) { MessageBar.Title = title; MessageBar.Message = ex.Message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    private void ShowSuccess(string title, string message) { MessageBar.Title = title; MessageBar.Message = message; MessageBar.Severity = InfoBarSeverity.Success; MessageBar.IsOpen = true; }
}
