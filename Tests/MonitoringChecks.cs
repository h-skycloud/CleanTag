using System.Collections.Concurrent;
using CleanTag.Core;

internal static class MonitoringChecks
{
    public static async Task RunAsync(string fixtureRoot, Func<string, Func<Task>, Task> test)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static async Task Wait(Func<Task<bool>> ready, string message, int milliseconds = 9000)
        {
            var until = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < until) { if (await ready()) return; await Task.Delay(100); }
            throw new Exception(message);
        }
        string root = Path.Combine(fixtureRoot, "monitor-files"), data = Path.Combine(root, "owned-data"); Directory.CreateDirectory(root);
        var store = new CatalogStore(Path.Combine(data, "catalog.sqlite")); await store.InitializeAsync();
        await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default, [data]));
        var source = (await store.GetSourcesAsync()).Single();
        var updates = new ConcurrentQueue<MonitorUpdate>();
        await using var monitor = new CatalogMonitor(store); monitor.Updated += updates.Enqueue;
        await monitor.StartAsync();
        await Wait(() => Task.FromResult(updates.Any(u => u.Message == "Konum güncel")), "Monitor did not reconcile startup");
        string live = Path.Combine(root, "live.txt"); string liveId = "";
        var tag = await store.SaveTagAsync("Live tag");
        await test("Live watcher adds created files and coalesces repeated write notifications", async () =>
        {
            await File.WriteAllTextAsync(live, "first");
            for (int i = 0; i < 5; i++) await File.AppendAllTextAsync(live, " updated");
            await Wait(async () => (await store.SearchAsync(new("live.txt"))).Total == 1, "Created file never appeared");
            var item = (await store.SearchAsync(new("live.txt"))).Files.Single(); liveId = item.Id;
            Require(item.Size == new FileInfo(live).Length, "Intermediate file version was indexed");
        });
        await test("Live watcher preserves tagged kept file identity across external rename and content edit", async () =>
        {
            var item = (await store.SearchAsync(new("live.txt"))).Files.Single(); await store.ChangeTagsAsync([item.Id], [tag.Id], true);
            var session = new CatalogReviewSession(store); await session.LoadAsync(root); await session.RecordAsync(item.Entry, "keep");
            string renamed = Path.Combine(root, "renamed.txt"); File.Move(live, renamed);
            await Wait(async () => (await store.SearchAsync(new("renamed.txt"))).Total == 1, "Rename not observed");
            var moved = (await store.SearchAsync(new("renamed.txt"))).Files.Single();
            await session.LoadAsync(root); Require(moved.Id == liveId && moved.Tags.Single().Id == tag.Id && session.IsReviewed(moved.Entry), "Rename lost state");
            await File.AppendAllTextAsync(renamed, " changed content");
            await Wait(async () => (await store.SearchAsync(new("renamed.txt"))).Files.Single().Size == new FileInfo(renamed).Length, "Edit not observed");
            var edited = (await store.SearchAsync(new("renamed.txt"))).Files.Single(); await session.LoadAsync(root);
            Require(edited.Id == liveId && edited.Tags.Single().Id == tag.Id && !session.IsReviewed(edited.Entry), "Edited version inherited review or lost tags");
            live = renamed;
        });
        await test("Live deletion changes availability without discarding tags; replacement cannot inherit them", async () =>
        {
            File.Move(live, Path.Combine(fixtureRoot, "removed-live.txt"));
            await Wait(async () => (await store.SearchAsync(new("renamed.txt", State: "missing"))).Total == 1, "Removal not observed");
            Require((await store.GetFileTagsAsync(liveId)).Single().Id == tag.Id, "Missing file lost tags");
            await File.WriteAllTextAsync(live, "different object");
            await Wait(async () => (await store.SearchAsync(new("renamed.txt"))).Total == 1, "Replacement not indexed");
            var replacement = (await store.SearchAsync(new("renamed.txt"))).Files.Single(); Require(replacement.Id != liveId && replacement.Tags.Count == 0, "Replacement inherited tags");
        });
        await test("Temporary downloads stay out until renamed to their final name", async () =>
        {
            string temporary = Path.Combine(root, "download.pdf.crdownload"); await File.WriteAllTextAsync(temporary, "finished");
            await Task.Delay(1700); Require((await store.SearchAsync(new("crdownload"))).Total == 0, "Partial download indexed");
            string final = Path.Combine(root, "download.pdf"); File.Move(temporary, final);
            await Wait(async () => (await store.SearchAsync(new("download.pdf"))).Total == 1, "Final download not indexed");
        });
        await test("A long-lived writer is held even through full reconciliation and appears after release", async () =>
        {
            string path = Path.Combine(root, "writing.txt");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await stream.WriteAsync("still writing"u8.ToArray()); await stream.FlushAsync();
                await Task.Delay(1800); monitor.RequestReconcile(source); await Task.Delay(1800);
                Require((await store.SearchAsync(new("writing.txt"))).Total == 0, "Open writer indexed by event or reconciliation");
                await stream.WriteAsync(" done"u8.ToArray()); await stream.FlushAsync();
            }
            await Wait(async () => (await store.SearchAsync(new("writing.txt"))).Total == 1, "Closed writer not indexed");
        });
        await test("Moving a populated directory into a watched root discovers its descendants", async () =>
        {
            string incoming = Path.Combine(fixtureRoot, "incoming-tree"); Directory.CreateDirectory(Path.Combine(incoming, "deep"));
            await File.WriteAllTextAsync(Path.Combine(incoming, "deep", "child.txt"), "subtree");
            Directory.Move(incoming, Path.Combine(root, "project"));
            await Wait(async () => (await store.SearchAsync(new("child.txt"))).Total == 1, "Directory move lost child file");
        });
        await test("Pausing monitoring persists; resume reconciles changes missed while disabled", async () =>
        {
            await monitor.SetEnabledAsync(false); var reopened = new CatalogStore(store.DatabasePath); await reopened.InitializeAsync();
            Require(!await reopened.GetMonitoringEnabledAsync(), "Pause preference not persisted");
            await File.WriteAllTextAsync(Path.Combine(root, "paused.txt"), "pause"); await Task.Delay(1600);
            Require((await store.SearchAsync(new("paused.txt"))).Total == 0, "Disabled monitor still indexed files");
            await monitor.SetEnabledAsync(true);
            await Wait(async () => (await store.SearchAsync(new("paused.txt"))).Total == 1, "Resume failed to reconcile");
        });
        await test("Reconciliation repairs missed changes while own metadata cannot create an event loop", async () =>
        {
            await store.SaveTagAsync("Metadata write"); int before = updates.Count; await Task.Delay(1400);
            Require(updates.Count == before, "Own catalog writes created a watcher feedback loop");
            string path = Path.Combine(root, "repair.txt"); await File.WriteAllTextAsync(path, "repair");
            monitor.RequestReconcile(source);
            await Wait(async () => (await store.SearchAsync(new("repair.txt"))).Total == 1, "Reconciliation missed a new file");
            Require((await store.SearchAsync(new("catalog.sqlite"))).Total == 0, "Own metadata indexed");
        });
        await test("Invalid delta cannot partially mutate catalog; inaccessible roots do not clear availability", async () =>
        {
            int before = (await store.SearchAsync(new())).Total; bool rejected = false;
            try { await store.ApplyChangesAsync(root, [], [Path.Combine(fixtureRoot, "outside.txt")]); } catch (IOException) { rejected = true; }
            Require(rejected && (await store.SearchAsync(new())).Total == before, "Outside-root delta was accepted");
            Require(await store.ApplyChangesAsync(Path.Combine(fixtureRoot, "missing-volume"), [], [Path.Combine(fixtureRoot, "missing-volume", "file")]) == 0, "Unavailable root mutated catalog");
        });
        await test("Direct folder monitoring never discovers descendant files or unregistered folders", async () =>
        {
            string folderRoot = Path.Combine(fixtureRoot, "direct-folder-watch"); Directory.CreateDirectory(folderRoot);
            var folderStore = new CatalogStore(Path.Combine(fixtureRoot, "folder-watch-data", "catalog.sqlite")); await folderStore.InitializeAsync();
            await folderStore.AddSourceAsync(folderRoot); await folderStore.AddFolderAsync(folderRoot);
            var registered = (await folderStore.GetSourcesAsync()).Single(); Require(registered.Content == SourceContent.Folders, "Direct folder acquired recursive scope");
            await using var folders = new CatalogMonitor(folderStore); var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            folders.Updated += u => { if (u.Message == "Konum güncel") initialized.TrySetResult(); }; await folders.StartAsync(); await initialized.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Directory.CreateDirectory(Path.Combine(folderRoot, "unregistered")); await File.WriteAllTextAsync(Path.Combine(folderRoot, "new.txt"), "never import");
            await Task.Delay(1800);
            Require((await folderStore.SearchAsync(new())).Total == 0 && (await folderStore.SearchAsync(new(FoldersOnly: true))).Total == 1, "Direct mode expanded scope");
        });
        await test("Folder-tree monitoring retains folder tags after a watched external rename", async () =>
        {
            string folderRoot = Path.Combine(fixtureRoot, "tree-watch"), child = Path.Combine(folderRoot, "child"); Directory.CreateDirectory(child);
            var folderStore = new CatalogStore(Path.Combine(fixtureRoot, "tree-watch-data", "catalog.sqlite")); await folderStore.InitializeAsync();
            await folderStore.IndexScanAsync(folderRoot, await FileScanner.ScanAsync(folderRoot, null, default, foldersOnly: true), true);
            var item = (await folderStore.SearchAsync(new("child", FoldersOnly: true))).Files.Single(); var folderTag = await folderStore.SaveTagAsync("Folder live"); await folderStore.ChangeTagsAsync([item.Id], [folderTag.Id], true);
            await using var folders = new CatalogMonitor(folderStore); var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            folders.Updated += u => { if (u.Message == "Konum güncel") initialized.TrySetResult(); }; await folders.StartAsync(); await initialized.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Directory.Move(child, Path.Combine(folderRoot, "renamed-folder"));
            await Wait(async () => (await folderStore.SearchAsync(new("renamed-folder", FoldersOnly: true))).Total == 1, "Folder rename not observed");
            var renamed = (await folderStore.SearchAsync(new("renamed-folder", FoldersOnly: true))).Files.Single(); Require(renamed.Id == item.Id && renamed.Tags.Single().Id == folderTag.Id, "Folder watcher lost tag identity");
        });
        await test("Monitoring scope and enabled preference survive backup restore", async () =>
        {
            string backup = Path.Combine(fixtureRoot, "monitor-backup.sqlite"); await store.SetMonitoringEnabledAsync(false); await store.BackupAsync(backup);
            await store.SetMonitoringEnabledAsync(true); await store.RestoreAsync(backup);
            Require(!await store.GetMonitoringEnabledAsync() && (await store.GetSourcesAsync()).Single().Content == SourceContent.Files, "Backup lost monitoring scope or preference");
            await store.SetMonitoringEnabledAsync(true);
        });
    }
}
