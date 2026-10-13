using CleanTag.Core;

internal static class TagManagementChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        var folder = Path.Combine(root, "tag-management"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "example.txt"); await File.WriteAllTextAsync(path, "preserve contents");
        var store = new CatalogStore(Path.Combine(root, "tag-management-data", "catalog.sqlite")); await store.InitializeAsync();
        var info = new FileInfo(path);
        var file = new FileEntry(path, info.Length, info.LastWriteTimeUtc.Ticks);
        await store.IndexScanAsync(folder, new([file], [], 0, false));
        var fileId = (await store.SearchAsync(new())).Files.Single().Id;
        await store.AddFolderAsync(folder);
        var folderId = (await store.SearchAsync(new(FoldersOnly: true))).Files.Single().Id;
        var removed = await store.SaveTagAsync("Delete this"); var kept = await store.SaveTagAsync("Keep this");
        await store.ChangeTagsAsync([fileId, folderId], [removed.Id, kept.Id], true);
        await test("Deleting a catalog tag removes file and folder links while preserving other tags and disk contents", async () =>
        {
            await store.DeleteTagAsync(removed.Id);
            var reopened = new CatalogStore(store.DatabasePath); await reopened.InitializeAsync();
            if ((await reopened.GetTagsAsync()).Any(t => t.Id == removed.Id)) throw new Exception("Deleted tag remains");
            foreach (var id in new[] { fileId, folderId })
                if (!(await reopened.GetFileTagsAsync(id)).Select(t => t.Id).SequenceEqual([kept.Id])) throw new Exception("Unrelated tag lost or link remains");
            if (!Directory.Exists(folder) || await File.ReadAllTextAsync(path) != "preserve contents") throw new Exception("Disk contents changed");
        });
        await test("Unknown tag deletion is rejected and stale undo cannot resurrect a deleted tag", async () =>
        {
            bool failed = false; try { await store.DeleteTagAsync("missing"); } catch (IOException) { failed = true; }
            if (!failed) throw new Exception("Unknown tag accepted");
            var stale = await store.SaveTagAsync("Old undo"); await store.ChangeTagsAsync([fileId], [stale.Id], true);
            var change = await store.ChangeTagsAsync([fileId], [stale.Id], false); await store.DeleteTagAsync(stale.Id); await store.UndoTagsAsync(change);
            if ((await store.GetTagsAsync()).Count != 1 || (await store.GetFileTagsAsync(fileId)).Single().Id != kept.Id) throw new Exception("Deleted tag resurrected or valid tag lost");
        });
    }
}
