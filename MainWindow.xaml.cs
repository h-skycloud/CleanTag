using Microsoft.UI.Xaml;
using CleanTag.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CleanTag;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _allowClose, _waitingToClose;
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate the root frame to the main page on startup.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + Math.Max(0, (area.Width - 1440) / 2), area.Y + Math.Max(0, (area.Height - 960) / 2)));
        RootFrame.Navigate(typeof(WorkspacePage));
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (_waitingToClose || RootFrame.Content is IWorkspacePage { CanClose: false }) return;
            _waitingToClose = true;
            try { if (RootFrame.Content is IWorkspacePage page) await page.PrepareToLeaveAsync(); _allowClose = true; Close(); }
            finally { _waitingToClose = false; }
        };
    }
}
