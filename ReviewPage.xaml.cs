using System.Diagnostics;
using System.Text.Json;
using CleanTag.Core;
using CleanTag.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace CleanTag;

public sealed partial class ReviewPage : Page, IWorkspacePage
{
    public bool CanClose => !_busy && !_dialog;
    public bool CanLeave => CanClose && !_scanning;
    public Task PrepareToLeaveAsync() => FlushHistoryAsync();
    public async Task FlushHistoryAsync()
    { _active = false; _existenceTimer.Stop(); _scanCts?.Cancel(); if (_scanDone != null) await _scanDone.Task; await PreviewSurface.ReleaseAsync(); await _session.FlushAsync(); }
    private static readonly string SuggestedFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string StateDirectory = AppWorkspace.StateDirectory;
    private readonly IReviewHistory _session = new CatalogReviewSession(AppWorkspace.Catalog);
    private bool _hasLoaded;
    private bool _active;
    private TaskCompletionSource? _scanDone;
    private int _tagVersion;
    private TagChange? _lastTagChange;
    private readonly ThumbnailCache _thumbnails = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private List<FileEntry> _all = [], _queue = [];
    private Dictionary<string, List<FileEntry>> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private FileEntry? _current, _lastKept;
    private string _root = "";
    private int _index, _kept, _reviewed;
    private long _trashed, _permanentlyDeleted;
    private bool _ready, _busy, _scanning, _dialog, _confirm = true, _permanent, _dragging;
    private double _dragStart;
    private CancellationTokenSource? _scanCts, _previewCts;
    private int _previewVersion;
    private readonly HashSet<string> _deferredThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keptThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _existenceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _missing, _checkingExistence;
    private CancellationTokenSource? _neighborCts;
    public record Crumb(string Name, string Path) { public override string ToString() => Name; }

    public ReviewPage()
    {
        InitializeComponent();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); if (!_busy && !_scanning) ApplyFilters(); };
        _existenceTimer.Tick += async (_, _) => await CheckCurrentExistsAsync();
        _existenceTimer.Start();
        _ready = true;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _active = true;
        AppWorkspace.Monitor.Updated += Monitor_Updated;
        _existenceTimer.Start();
        if (_hasLoaded) { if (_root.Length > 0) { await _session.LoadAsync(_root); ApplyFilters(); } return; }
        _hasLoaded = true;
        QuickStartButton.Visibility = Directory.Exists(SuggestedFolder) ? Visibility.Visible : Visibility.Collapsed;
        var args = Environment.GetCommandLineArgs();
        int folderArg = Array.IndexOf(args, "--folder");
        if (folderArg >= 0 && args.Length > folderArg + 1) await LoadFolderAsync(args[folderArg + 1]);
        else
        {
            try
            {
                string lastFile = Path.Combine(StateDirectory, "last-folder.txt");
                if (File.Exists(lastFile))
                {
                    var last = (await File.ReadAllTextAsync(lastFile)).Trim();
                    if (Directory.Exists(last)) { QuickStartButton.Content = $"Devam et · {Path.GetFileName(last)}"; QuickStartButton.Tag = last; QuickStartButton.Visibility = Visibility.Visible; }
                }
            }
            catch { /* A missing preference must not prevent startup. */ }
        }
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e) { AppWorkspace.Monitor.Updated -= Monitor_Updated; _active = false; _scanCts?.Cancel(); ReleasePreview(); _searchTimer.Stop(); _existenceTimer.Stop(); _neighborCts?.Cancel(); _lastTagChange = null; UndoTagsButton.IsEnabled = false; }

    private void Monitor_Updated(MonitorUpdate update) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_active || _root.Length == 0 || update.Changes == 0 || update.Root.Length == 0) return;
        if (CatalogText.PathKey(_root) == CatalogText.PathKey(update.Root) || FileSafety.IsWithin(_root, update.Root) || FileSafety.IsWithin(update.Root, _root))
            NewResultsButton.Visibility = Visibility.Visible;
    });
    private async void NewResults_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialog || _scanning || _root.Length == 0) return;
        _scanning = true; _scanDone = new(TaskCreationOptions.RunContinuationsAsynchronously); SetButtons();
        try
        {
            await PreviewSurface.ReleaseAsync();
            _all = (await AppWorkspace.Catalog.GetEntriesAsync(_root)).ToList(); _removed.Clear();
            _folders = await Task.Run(() => _all.GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase));
            await _session.LoadAsync(_root);
            _reviewed = _all.Count(_session.IsReviewed); RootStats.Text = $"{_all.Count:N0} dosya · {FileEntry.FormatSize(_all.Sum(f => f.Size))}";
            NewResultsButton.Visibility = Visibility.Collapsed;
            StatusLabel.Text = "Gelenler katalogdan güncellendi";
        }
        catch (Exception ex) { ShowMessage("Yeni gelenler gösterilemedi", ex.Message, InfoBarSeverity.Warning); }
        finally { _scanning = false; _scanDone.TrySetResult(); ApplyFilters(); }
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _scanning || _dialog) return;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) await LoadFolderAsync(folder.Path);
        }
        catch (Exception ex) { ShowMessage("Klasör açılamadı", ex.Message, InfoBarSeverity.Error); }
    }
    private async void QuickStart_Click(object sender, RoutedEventArgs e) => await LoadFolderAsync(QuickStartButton.Tag as string ?? SuggestedFolder);
    private async Task LoadFolderAsync(string root)
    {
        if (_busy || _scanning) return;
        _scanCts?.Cancel(); _scanCts = new(); var token = _scanCts.Token;
        _scanDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _scanning = true; ReleasePreview(); _current = null; SetButtons();
        ChooseButton.IsEnabled = false; QuickStartButton.IsEnabled = false;
        ScanPanel.Visibility = Visibility.Visible; FileCard.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible; EmptyTitle.Text = "Klasör okunuyor…"; EmptyText.Text = "Önizlemeler yalnız dosya sırası geldiğinde hazırlanır.";
        MessageBar.IsOpen = false;
        try
        {
            var result = await FileScanner.ScanAsync(root, new Progress<ScanProgress>(p => ScanLabel.Text = $"{p.Files:N0} dosya · {FileEntry.FormatSize(p.Bytes)} bulundu"), token, [StateDirectory]);
            await AppWorkspace.Catalog.IndexScanAsync(root, result);
            _root = Path.GetFullPath(root); _all = result.Files; _removed.Clear(); _deferredThisSession.Clear(); _keptThisSession.Clear();
            _folders = await Task.Run(() => _all.GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase));
            await _session.LoadAsync(_root);
            _kept = 0; _trashed = 0; _permanentlyDeleted = 0; _lastKept = null;
            _reviewed = _all.Count(_session.IsReviewed);
            RootName.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(_root));
            ToolTipService.SetToolTip(RootName, _root);
            RootStats.Text = $"{_all.Count:N0} dosya · {FileEntry.FormatSize(_all.Sum(f => f.Size))}";
            StatusLabel.Text = result.Cancelled ? "Tarama durduruldu. Bulunan dosyaları inceleyebilirsiniz." : $"Tarama tamamlandı · {result.Skipped:N0} erişilemeyen, sistem veya bağlantılı öğe atlandı";
            if (result.Errors.Count > 0) ShowMessage("Bazı konumlar okunamadı", $"{result.Skipped:N0} öğe atlandı. İlk hata: {result.Errors[0]}", InfoBarSeverity.Warning);
            try { Directory.CreateDirectory(StateDirectory); await File.WriteAllTextAsync(Path.Combine(StateDirectory, "last-folder.txt"), _root); }
            catch { StatusLabel.Text += " · Son klasör tercihi kaydedilemedi"; }
        }
        catch (Exception ex)
        { _all = []; _queue = []; _folders.Clear(); _root = ""; ShowMessage("Tarama tamamlanamadı", ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _scanning = false; ScanPanel.Visibility = Visibility.Collapsed; ChooseButton.IsEnabled = true; QuickStartButton.IsEnabled = true; ApplyFilters();
            _scanDone?.TrySetResult();
        }
    }
    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();
    private void Filter_Changed(object sender, RoutedEventArgs e)
    { if (!_ready) return; _searchTimer.Stop(); _searchTimer.Start(); }
    private void ApplyFilters()
    {
        if (!_ready || _busy || _scanning) return;
        string query = SearchBox.Text.Trim(); string category = TypeFilter.SelectedItem as string ?? "Tüm dosyalar";
        IEnumerable<FileEntry> files = _all.Where(f => !_removed.Contains(f.FullPath) && !_session.IsReviewed(f) && (ShowDeferred.IsOn || !_session.IsDeferred(f) || _deferredThisSession.Contains(f.Identity)));
        if (query.Length > 0) files = files.Where(f => f.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (category != "Tüm dosyalar") files = files.Where(f => f.Category == category);
        files = SortBox.SelectedIndex switch
        {
            1 => files.OrderBy(f => f.ModifiedTicks), 2 => files.OrderByDescending(f => f.ModifiedTicks),
            3 => files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
            4 => files.OrderBy(f => f.FullPath, StringComparer.CurrentCultureIgnoreCase),
            _ => files.OrderByDescending(f => f.Size)
        };
        _queue = files.OrderBy(f => _session.IsDeferred(f) ? 1 : 0).ToList(); _index = 0; ShowCurrent();
    }
    private void ShowCurrent()
    {
        ReleasePreview(); CardTransform.TranslateX = 0; CardTransform.Rotation = 0; FileCard.Opacity = 1;
        _current = _index < _queue.Count ? _queue[_index] : null;
        CurrentTags.Text = ""; TagCurrentButton.IsEnabled = _current != null;
        _missing = false; MissingBar.IsOpen = false;
        UpdateCounters(); SetButtons(); UpdateNavigation();
        if (_current == null)
        {
            FileCard.Visibility = Visibility.Collapsed; EmptyPanel.Visibility = Visibility.Visible; SiblingList.ItemsSource = null;
            SiblingCount.Text = "Dosya seçildiğinde görünür"; SiblingFolder.Text = "";
            EmptyTitle.Text = string.IsNullOrEmpty(_root) ? "Bir klasörle başla." : "Bu sıra tamamlandı.";
            EmptyText.Text = string.IsNullOrEmpty(_root) ? "Dosyaları önizle. Sola kaydırarak sil, sağa kaydırarak tut." : "Başka bir filtre veya klasör seçebilirsin. Tutma kararların kaydedildi.";
            QuickStartButton.Visibility = string.IsNullOrEmpty(_root) && Directory.Exists(SuggestedFolder) ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        EmptyPanel.Visibility = Visibility.Collapsed; FileCard.Visibility = Visibility.Visible;
        FileNameLabel.Text = _current.Name; ToolTipService.SetToolTip(FileNameLabel, _current.Name);
        FileMetaLabel.Text = $"{_current.SizeLabel}  ·  {_current.ModifiedLabel}"; ExtensionLabel.Text = _current.TypeLabel;
        PathBreadcrumb.ItemsSource = GetCrumbs(_current.Folder);
        if (_folders.TryGetValue(_current.Folder, out var siblings))
        {
            var list = siblings.Where(f => f.FullPath != _current.FullPath && !_removed.Contains(f.FullPath)).ToList();
            SiblingList.ItemsSource = list; SiblingCount.Text = $"{list.Count:N0} diğer dosya · seçerek incele"; SiblingFolder.Text = Path.GetFileName(_current.Folder);
        }
        if (!_active) return;
        _ = RecordViewedAsync(_current);
        _ = LoadCurrentTagsAsync(_current);
        if (IsMissing(_current.FullPath)) { MarkMissing(); return; }
        _previewCts = new(); int version = ++_previewVersion;
        _ = LoadPreviewAsync(_current, version, _previewCts.Token);
    }
    private static List<Crumb> GetCrumbs(string folder)
    {
        var result = new List<Crumb>(); var drive = Path.GetPathRoot(folder)!;
        result.Add(new(drive.TrimEnd('\\'), drive)); string current = drive;
        foreach (var part in folder[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        { current = Path.Combine(current, part); result.Add(new(part, current)); }
        return result;
    }
    private void UpdateCounters()
    {
        QueueLabel.Text = _current == null ? "İNCELEME" : $"{_queue.Count - _index:N0} DOSYA SIRADA";
        KeptLabel.Text = $"{_kept:N0} dosya tutuldu"; TrashLabel.Text = $"{FileEntry.FormatSize(_trashed)} Çöp Kutusu’nda";
        PermanentLabel.Text = $"{FileEntry.FormatSize(_permanentlyDeleted)} kalıcı silindi";
        ReviewProgress.Value = _all.Count == 0 ? 0 : Math.Clamp(100.0 * _reviewed / _all.Count, 0, 100);
        UndoKeepButton.IsEnabled = _lastKept != null && !_busy;
    }
    private void SetButtons()
    {
        bool enabled = _current != null && !_missing && !_busy && !_scanning && !_dialog;
        DeleteButton.IsEnabled = KeepButton.IsEnabled = LaterButton.IsEnabled = TagCurrentButton.IsEnabled = enabled;
        SearchBox.IsEnabled = TypeFilter.IsEnabled = SortBox.IsEnabled = ShowDeferred.IsEnabled = !_busy && !_scanning && !_dialog;
        PreviousFileButton.IsEnabled = _index > 0 && !_busy && !_scanning && !_dialog;
        NextFileButton.IsEnabled = _index + 1 < _queue.Count && !_busy && !_scanning && !_dialog;
    }
    private void ReleasePreview()
    {
        _previewCts?.Cancel(); _previewCts?.Dispose(); _previewCts = null; _previewVersion++;
        PreviewSurface.Clear(); SwipeBadge.Visibility = Visibility.Collapsed;
    }
    private async Task LoadPreviewAsync(FileEntry file, int version, CancellationToken token)
    {
        try
        {
            FileSafety.Validate(_root, file);
            await PreviewSurface.ShowAsync(file, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _previewVersion || token.IsCancellationRequested) return;
            if (IsMissing(file.FullPath)) MarkMissing();
            else PreviewSurface.ShowFallback(file.TypeLabel, ex.Message);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DecideAsync(false);
    private async void Keep_Click(object sender, RoutedEventArgs e) => await DecideAsync(true);
    private async Task DecideAsync(bool keep)
    {
        if (_current == null || _missing || _busy || _scanning || _dialog) return;
        var file = _current; var permanent = _permanent; _busy = true; SetButtons();
        try
        {
            if (!keep && _confirm)
            {
                if (!await ConfirmAsync(permanent ? "Kalıcı olarak silinsin mi?" : "Çöp Kutusu’na gönderilsin mi?", $"{file.Name}\n{file.SizeLabel}\n\n{file.FullPath}\n\n" + (permanent ? "Bu işlem uygulamadan geri alınamaz. Dosya Çöp Kutusu’na gitmez." : "Dosyayı Windows Çöp Kutusu’ndan geri yükleyebilirsiniz."), permanent ? "Kalıcı sil" : "Çöp Kutusu’na gönder")) return;
            }
            bool alreadyReviewed = _session.IsReviewed(file);
            if (keep)
            {
                FileSafety.Validate(_root, file);
                await _session.RecordAsync(file, "keep"); _keptThisSession.Add(file.Identity); _kept = _keptThisSession.Count; _lastKept = file;
                StatusLabel.Text = $"Tutuldu · {file.Name}";
            }
            else
            {
                ReleasePreview(); await PreviewSurface.ReleaseAsync();
                await DeletionService.DeleteAsync(_root, file, permanent, App.WindowHandle);
                _removed.Add(file.FullPath);
                _keptThisSession.Remove(file.Identity); _kept = _keptThisSession.Count;
                if (permanent) _permanentlyDeleted += file.Size; else _trashed += file.Size;
                try { await _session.RecordAsync(file, permanent ? "permanent" : "recycle"); }
                catch (Exception ex) { ShowMessage("Dosya silindi, geçmiş kaydedilemedi", ex.Message, InfoBarSeverity.Warning); }
                StatusLabel.Text = (permanent ? "Kalıcı silindi · " : "Çöp Kutusu’na gönderildi · ") + file.Name;
            }
            if (!alreadyReviewed) _reviewed++; await AnimateCardAsync(keep ? 1 : -1); AdvancePending();
        }
        catch (Exception ex)
        { ShowMessage("İşlem tamamlanamadı", ex.Message, InfoBarSeverity.Error); ShowCurrent(); }
        finally { _busy = false; SetButtons(); UpdateCounters(); }
    }
    private async Task AnimateCardAsync(int direction)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var done = new TaskCompletionSource(); var story = new Storyboard();
        var slide = new DoubleAnimation { From = 0, To = direction * 170, Duration = TimeSpan.FromMilliseconds(160), EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        Storyboard.SetTarget(slide, CardTransform); Storyboard.SetTargetProperty(slide, "TranslateX"); story.Children.Add(slide);
        var fade = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(fade, FileCard); Storyboard.SetTargetProperty(fade, "Opacity"); story.Children.Add(fade);
        story.Completed += (_, _) => done.TrySetResult(); story.Begin(); await done.Task; story.Stop();
    }
    private async void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _missing || _busy || _dialog || _scanning) return;
        var file = _current; _busy = true; SetButtons();
        try
        {
            bool wasReviewed = _session.IsReviewed(file);
            await _session.RecordAsync(file, "defer");
            if (wasReviewed) _reviewed--;
            _keptThisSession.Remove(file.Identity); _kept = _keptThisSession.Count;
            if (_lastKept?.Identity == file.Identity) _lastKept = null;
            _deferredThisSession.Add(file.Identity);
            // Preserve the old position for Back. Keep exactly one future copy for the next round.
            ReviewOrder.Defer(_queue, _index);
            StatusLabel.Text = $"Ertelendi · Tur sonunda yeniden gelecek · {file.Name}";
            AdvancePending();
        }
        catch (Exception ex) { ShowMessage("Erteleme kaydedilemedi", ex.Message, InfoBarSeverity.Error); }
        finally { _busy = false; SetButtons(); }
    }
    private void AdvancePending()
    {
        _index = ReviewOrder.NextPending(_queue, _index, f => _removed.Contains(f.FullPath) || _session.IsReviewed(f));
        ShowCurrent();
    }
    private async void UndoKeep_Click(object sender, RoutedEventArgs e)
    {
        if (_lastKept == null || _busy || _scanning || _dialog) return;
        _busy = true; SetButtons();
        try { var file = _lastKept; bool wasReviewed = _session.IsReviewed(file); await _session.RecordAsync(file, "revisit"); _queue.Insert(_index, file); _keptThisSession.Remove(file.Identity); _kept = _keptThisSession.Count; if (wasReviewed) _reviewed--; _lastKept = null; StatusLabel.Text = $"Tutma kararı geri alındı · {file.Name}"; ShowCurrent(); }
        catch (Exception ex) { ShowMessage("Karar geri alınamadı", ex.Message, InfoBarSeverity.Error); }
        finally { _busy = false; SetButtons(); UpdateCounters(); }
    }
    private void Sibling_Click(object sender, ItemClickEventArgs e)
    {
        if (_busy || _scanning || _dialog || e.ClickedItem is not FileEntry file) return;
        if (_session.IsReviewed(file)) { ShowMessage("Bu dosya daha önce tutuldu", "Ayarlar menüsünden incelemeyi yeniden başlatarak tekrar sıraya alabilirsiniz.", InfoBarSeverity.Informational); return; }
        int existing = _queue.FindIndex(_index, f => f.FullPath == file.FullPath);
        if (existing >= _index) _queue.RemoveAt(existing);
        _queue.Insert(_index, file); ShowCurrent();
    }
    private async Task LoadCurrentTagsAsync(FileEntry file)
    {
        int version = ++_tagVersion;
        try
        {
            var id = await AppWorkspace.Catalog.FindFileIdAsync(file);
            var tags = id == null ? Array.Empty<CatalogTag>() : await AppWorkspace.Catalog.GetFileTagsAsync(id);
            if (_current?.Identity == file.Identity && version == _tagVersion)
                CurrentTags.Text = tags.Count == 0 ? "Etiketsiz" : string.Join(" · ", tags.Select(t => t.Name));
        }
        catch (Exception ex) { if (_current?.Identity == file.Identity) ShowMessage("Etiketler okunamadı", ex.Message, InfoBarSeverity.Warning); }
    }
    private async void TagCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _busy || _dialog || _scanning || _missing) return;
        _dialog = true; SetButtons(); var file = _current;
        try
        {
            var id = await AppWorkspace.Catalog.FindFileIdAsync(file);
            if (id == null) throw new IOException("Dosya katalogda bulunamadı. Klasörü yeniden tarayın.");
            _lastTagChange = await TagDialog.ShowAsync(XamlRoot, new[] { id }, true);
            if (_lastTagChange != null) { UndoTagsButton.IsEnabled = true; await LoadCurrentTagsAsync(file); }
        }
        catch (Exception ex) { ShowMessage("Etiket eklenemedi", ex.Message, InfoBarSeverity.Error); }
        finally { _dialog = false; SetButtons(); }
    }
    private async void UndoTags_Click(object sender, RoutedEventArgs e)
    {
        if (_lastTagChange == null || _busy || _dialog) return;
        _busy = true; SetButtons();
        try { await AppWorkspace.Catalog.UndoTagsAsync(_lastTagChange); _lastTagChange = null; UndoTagsButton.IsEnabled = false; if (_current != null) await LoadCurrentTagsAsync(_current); }
        catch (Exception ex) { ShowMessage("Etiket işlemi geri alınamadı", ex.Message, InfoBarSeverity.Error); }
        finally { _busy = false; SetButtons(); }
    }
    private void Breadcrumb_Click(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) { if (args.Item is Crumb c) LaunchExplorer(c.Path); }
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    { if (_current != null) try { Process.Start(new ProcessStartInfo(_current.FullPath) { UseShellExecute = true }); } catch (Exception ex) { ShowMessage("Dosya açılamadı", ex.Message, InfoBarSeverity.Warning); } }
    private void RevealFile_Click(object sender, RoutedEventArgs e) { if (_current != null) LaunchExplorer(_current.FullPath, true); }
    private void OpenRecycle_Click(object sender, RoutedEventArgs e) => LaunchExplorer("shell:RecycleBinFolder");
    private async void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        try { string path = Path.Combine(StateDirectory, "history-export.json"); await AppWorkspace.Catalog.ExportHistoryAsync(path); LaunchExplorer(path, true); }
        catch (Exception ex) { ShowMessage("Geçmiş dışa aktarılamadı", ex.Message, InfoBarSeverity.Error); }
    }
    private async void EmptyRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialog || _scanning) return;
        if (!await ConfirmAsync("Windows Çöp Kutusu tamamen boşaltılsın mı?", "Tüm sürücülerdeki Çöp Kutusu içeriği, başka uygulamalardan silinen dosyalar dahil kalıcı olarak kaldırılır. Bu işlem geri alınamaz.", "Çöp Kutusu’nu boşalt")) return;
        _busy = true; SetButtons();
        try { await DeletionService.EmptyRecycleBinAsync(App.WindowHandle); _trashed = 0; UpdateCounters(); ShowMessage("Çöp Kutusu boşaltıldı", "Windows işlemi tamamladı. Silme geçmişiniz JSON dosyasında korunuyor.", InfoBarSeverity.Success); }
        catch (Exception ex) { ShowMessage("Çöp Kutusu boşaltılamadı", ex.Message, InfoBarSeverity.Error); }
        finally { _busy = false; SetButtons(); }
    }
    private async Task RecordViewedAsync(FileEntry file)
    {
        try { await _session.RecordAsync(file, "viewed"); }
        catch (Exception ex) { ShowMessage("Görüntüleme geçmişi kaydedilemedi", ex.Message, InfoBarSeverity.Warning); }
    }
    private static bool IsMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }
    private void MarkMissing()
    {
        _missing = true; ReleasePreview(); MissingBar.IsOpen = true;
        PreviewSurface.ShowFallback("Dosya artık bu konumda yok", "Dosya veya üst klasörü taşınmış olabilir. Direkt geç ile devam edebilirsiniz."); SetButtons();
    }
    private async Task CheckCurrentExistsAsync()
    {
        if (!_active || _checkingExistence || _current == null || _busy || _scanning || _dialog) return;
        _checkingExistence = true; var file = _current;
        try
        {
            bool missing = await Task.Run(() => IsMissing(file.FullPath));
            if (!_active || _current?.Identity != file.Identity || _busy || _scanning) return;
            if (missing && !_missing) MarkMissing();
            else if (!missing && _missing) ShowCurrent();
        }
        finally { _checkingExistence = false; }
    }
    private async void SkipMissing_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || !_missing || _busy || _dialog || _scanning) return;
        var file = _current; _busy = true; SetButtons();
        try { bool wasReviewed = _session.IsReviewed(file); await _session.RecordAsync(file, "missing"); if (!wasReviewed) _reviewed++; _removed.Add(file.FullPath); AdvancePending(); }
        catch (Exception ex) { ShowMessage("Atlama kaydedilemedi", ex.Message, InfoBarSeverity.Error); }
        finally { _busy = false; SetButtons(); }
    }
    private void PreviousFile_Click(object sender, RoutedEventArgs e)
    { if (!_busy && !_dialog && !_scanning && _index > 0) { _index--; ShowCurrent(); } }
    private void NextFile_Click(object sender, RoutedEventArgs e)
    { if (!_busy && !_dialog && !_scanning && _index + 1 < _queue.Count) { _index++; ShowCurrent(); } }
    private void UpdateNavigation()
    {
        _neighborCts?.Cancel(); _neighborCts?.Dispose(); _neighborCts = new();
        var previous = _index > 0 ? _queue[_index - 1] : null;
        var next = _index + 1 < _queue.Count ? _queue[_index + 1] : null;
        PreviousName.Text = previous?.Name ?? "—"; PreviousType.Text = previous?.TypeLabel ?? ""; PreviousThumbnail.Source = null;
        NextName.Text = next?.Name ?? "—"; NextType.Text = next?.TypeLabel ?? ""; NextThumbnail.Source = null;
        ToolTipService.SetToolTip(PreviousFileButton, previous?.FullPath ?? "Önceki dosya yok");
        ToolTipService.SetToolTip(NextFileButton, next?.FullPath ?? "Sonraki dosya yok");
        if (previous != null) _ = LoadNeighborAsync(previous, PreviousThumbnail, _neighborCts.Token);
        if (next != null) _ = LoadNeighborAsync(next, NextThumbnail, _neighborCts.Token);
    }
    private async Task LoadNeighborAsync(FileEntry file, Image target, CancellationToken token)
    {
        try
        {
            if (IsMissing(file.FullPath)) return;
            var bytes = await _thumbnails.GetAsync(file, token);
            if (bytes == null && file.Extension is ".psd" or ".psb") bytes = (await Task.Run(() => PreviewContent.Read(file, token), token)).Image;
            if (bytes == null) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(token); }
            stream.Seek(0); var bitmap = new BitmapImage { DecodePixelWidth = 88 }; await bitmap.SetSourceAsync(stream).AsTask(token);
            if (!token.IsCancellationRequested) target.Source = bitmap;
        }
        catch { /* The file type label remains when Windows cannot provide a thumbnail. */ }
    }
    private void LaunchExplorer(string path, bool select = false)
    { try { var info = new ProcessStartInfo("explorer.exe"); if (select) info.Arguments = $"/select,\"{path}\""; else info.ArgumentList.Add(path); Process.Start(info); } catch (Exception ex) { ShowMessage("Konum açılamadı", ex.Message, InfoBarSeverity.Warning); } }
    private async void ResetReview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _scanning || _dialog || string.IsNullOrEmpty(_root)) return;
        if (!await ConfirmAsync("İnceleme kararları sıfırlansın mı?", "Bu klasör ve alt klasörlerinde tutma ve erteleme kararları sıfırlanır. JSON olay geçmişi korunur; silinmiş dosyalar geri gelmez.", "Yeniden başlat")) return;
        try { await _session.ResetAsync(); await LoadFolderAsync(_root); } catch (Exception ex) { ShowMessage("Geçmiş sıfırlanamadı", ex.Message, InfoBarSeverity.Error); }
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_dialog || _busy) return;
        _dialog = true; SetButtons();
        var confirm = new ToggleSwitch { Header = "Silmeden önce sor", IsOn = _confirm, OnContent = "Her silmede onay iste", OffContent = "Kaydırınca hemen uygula" };
        var permanent = new ToggleSwitch { Header = "Kalıcı silme", IsOn = _permanent, OnContent = "Çöp Kutusu’nu atla", OffContent = "Çöp Kutusu’na gönder" };
        var theme = new ComboBox { Header = "Görünüm", ItemsSource = new[] { "Sistem", "Açık", "Koyu" }, SelectedIndex = RequestedTheme == ElementTheme.Light ? 1 : RequestedTheme == ElementTheme.Dark ? 2 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var content = new StackPanel { Spacing = 20, MaxWidth = 440 };
        content.Children.Add(confirm); content.Children.Add(permanent);
        content.Children.Add(new TextBlock { Text = "Kalıcı silme uygulamadan geri alınamaz. Güvenli veri imhası değildir; yedekler veya kurtarma araçlarıyla kurtarılabilir. Uygulama yeniden açıldığında bu mod kapanır.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        content.Children.Add(theme);
        content.Children.Add(new TextBlock { Text = "Fotoğraf, PDF, video/ses ve metin önizlemeleri yereldir. Office dosyaları metin veya gömülü görsel olarak gösterilir. CAD/model önizlemesi Windows’ta kurulu sağlayıcıya bağlıdır.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Ayarlar", Content = content, PrimaryButtonText = "Uygula", CloseButtonText = "Vazgeç", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _confirm = confirm.IsOn; _permanent = permanent.IsOn;
                RequestedTheme = theme.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
                if (XamlRoot.Content is FrameworkElement windowRoot) windowRoot.RequestedTheme = RequestedTheme;
                ModeLabel.Visibility = _permanent ? Visibility.Collapsed : Visibility.Visible;
                PermanentModeLabel.Visibility = _permanent ? Visibility.Visible : Visibility.Collapsed;
                DeleteLabel.Text = _permanent ? "Kalıcı sil" : "Sola · Sil";
                if (_permanent) ShowMessage("Kalıcı silme açık", _confirm ? "Silme kararları Çöp Kutusu’nu atlar." : "Onay kapalı: sola kaydırınca dosya doğrudan kalıcı silinir.", InfoBarSeverity.Warning);
                else MessageBar.IsOpen = false;
            }
        }
        finally { _dialog = false; SetButtons(); }
    }
    private async Task<bool> ConfirmAsync(string title, string text, string action)
    {
        _dialog = true; SetButtons();
        try { return await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 450 }, PrimaryButtonText = action, CloseButtonText = "Vazgeç", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialog = false; SetButtons(); }
    }
    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    { MessageBar.Title = title; MessageBar.Message = message; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
    private async void CompactFilters_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _scanning || _dialog) return;
        _dialog = true;
        var search = new TextBox { Header = "İsim veya konum", Text = SearchBox.Text };
        var type = new ComboBox { Header = "Dosya türü", ItemsSource = TypeFilter.Items.Cast<string>().ToList(), SelectedIndex = TypeFilter.SelectedIndex, HorizontalAlignment = HorizontalAlignment.Stretch };
        var sort = new ComboBox { Header = "Sıralama", ItemsSource = SortBox.Items.Cast<string>().ToList(), SelectedIndex = SortBox.SelectedIndex, HorizontalAlignment = HorizontalAlignment.Stretch };
        var deferred = new ToggleSwitch { Header = "Ertelenenleri göster", IsOn = ShowDeferred.IsOn };
        var panel = new StackPanel { Spacing = 16, Width = 330 }; panel.Children.Add(search); panel.Children.Add(type); panel.Children.Add(sort); panel.Children.Add(deferred);
        try
        {
            if (await new ContentDialog { XamlRoot = XamlRoot, Title = "Filtreler", Content = panel, PrimaryButtonText = "Uygula", CloseButtonText = "Vazgeç" }.ShowAsync() == ContentDialogResult.Primary)
            { SearchBox.Text = search.Text; TypeFilter.SelectedIndex = type.SelectedIndex; SortBox.SelectedIndex = sort.SelectedIndex; ShowDeferred.IsOn = deferred.IsOn; ApplyFilters(); }
        }
        finally { _dialog = false; SetButtons(); }
    }
    private async void CompactSiblings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialog || _current == null) return;
        _dialog = true;
        var list = new ListView { ItemsSource = SiblingList.ItemsSource, ItemTemplate = SiblingList.ItemTemplate, MaxHeight = 400, MinWidth = 350, SelectionMode = ListViewSelectionMode.Single };
        FileEntry? selected = null;
        try { if (await new ContentDialog { XamlRoot = XamlRoot, Title = "Aynı klasörde", Content = list, PrimaryButtonText = "İncele", CloseButtonText = "Kapat" }.ShowAsync() == ContentDialogResult.Primary) selected = list.SelectedItem as FileEntry; }
        finally { _dialog = false; }
        if (selected == null) return;
        if (_session.IsReviewed(selected)) { ShowMessage("Dosya daha önce tutuldu", "Yeniden incelemek için inceleme geçmişini sıfırlayabilirsiniz.", InfoBarSeverity.Informational); return; }
        int existing = _queue.FindIndex(_index, f => f.FullPath == selected.FullPath); if (existing >= _index) _queue.RemoveAt(existing);
        _queue.Insert(_index, selected); ShowCurrent();
    }
    private void Shell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready) return;
        bool wide = e.NewSize.Width >= 1160, medium = e.NewSize.Width >= 900;
        RightColumn.Width = new GridLength(wide ? 282 : 0); Inspector.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = new GridLength(medium ? 205 : 0); Sidebar.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
        Workspace.ColumnSpacing = medium ? 24 : 0; HeaderCaption.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        Shell.Padding = new Thickness(medium ? 28 : 12, 12, medium ? 28 : 12, 16);
    }
    private void Shell_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_busy || _dialog || _scanning || e.OriginalSource is TextBox or ComboBox or Slider or TextBlock { IsTextSelectionEnabled: true }) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (e.Key == VirtualKey.Left) { e.Handled = true; _ = DecideAsync(false); }
        else if (e.Key == VirtualKey.Right) { e.Handled = true; _ = DecideAsync(true); }
        else if (e.Key == VirtualKey.Down) { e.Handled = true; Later_Click(this, new RoutedEventArgs()); }
    }
    private static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBox or Slider or BreadcrumbBar or MediaPlayerElement or TextBlock { IsTextSelectionEnabled: true }) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_busy || _dialog || _current == null || IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (!e.GetCurrentPoint(CardStage).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetCurrentPoint(CardStage).Position.X; _dragging = FileCard.CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Card_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double delta = e.GetCurrentPoint(CardStage).Position.X - _dragStart;
        CardTransform.TranslateX = delta; CardTransform.Rotation = Math.Clamp(delta / 35, -8, 8);
        SwipeBadge.Visibility = Math.Abs(delta) > 24 ? Visibility.Visible : Visibility.Collapsed;
        SwipeLabel.Text = delta < 0 ? (_permanent ? "KALICI SİL" : "SİL") : "TUT";
    }
    private async void Card_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double delta = CardTransform.TranslateX; _dragging = false; FileCard.ReleasePointerCapture(e.Pointer);
        CardTransform.TranslateX = 0; CardTransform.Rotation = 0; SwipeBadge.Visibility = Visibility.Collapsed;
        if (Math.Abs(delta) >= Math.Max(80, FileCard.ActualWidth * .19)) await DecideAsync(delta > 0);
    }
    private void Card_PointerCanceled(object sender, PointerRoutedEventArgs e)
    { if (!_dragging) return; _dragging = false; CardTransform.TranslateX = 0; CardTransform.Rotation = 0; SwipeBadge.Visibility = Visibility.Collapsed; }
}
