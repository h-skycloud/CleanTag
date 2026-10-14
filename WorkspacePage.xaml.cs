using CleanTag.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanTag;

public sealed partial class WorkspacePage : Page, IWorkspacePage
{
    private ReviewPage? _review;
    private LibraryPage? _library;
    private DiskAnalysisPage? _analysis;
    private string? _analysisRoot;
    private bool _loading, _switching;
    private object? _previousSelection;
    public bool CanLeave => !_loading && !_switching && (PageHost.Content as IWorkspacePage)?.CanLeave != false;
    public bool CanClose => !_loading && !_switching && (PageHost.Content as IWorkspacePage)?.CanClose != false;
    public WorkspacePage() => InitializeComponent();
    private async void Page_Loaded(object sender, RoutedEventArgs e) => await InitializeAsync();
    private async void Retry_Click(object sender, RoutedEventArgs e) => await InitializeAsync();
    private async Task InitializeAsync()
    {
        if (_loading || Navigation.Visibility == Visibility.Visible) return;
        _loading = true; StartupProgress.IsActive = true; RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            await AppWorkspace.InitializeAsync();
            StartupPanel.Visibility = Visibility.Collapsed; Navigation.Visibility = Visibility.Visible;
            bool library = Environment.GetCommandLineArgs().Contains("--library");
            Navigation.SelectedItem = Navigation.MenuItems[Environment.GetCommandLineArgs().Contains("--analyze") ? 2 : library ? 1 : 0];
        }
        catch (Exception ex)
        { StartupTitle.Text = "Katalog açılamadı"; StartupMessage.Text = ex.Message; RetryButton.Visibility = Visibility.Visible; }
        finally { StartupProgress.IsActive = false; _loading = false; }
    }
    private async void Navigation_Changed(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_switching || args.SelectedItem == null) return;
        if ((PageHost.Content as IWorkspacePage)?.CanLeave == false)
        { _switching = true; Navigation.SelectedItem = _previousSelection; _switching = false; return; }
        _switching = true;
        try
        {
            if (PageHost.Content is IWorkspacePage oldPage) await oldPage.PrepareToLeaveAsync();
            var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
            if (tag == "library" && _library == null) { _library = new LibraryPage(); _library.AnalyzeRequested += OpenAnalysis; }
            PageHost.Content = tag == "analysis" ? _analysis ??= new DiskAnalysisPage() : tag == "library" ? _library! : _review ??= new ReviewPage();
            _previousSelection = args.SelectedItem;
            if (tag == "analysis" && _analysisRoot != null) { string root = _analysisRoot; _analysisRoot = null; await _analysis!.AnalyzeAsync(root); }
        }
        finally { _switching = false; }
    }
    public async Task PrepareToLeaveAsync()
    {
        await AppWorkspace.Monitor.DisposeAsync();
        if (PageHost.Content is IWorkspacePage page) await page.PrepareToLeaveAsync();
        await AppWorkspace.Catalog.FlushAsync();
    }
    private void OpenAnalysis(string root) { _analysisRoot = root; Navigation.SelectedItem = Navigation.MenuItems[2]; }
}
