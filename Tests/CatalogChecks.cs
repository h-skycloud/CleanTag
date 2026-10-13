using System.Text.Json;
using CleanTag.Core;

internal static class CatalogChecks
{
    public static async Task RunAsync(string fixtureRoot, Func<string, Func<Task>, Task> test)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static FileEntry Entry(string path) { var info = new FileInfo(path); return new(path, info.Length, info.LastWriteTimeUtc.Ticks) { NativeId = FileIdentity.TryRead(path) }; }
        var root = Path.Combine(fixtureRoot, "catalog-files"); var child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
        string pathA = Path.Combine(child, "cephe.pdf"), pathB = Path.Combine(root, "İç Mekân.txt");
        await File.WriteAllTextAsync(pathA, "preview fixture"); await File.WriteAllTextAsync(pathB, "interior fixture");
        var a = Entry(pathA); var b = Entry(pathB);
        var store = new CatalogStore(Path.Combine(fixtureRoot, "catalog-data", "catalog.sqlite"));
        await store.InitializeAsync();
        await test("Catalog persists sources and files; overlapping roots do not duplicate records", async () =>
        {
            await store.IndexScanAsync(root, new([a, b], [], 0, false));
            await store.IndexScanAsync(child, new([a], [], 0, false));
            var reopened = new CatalogStore(store.DatabasePath); await reopened.InitializeAsync();
            Require((await reopened.SearchAsync(new())).Total == 2, "Duplicate files from overlapping roots");
            Require((await reopened.GetSourcesAsync()).Count == 2, "Sources not persisted");
        });
        var aid = (await store.FindFileIdAsync(a))!; var bid = (await store.FindFileIdAsync(b))!;
        var tag = await store.SaveTagAsync("Cephe", "Konu", "facade, dış yüzey", "Mavi");
        await test("Tags and aliases survive reopen; tagging leaves names and content intact", async () =>
        {
            var before = await File.ReadAllBytesAsync(pathA);
            await store.ChangeTagsAsync([aid], [tag.Id], true);
            var reopened = new CatalogStore(store.DatabasePath); await reopened.InitializeAsync();
            Require((await reopened.SearchAsync(new("facade"))).Files.Single().Id == aid, "Alias search failed");
            Require((await reopened.SearchAsync(new(TagId: tag.Id))).Total == 1, "Tag filter failed");
            var after = await File.ReadAllBytesAsync(pathA);
            Require(before.SequenceEqual(after) && File.Exists(pathA), "Tagging modified file");
        });
        await test("Turkish search respects dotted and dotless letters; literal SQL punctuation", async () =>
        {
            Require((await store.SearchAsync(new("iç mekân"))).Files.Single().Id == bid, "Turkish dotted I mismatch");
            Require((await store.SearchAsync(new("' OR 1=1 --"))).Total == 0, "Search was not parameterized");
            var sameTag = await store.SaveTagAsync("CEPHE", "Konu", "facade, dış yüzey", "Mavi"); Require(sameTag.Id == tag.Id, "Case-normalized tag duplicated");
        });
        await test("Batch tag undo changes only actual additions and preserves existing assignments", async () =>
        {
            var change = await store.ChangeTagsAsync([aid, bid], [tag.Id], true);
            Require(change.Added.Count == 1 && change.Added[0].FileId == bid, "Existing assignment included in undo");
            await store.UndoTagsAsync(change);
            Require((await store.GetFileTagsAsync(aid)).Count == 1 && (await store.GetFileTagsAsync(bid)).Count == 0, "Undo removed old assignment");
            var removed = await store.ChangeTagsAsync([aid], [tag.Id], false); await store.UndoTagsAsync(removed);
            Require((await store.GetFileTagsAsync(aid)).Count == 1, "Removal undo failed");
        });
        await test("Invalid bulk operation rolls back without partial tag writes", async () =>
        {
            bool failed = false;
            try { await store.ChangeTagsAsync([bid, "unknown-file"], [tag.Id], true); } catch (IOException) { failed = true; }
            Require(failed && (await store.GetFileTagsAsync(bid)).Count == 0, "Partial bulk mutation persisted");
        });
        await test("SQLite review state and history preserve keep, defer, view, revisit and subtree reset", async () =>
        {
            var session = new CatalogReviewSession(store); await session.LoadAsync(root);
            await session.RecordAsync(a, "keep"); await session.RecordAsync(a, "viewed"); await session.RecordAsync(b, "defer");
            Require((await store.SearchAsync(new(State: "keep"))).Files.Single().Id == aid, "Kept file missing from library");
            await session.LoadAsync(child); await session.ResetAsync(); await session.LoadAsync(root);
            Require(!session.IsReviewed(a) && session.IsDeferred(b), "Reset escaped subtree");
            await session.RecordAsync(a, "keep"); await session.RecordAsync(a, "revisit"); Require(!session.IsReviewed(a), "Revisit failed");
            string export = Path.Combine(fixtureRoot, "history-export.json"); await store.ExportHistoryAsync(export);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(export));
            Require(json.RootElement.GetProperty("Events").GetArrayLength() == 6, "Event history incomplete");
        });
        await test("Cancelled, skipped and inaccessible scans cannot mark existing files missing", async () =>
        {
            await store.IndexScanAsync(root, new([], [], 0, true));
            await store.IndexScanAsync(root, new([], ["access denied"], 1, false));
            await store.IndexScanAsync(root, new([], [], 1, false));
            Require((await store.SearchAsync(new())).Total == 2, "Incomplete scan removed records");
            await store.IndexScanAsync(root, new([a], [], 0, false));
            Require((await store.SearchAsync(new(State: "missing"))).Files.Single().Id == bid, "Complete scan did not mark absence");
            await store.IndexScanAsync(root, new([a, b], [], 0, false));
        });
        await test("Native identity preserves tags and review decisions across external rename", async () =>
        {
            Require(a.NativeId != null, "Windows file ID unavailable on test drive");
            var session = new CatalogReviewSession(store); await session.LoadAsync(root); await session.RecordAsync(a, "keep");
            string renamed = Path.Combine(child, "yeni-cephe.pdf"); File.Move(pathA, renamed);
            var entry = Entry(renamed); await store.IndexScanAsync(root, new([entry, b], [], 0, false));
            Require((await store.FindFileIdAsync(entry)) == aid && (await store.GetFileTagsAsync(aid)).Count == 1, "Rename lost tags");
            await session.LoadAsync(root); Require(session.IsReviewed(entry), "Rename lost keep decision");
            File.Move(renamed, pathA); a = Entry(pathA); await store.IndexScanAsync(root, new([a, b], [], 0, false));
        });
        await test("Replacement at the same path cannot inherit an old file's tags", async () =>
        {
            string path = Path.Combine(root, "replacement.txt"); await File.WriteAllTextAsync(path, "old");
            var old = Entry(path); await store.IndexScanAsync(root, new([a, b, old], [], 0, false));
            string oldId = (await store.FindFileIdAsync(old))!; await store.ChangeTagsAsync([oldId], [tag.Id], true);
            string moved = Path.Combine(fixtureRoot, "replacement-old.txt"); File.Move(path, moved);
            await File.WriteAllTextAsync(path, "unrelated-new"); var replacement = Entry(path);
            await store.IndexScanAsync(root, new([a, b, replacement], [], 0, false));
            string newId = (await store.FindFileIdAsync(replacement))!;
            Require(newId != oldId && (await store.GetFileTagsAsync(newId)).Count == 0, "Replacement inherited metadata");
        });
        await test("Identical copies have separate identities and tag assignments", async () =>
        {
            string copy = Path.Combine(root, "copy.pdf"); File.Copy(pathA, copy); var copied = Entry(copy);
            await store.IndexScanAsync(root, new([a, b, copied], [], 0, false));
            var id = await store.FindFileIdAsync(copied); Require(id != aid && (await store.GetFileTagsAsync(id!)).Count == 0, "Copies merged");
        });
        await test("Pagination has stable results and supports metadata/type/status filters", async () =>
        {
            await store.IndexScanAsync(root, new([a, b], [], 0, false));
            var first = await store.SearchAsync(new(Limit: 1)); var second = await store.SearchAsync(new(Offset: 1, Limit: 1));
            Require(first.Total == 2 && second.Total == 2 && first.Files.Single().Id != second.Files.Single().Id, "Pagination duplicated result");
            Require((await store.SearchAsync(new(Category: "Belgeler"))).Total == 2, "Category filter failed");
            Require((await store.SearchAsync(new(State: "untagged"))).Total >= 1, "Untagged filter failed");
        });
        await test("Online backup and restore preserve metadata and create a recovery backup", async () =>
        {
            string backup = Path.Combine(fixtureRoot, "backup.sqlite"); await store.BackupAsync(backup);
            await store.ChangeTagsAsync([aid], [tag.Id], false); Require((await store.GetFileTagsAsync(aid)).Count == 0, "Remove failed");
            await store.RestoreAsync(backup); Require((await store.GetFileTagsAsync(aid)).Count == 1, "Restore lost assignment");
            Require(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "backups"), "before-restore-*.sqlite").Length == 1, "Recovery backup missing");
            string export = Path.Combine(fixtureRoot, "catalog-export.json"); await store.ExportCatalogAsync(export);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(export)); Require(json.RootElement.GetProperty("Tables").GetProperty("FileTags").GetArrayLength() > 0, "Metadata export incomplete");
        });
        await test("Legacy JSON migration is idempotent and preserves ordered reset semantics", async () =>
        {
            string data = Path.Combine(fixtureRoot, "migration"); Directory.CreateDirectory(data);
            string legacy = Path.Combine(data, "decisions.json");
            var events = new[] { new ReviewDecision(root, a.Identity, a.FullPath, "keep", a.Size, DateTime.UtcNow),
                new ReviewDecision(root, b.Identity, b.FullPath, "defer", b.Size, DateTime.UtcNow), new ReviewDecision(child, "", "", "reset", 0, DateTime.UtcNow) };
            await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new { Version = 1, Events = events }));
            string before = await File.ReadAllTextAsync(legacy);
            var migrated = new CatalogStore(Path.Combine(data, "catalog.sqlite")); await migrated.InitializeAsync(legacy);
            var reopened = new CatalogStore(migrated.DatabasePath); await reopened.InitializeAsync(legacy);
            var session = new CatalogReviewSession(reopened); await session.LoadAsync(root);
            Require(!session.IsReviewed(a) && session.IsDeferred(b), "Migration replay wrong");
            Require(await File.ReadAllTextAsync(legacy) == before, "Legacy file modified");
            string export = Path.Combine(data, "export.json"); await reopened.ExportHistoryAsync(export);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(export)); Require(json.RootElement.GetProperty("Events").GetArrayLength() == 3, "Reimport duplicated events");
        });
        await test("Corrupt legacy history is rejected without publishing partial migration", async () =>
        {
            string folder = Path.Combine(fixtureRoot, "corrupt-migration"); Directory.CreateDirectory(folder);
            string legacy = Path.Combine(folder, "decisions.json"); await File.WriteAllTextAsync(legacy, "{\"Version\":1,\"Events\":[");
            var corrupt = new CatalogStore(Path.Combine(folder, "catalog.sqlite")); bool rejected = false;
            try { await corrupt.InitializeAsync(legacy); } catch (JsonException) { rejected = true; }
            Require(rejected, "Corrupt history silently ignored");
            await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new { Version = 1, Events = Array.Empty<ReviewDecision>() }));
            await corrupt.InitializeAsync(legacy); Require((await corrupt.SearchAsync(new())).Total == 0, "Partial migration leaked");
        });
        await test("Concurrent catalog writers retain independent tag assignments", async () =>
        {
            var other = new CatalogStore(store.DatabasePath); await other.InitializeAsync();
            var two = await store.SaveTagAsync("Test concurrent");
            await Task.WhenAll(store.ChangeTagsAsync([aid], [two.Id], true), other.ChangeTagsAsync([bid], [two.Id], true));
            Require((await store.SearchAsync(new(TagId: two.Id))).Total == 2, "Concurrent writer lost assignment");
        });
        await test("Scanner excludes application metadata without declaring the scan incomplete", async () =>
        {
            string scanRoot = Path.Combine(fixtureRoot, "scan-exclusions"); string data = Path.Combine(scanRoot, "app-data");
            Directory.CreateDirectory(data); await File.WriteAllTextAsync(Path.Combine(data, "catalog.sqlite"), "owned metadata");
            await File.WriteAllTextAsync(Path.Combine(scanRoot, "visible.txt"), "visible");
            var result = await FileScanner.ScanAsync(scanRoot, null, default, [data]);
            Require(result.Files.Count == 1 && result.Files[0].Name == "visible.txt" && result.Skipped == 0, "Metadata was scanned or exclusion counted as access failure");
        });
        await test("Content edits retain native file tags but require a new review decision", async () =>
        {
            var session = new CatalogReviewSession(store); await session.LoadAsync(root); await session.RecordAsync(a, "keep");
            await File.AppendAllTextAsync(pathA, " updated"); var updated = Entry(pathA);
            await store.IndexScanAsync(root, new([updated, b], [], 0, false));
            Require((await store.FindFileIdAsync(updated)) == aid && (await store.GetFileTagsAsync(aid)).Count > 0, "Content edit lost tags");
            await session.LoadAsync(root); Require(!session.IsReviewed(updated), "Changed content remained reviewed");
        });
        await test("SQLite deletion history keeps creation metadata observed before removal", async () =>
        {
            string path = Path.Combine(root, "owned-deletion.txt"); await File.WriteAllTextAsync(path, "owned"); var entry = Entry(path);
            var session = new CatalogReviewSession(store); await session.LoadAsync(root); await session.RecordAsync(entry, "viewed");
            File.Delete(path); await session.RecordAsync(entry, "permanent");
            Require(session.GetDecision(entry)?.CreatedUtc != null, "Creation metadata lost after deletion");
        });
        await test("Same-size same-time replacement resets review without inheriting tags", async () =>
        {
            string path = Path.Combine(root, "same-metadata.txt"); await File.WriteAllTextAsync(path, "aaa"); var original = Entry(path);
            await store.IndexScanAsync(root, new([b, original], [], 0, false));
            var session = new CatalogReviewSession(store); await session.LoadAsync(root); await session.RecordAsync(original, "keep");
            string oldId = (await store.FindFileIdAsync(original))!; await store.ChangeTagsAsync([oldId], [tag.Id], true);
            File.Move(path, Path.Combine(fixtureRoot, "same-metadata-old.txt")); await File.WriteAllTextAsync(path, "bbb");
            File.SetLastWriteTimeUtc(path, new DateTime(original.ModifiedTicks, DateTimeKind.Utc)); var replacement = Entry(path);
            Require(replacement.Identity == original.Identity && replacement.NativeId != original.NativeId, "Test did not create metadata collision");
            await store.IndexScanAsync(root, new([b, replacement], [], 0, false)); await session.LoadAsync(root);
            Require(!session.IsReviewed(replacement) && (await store.GetFileTagsAsync((await store.FindFileIdAsync(replacement))!)).Count == 0, "Replacement inherited state");
        });
    }
}
