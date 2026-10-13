using CleanTag.Core;

namespace CleanTag.Services;

public static class AppWorkspace
{
    public const string AppName = "CleanTag";
    public const string LegacyAppName = "LookClean";

    public static string StateDirectory { get; } = ResolveStateDirectory();
    public static CatalogStore Catalog { get; } = new(Path.Combine(StateDirectory, "catalog.sqlite"));
    public static CatalogMonitor Monitor { get; } = new(Catalog, [StateDirectory]);
    public static async Task InitializeAsync()
    {
        if (!Environment.GetCommandLineArgs().Contains("--data-dir"))
            await LegacyDataMigration.MigrateAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyAppName), StateDirectory);
        await Catalog.InitializeAsync(Path.Combine(StateDirectory, "decisions.json"));
        await Monitor.StartAsync();
    }

    private static string ResolveStateDirectory()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && args.Length > index + 1)
            return Path.GetFullPath(args[index + 1]);

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    }
}

public interface IWorkspacePage
{
    bool CanLeave { get; }
    bool CanClose => CanLeave;
    Task PrepareToLeaveAsync();
}
