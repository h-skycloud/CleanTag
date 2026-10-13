using CleanTag.Core;
using CleanTag.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CleanTag.Controls;

public sealed partial class FilePreview : UserControl
{
    private readonly ThumbnailCache _thumbnails = new();
    private readonly SemaphoreSlim _pdfGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private PdfDocument? _pdf;
    private IRandomAccessStream? _pdfStream;
    private MediaPlayer? _media;
    private uint _pdfPage;
    private int _version;
    private bool _rendering;

    public FilePreview() => InitializeComponent();
    private void Control_Unloaded(object sender, RoutedEventArgs e) => Clear();
    public void ShowFallback(string title, string message)
    { Clear(); FallbackTitle.Text = title; FallbackText.Text = message; }

    public void Clear()
    {
        _cts?.Cancel(); _cts?.Dispose(); _cts = null; _version++;
        MediaPreview.SetMediaPlayer(null); _media?.Dispose(); _media = null;
        _pdf = null;
        var stream = _pdfStream; _pdfStream = null;
        if (stream != null) _ = DisposePdfStreamAsync(stream);
        PreviewImage.Source = null; TextPreview.Text = "";
        PreviewImage.Visibility = TextPreviewHost.Visibility = MediaPreview.Visibility = PdfControls.Visibility = Visibility.Collapsed;
        PreviewFallback.Visibility = Visibility.Visible; PreviewLoading.IsActive = false;
        FallbackTitle.Text = "Önizleme"; FallbackText.Text = "Bir öğe seçin."; PreviewStatus.Text = "";
    }

    private async Task DisposePdfStreamAsync(IRandomAccessStream stream)
    {
        await _pdfGate.WaitAsync();
        try { stream.Dispose(); }
        finally { _pdfGate.Release(); }
    }

    public async Task ReleaseAsync()
    {
        Clear(); await _pdfGate.WaitAsync(); _pdfGate.Release();
    }

    public async Task ShowAsync(FileEntry file, CancellationToken cancellationToken = default)
    {
        Clear(); _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token; int version = _version;
        bool Current() => version == _version && !token.IsCancellationRequested;
        FallbackTitle.Text = file.TypeLabel; FallbackText.Text = "Önizleme hazırlanıyor…";
        PreviewStatus.Text = "Önizleme hazırlanıyor"; PreviewLoading.IsActive = true;
        try
        {
            if (file.IsDirectory)
            {
                var text = await Task.Run(() =>
                {
                    if (!Directory.Exists(file.FullPath)) throw new FileNotFoundException("Klasör bu konumda bulunamadı.");
                    FileSafety.CheckAncestors(file.FullPath);
                    var lines = new List<string>();
                    foreach (var entry in new DirectoryInfo(file.FullPath).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        if (lines.Count == 24) { lines.Add("… Daha fazlası için klasörü açın."); break; }
                        lines.Add($"{(entry is DirectoryInfo ? "📁" : "·")} {entry.Name}");
                    }
                    return lines.Count == 0 ? "Bu klasör boş." : string.Join(Environment.NewLine, lines);
                }, token);
                if (!Current()) return;
                TextPreview.Text = text; TextPreviewHost.Visibility = Visibility.Visible; PreviewFallback.Visibility = Visibility.Collapsed;
                PreviewStatus.Text = "Klasör içeriği · İlk 24 öğe · Alt klasörler taranmaz";
                return;
            }
            if (!File.Exists(file.FullPath)) throw new FileNotFoundException("Dosya bu konumda bulunamadı.");
            if (file.Category is "Videolar" or "Sesler")
            {
                var storage = await StorageFile.GetFileFromPathAsync(file.FullPath).AsTask(token);
                if (!Current()) return;
                var player = new MediaPlayer { AutoPlay = false };
                player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
                { if (Current()) { MediaPreview.Visibility = Visibility.Collapsed; PreviewFallback.Visibility = Visibility.Visible; FallbackText.Text = "Bu medya için codec bulunamadı. Kendi uygulamasında açabilirsiniz."; } });
                player.Source = MediaSource.CreateFromStorageFile(storage); _media = player;
                MediaPreview.SetMediaPlayer(player); MediaPreview.Visibility = Visibility.Visible; PreviewFallback.Visibility = Visibility.Collapsed;
                PreviewStatus.Text = "Yerel oynatıcı · Oynatmak için ▶"; return;
            }
            if (file.Extension == ".pdf")
            {
                var storage = await StorageFile.GetFileFromPathAsync(file.FullPath).AsTask(token);
                var stream = await storage.OpenReadAsync().AsTask(token);
                bool owned = false;
                try
                {
                    var document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(token);
                    if (!Current()) return;
                    _pdf = document; _pdfStream = stream; _pdfPage = 0; owned = true;
                    await RenderPdfAsync(version, token); return;
                }
                finally { if (!owned) stream.Dispose(); }
            }
            if (file.Category == "Görseller" && file.Extension != ".svg")
            {
                try
                {
                    var storage = await StorageFile.GetFileFromPathAsync(file.FullPath).AsTask(token);
                    using var stream = await storage.OpenReadAsync().AsTask(token);
                    var bitmap = new BitmapImage { DecodePixelWidth = 1500 };
                    await bitmap.SetSourceAsync(stream).AsTask(token);
                    if (Current()) SetImage(bitmap, "Görsel önizleme");
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            var content = await Task.Run(() => PreviewContent.Read(file, token), token);
            if (!Current()) return;
            if (content.Image != null)
            {
                await ShowImageAsync(content.Image, version, token, content.ImageType == "svg");
                if (Current()) PreviewStatus.Text = content.ImageType == "svg" ? "Vektör önizleme · Dış kaynaklar kapalı" : "Gömülü önizleme";
                return;
            }
            if (!string.IsNullOrWhiteSpace(content.Text) && file.Extension is not ".psd" and not ".psb")
            {
                TextPreview.Text = content.Text; TextPreviewHost.Visibility = Visibility.Visible; PreviewFallback.Visibility = Visibility.Collapsed;
                PreviewStatus.Text = file.Category == "Arşivler" ? "Arşiv içeriği · Dosyalar çıkartılmadı" : "Metin önizlemesi · Özgün sayfa düzeni korunmaz";
                return;
            }
            var thumbnail = await _thumbnails.GetAsync(file, token);
            if (!Current()) return;
            if (thumbnail != null) { await ShowImageAsync(thumbnail, version, token); if (Current()) PreviewStatus.Text = "Windows / gömülü küçük resim"; }
            else { FallbackText.Text = "Bu biçim için yerel önizleme bulunamadı. Dosyayı kendi uygulamasında açabilirsiniz."; PreviewStatus.Text = "Önizleme sağlayıcısı yok"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Current()) { FallbackText.Text = ex is FileNotFoundException ? ex.Message : "Önizleme açılamadı. Kendi uygulamasında açabilirsiniz."; PreviewStatus.Text = ex.Message; }
        }
        finally { if (Current()) PreviewLoading.IsActive = false; }
    }

    private void SetImage(ImageSource image, string status)
    { PreviewImage.Source = image; PreviewImage.Visibility = Visibility.Visible; PreviewFallback.Visibility = Visibility.Collapsed; PreviewStatus.Text = status; }

    private async Task ShowImageAsync(byte[] bytes, int version, CancellationToken token, bool svg = false)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(token); }
        stream.Seek(0);
        if (svg)
        {
            var image = new SvgImageSource { RasterizePixelWidth = 1500 };
            if (await image.SetSourceAsync(stream).AsTask(token) != SvgImageSourceLoadStatus.Success) throw new IOException("SVG görüntülenemedi.");
            if (version == _version && !token.IsCancellationRequested) SetImage(image, "Vektör önizleme");
        }
        else
        {
            var image = new BitmapImage { DecodePixelWidth = 1500 }; await image.SetSourceAsync(stream).AsTask(token);
            if (version == _version && !token.IsCancellationRequested) SetImage(image, "Önizleme");
        }
    }

    private async Task RenderPdfAsync(int version, CancellationToken token)
    {
        var document = _pdf; uint index = _pdfPage;
        if (document == null) return;
        await _pdfGate.WaitAsync(token); _rendering = true;
        try
        {
            if (version != _version || token.IsCancellationRequested) return;
            using var page = document.GetPage(index); using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1500 }).AsTask(token);
            stream.Seek(0); var image = new BitmapImage(); await image.SetSourceAsync(stream).AsTask(token);
            if (version != _version || token.IsCancellationRequested) return;
            SetImage(image, "PDF · Sayfa önizlemesi"); PdfControls.Visibility = Visibility.Visible; PageLabel.Text = $"{index + 1} / {document.PageCount}";
        }
        finally { _rendering = false; _pdfGate.Release(); }
    }
    private async void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_pdfPage > 0 && !_rendering) { _pdfPage--; await ChangePageAsync(); } }
    private async void NextPage_Click(object sender, RoutedEventArgs e) { if (_pdf != null && _pdfPage + 1 < _pdf.PageCount && !_rendering) { _pdfPage++; await ChangePageAsync(); } }
    private async Task ChangePageAsync()
    {
        try { await RenderPdfAsync(_version, _cts?.Token ?? default); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewStatus.Text = ex.Message; }
    }
}
