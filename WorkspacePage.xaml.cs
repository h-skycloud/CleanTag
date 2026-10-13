using CleanTag.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanTag;

public sealed partial class WorkspacePage : Page, IWorkspacePage
{
    private ReviewPage? _review;
    private LibraryPage? _library;
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
            Navigation.SelectedItem = Navigation.MenuItems[library ? 1 : 0];
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
            PageHost.Content = tag == "library" ? _library ??= new LibraryPage() : _review ??= new ReviewPage();
            _previousSelection = args.SelectedItem;
        }
        finally { _switching = false; }
    }
    public async Task PrepareToLeaveAsync()
    {
        await AppWorkspace.Monitor.DisposeAsync();
        if (PageHost.Content is IWorkspacePage page) await page.PrepareToLeaveAsync();
        await AppWorkspace.Catalog.FlushAsync();
    }
}
