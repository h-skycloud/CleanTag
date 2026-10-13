using CleanTag.Core;

internal static class FolderScanChecks
{
    public static async Task RunAsync(string fixtureRoot, Func<string, Func<Task>, Task> test)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        string root = Path.Combine(fixtureRoot, "folder-mode"), child = Path.Combine(root, "Proje.zip"), nested = Path.Combine(child, "Çizimler");
        Directory.CreateDirectory(nested);
        string file = Path.Combine(child, "plan.txt"); await File.WriteAllTextAsync(file, "aaa");
        var store = new CatalogStore(Path.Combine(fixtureRoot, "folder-mode-data", "catalog.sqlite")); await store.InitializeAsync();
        var source = await store.AddSourceAsync(root);
        string folderId = "";
        var tag = await store.SaveTagAsync("Mimari", aliases: "architecture");
        await test("Folder registration is direct; source roots and dotted folder names are taggable", async () =>
        {
            await store.AddFolderAsync(root); await store.AddFolderAsync(child);
            var folders = await store.SearchAsync(new(SourceId: source.Id, FoldersOnly: true));
            Require(folders.Total == 2 && (await store.SearchAsync(new())).Total == 0, "Direct registration indexed descendants or omitted source root");
            var item = folders.Files.Single(f => f.Path == child); folderId = item.Id;
            Require(item.IsFolder && item.TypeLabel == "Klasör" && item.NativeId != null, "Folder treated as ZIP file or has no identity");
        });
        await test("File and folder tags are separate; folder tagging never propagates to descendants", async () =>
        {
            await store.ChangeTagsAsync([folderId], [tag.Id], true);
            var scan = await FileScanner.ScanAsync(root, null, default); await store.IndexScanAsync(root, scan);
            Require((await store.SearchAsync(new(TagId: tag.Id))).Total == 0, "Folder tag propagated to a file");
            Require((await store.SearchAsync(new("architecture", FoldersOnly: true))).Files.Single().Id == folderId, "Folder alias search failed");
            Require((await store.SearchAsync(new(FoldersOnly: true))).Total == 2, "File scan invalidated registered folders");
        });
        await test("Refreshing a folder preserves its tags after its contents change", async () =>
        {
            await File.WriteAllTextAsync(Path.Combine(child, "new.txt"), "child change");
            Directory.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddMinutes(-2));
            await store.RefreshFoldersAsync();
            var item = (await store.SearchAsync(new(FoldersOnly: true))).Files.Single(f => f.Path == child);
            Require(item.Id == folderId && item.Tags.Single().Id == tag.Id, "Content change reset folder tags");
        });
        await test("Folder discovery returns only directories and cannot invalidate file availability", async () =>
        {
            var folders = await FileScanner.ScanAsync(root, null, default, foldersOnly: true);
            Require(folders.Files.Count == 3 && folders.Files.All(f => f.IsDirectory && f.Size == 0), "Folder discovery traversed file metadata");
            await store.IndexScanAsync(root, folders, true);
            Require((await store.SearchAsync(new())).Total == 1 && (await store.SearchAsync(new(FoldersOnly: true))).Total == 3, "Folder scan invalidated files");
        });
        await test("External folder rename retains folder tags; the old path cannot mark the relocated item missing", async () =>
        {
            string renamed = Path.Combine(root, "Yeni proje"); Directory.Move(child, renamed);
            await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default, foldersOnly: true), true);
            var item = (await store.SearchAsync(new(TagId: tag.Id, FoldersOnly: true))).Files.Single();
            Require(item.Id == folderId && item.Path == renamed && item.Available, "Folder rename lost identity or availability");
            Directory.Move(renamed, child);
            await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default, foldersOnly: true), true);
        });
        await test("Fast scan captures native identity lazily and preserves tagged file renames and edits", async () =>
        {
            await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default));
            var original = (await store.SearchAsync(new("plan.txt"))).Files.Single();
            Require(original.NativeId == null, "Untracked file was opened during inventory");
            await store.ChangeTagsAsync([original.Id], [tag.Id], true);
            Require((await store.SearchAsync(new("plan.txt"))).Files.Single().NativeId != null, "Tagging did not capture identity");
            string renamed = Path.Combine(child, "renamed.txt"); File.Move(file, renamed);
            await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default));
            var relocated = (await store.SearchAsync(new(TagId: tag.Id))).Files.Single();
            Require(relocated.Id == original.Id && relocated.Path == renamed && relocated.Available, "Fast scan lost rename metadata");
            await File.AppendAllTextAsync(renamed, " edited"); await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default));
            Require((await store.SearchAsync(new(TagId: tag.Id))).Files.Single().Id == original.Id, "Fast scan lost tags on content edit");
        });
        await test("Fast scan rejects same-size same-time replacement of tagged and kept files", async () =>
        {
            string path = Path.Combine(root, "protected.txt"); await File.WriteAllTextAsync(path, "old");
            var scan = await FileScanner.ScanAsync(root, null, default); await store.IndexScanAsync(root, scan);
            var entry = scan.Files.Single(f => f.FullPath == path);
            var session = new CatalogReviewSession(store); await session.LoadAsync(root); await session.RecordAsync(entry, "keep");
            string id = (await store.FindFileIdAsync(entry))!; await store.ChangeTagsAsync([id], [tag.Id], true);
            File.Move(path, Path.Combine(fixtureRoot, "protected-old.txt")); await File.WriteAllTextAsync(path, "new");
            File.SetLastWriteTimeUtc(path, new DateTime(entry.ModifiedTicks, DateTimeKind.Utc));
            await store.IndexScanAsync(root, await FileScanner.ScanAsync(root, null, default)); await session.LoadAsync(root);
            var replacement = (await store.SearchAsync(new("protected.txt"))).Files.Single();
            Require(replacement.Id != id && replacement.Tags.Count == 0 && !session.IsReviewed(replacement.Entry), "Replacement inherited metadata");
        });
        await test("Scan batches appear before completion; cancellation cannot invalidate unobserved files", async () =>
        {
            string many = Path.Combine(fixtureRoot, "batch-scan"); Directory.CreateDirectory(many);
            for (int i = 0; i < 1200; i++) await File.WriteAllTextAsync(Path.Combine(many, $"file-{i:D4}.txt"), "x");
            var all = await FileScanner.ScanAsync(many, null, default); await store.IndexScanAsync(many, all);
            var cts = new CancellationTokenSource(); int batches = 0;
            var cancelled = await FileScanner.ScanAsync(many, null, cts.Token, onBatch: async batch =>
            {
                batches++; Require(batch.Count == 512, "First batch was not bounded");
                await store.IndexScanAsync(many, new(batch.ToList(), [], 0, true)); cts.Cancel();
            });
            await store.IndexScanAsync(many, cancelled);
            Require(cancelled.Cancelled && cancelled.Files.Count == 512 && batches == 1, "Cancellation or progressive callback failed");
            var manySource = (await store.GetSourcesAsync()).Single(s => s.Path == many);
            Require((await store.SearchAsync(new(SourceId: manySource.Id))).Total == 1200, "Partial scan invalidated unobserved files");
            bool rejected = false;
            try { await FileScanner.ScanAsync(many, null, default, onBatch: _ => throw new IOException("database failure")); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Indexing failure was swallowed as a directory access failure");
        });
        await test("Unsafe directory attributes and excluded roots are respected by optimized enumeration", async () =>
        {
            string scanRoot = Path.Combine(fixtureRoot, "unsafe-scan"), unsafeFolder = Path.Combine(scanRoot, "system-child"); Directory.CreateDirectory(unsafeFolder);
            await File.WriteAllTextAsync(Path.Combine(unsafeFolder, "skip.txt"), "x");
            File.SetAttributes(unsafeFolder, File.GetAttributes(unsafeFolder) | FileAttributes.System);
            try
            {
                var result = await FileScanner.ScanAsync(scanRoot, null, default);
                Require(result.Files.Count == 0 && result.Skipped == 1, "System directory was descended into");
                var excluded = await FileScanner.ScanAsync(scanRoot, null, default, [scanRoot]);
                Require(excluded.Files.Count == 0 && excluded.Skipped == 0, "Excluded root was scanned");
            }
            finally { File.SetAttributes(unsafeFolder, FileAttributes.Directory); }
        });
        await test("Folder tag undo and backup restore retain entity kinds and existing assignments", async () =>
        {
            var extra = await store.SaveTagAsync("Folder undo"); var change = await store.ChangeTagsAsync([folderId], [tag.Id, extra.Id], true);
            await store.UndoTagsAsync(change); Require((await store.GetFileTagsAsync(folderId)).Single().Id == tag.Id, "Undo removed an existing folder tag");
            string backup = Path.Combine(fixtureRoot, "folder-backup.sqlite"); await store.BackupAsync(backup);
            await store.ChangeTagsAsync([folderId], [tag.Id], false); await store.RestoreAsync(backup);
            var item = (await store.SearchAsync(new(TagId: tag.Id, FoldersOnly: true))).Files.Single();
            Require(item.Id == folderId && item.IsFolder, "Backup lost folder identity or assignment");
        });
    }
}
