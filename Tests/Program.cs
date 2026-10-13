using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CleanTag.Core;
using CleanTag.Services;

if (args.Contains("--benchmark")) { await ScanBenchmarks.RunAsync(); return 0; }

var testBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data"));
Directory.CreateDirectory(testBase);
var root = Path.Combine(testBase, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<object>();
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool value, string message) { if (!value) throw new Exception(message); }
FileEntry Entry(string path) { var f = new FileInfo(path); return new(path, f.Length, f.LastWriteTimeUtc.Ticks); }
async Task MustFail(Func<Task> action) { bool failed = false; try { await action(); } catch (IOException) { failed = true; } Require(failed, "An unsafe operation was not rejected"); }
var sub = Path.Combine(root, "İç klasör [1]"); Directory.CreateDirectory(sub);
var original = Path.Combine(sub, "görüş [0].txt"); await File.WriteAllTextAsync(original, "Merhaba dünya");
await Test("Drive roots accepted; directory itself is never a file target", async () =>
{
    Require(FileSafety.IsWithin(Path.GetPathRoot(original)!, original), "Drive root not accepted");
    Require(!FileSafety.IsWithin(root, root), "Root directory accepted as a file"); await Task.CompletedTask;
});
await Test("Unicode and literal bracket paths; recursive inventory", async () =>
{
    var scan = await FileScanner.ScanAsync(root, null, default);
    Require(scan.Files.Count == 1 && scan.Files[0].FullPath == original && scan.Errors.Count == 0, "Inventory mismatch");
});
await Test("Cancellation returns a partial result", async () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    var scan = await FileScanner.ScanAsync(root, null, cts.Token); Require(scan.Cancelled && scan.Files.Count == 0, "Cancellation ignored");
});
await Test("Reject outside-root and sibling-prefix deletion", async () =>
{
    var other = Path.Combine(testBase, Path.GetFileName(root) + "-outside.txt"); await File.WriteAllTextAsync(other, "keep");
    await MustFail(() => DeletionService.DeleteAsync(root, Entry(other), true, 0));
    Require(File.Exists(other), "Outside file deleted"); File.Delete(other);
});
await Test("Reject changed file before deletion", async () =>
{
    var entry = Entry(original); await File.AppendAllTextAsync(original, " updated");
    await MustFail(() => DeletionService.DeleteAsync(root, entry, true, 0)); Require(File.Exists(original), "Changed file deleted");
});
await Test("Persist keep, revisit, and root-specific reset", async () =>
{
    var ledger = Path.Combine(root, "decisions.jsonl"); var entry = Entry(original);
    var first = new ReviewSession(ledger); await first.LoadAsync(root); await first.RecordAsync(entry, "keep");
    var second = new ReviewSession(ledger); await second.LoadAsync(root); Require(second.IsReviewed(entry), "Keep not persisted");
    await second.RecordAsync(entry, "revisit"); Require(!second.IsReviewed(entry), "Revisit failed");
    await second.RecordAsync(entry, "keep"); await second.ResetAsync();
    await first.LoadAsync(root); Require(!first.IsReviewed(entry), "Reset not persisted");
});
await Test("Text preview is bounded", async () =>
{
    var path = Path.Combine(root, "large.txt"); await File.WriteAllTextAsync(path, new string('a', 100_000));
    var preview = PreviewContent.Read(Entry(path), default); Require(preview.Text.Length < 65000 && preview.Text.Contains("64.000"), "Text was not bounded");
});
await Test("ZIP lists content without extracting paths", async () =>
{
    var path = Path.Combine(root, "archive.zip");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("test"); }
    var preview = PreviewContent.Read(Entry(path), default); Require(preview.Text.Contains("../escape.txt") && !File.Exists(Path.Combine(testBase, "escape.txt")), "Archive extraction occurred");
    await Task.CompletedTask;
});
await Test("DOCX text preview", async () =>
{
    var path = Path.Combine(root, "document.docx");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()); writer.Write("<w:document xmlns:w=\"urn:test\"><w:p><w:t>CleanTag belge testi</w:t></w:p></w:document>"); }
    Require(PreviewContent.Read(Entry(path), default).Text.Contains("CleanTag belge testi"), "Document text missing"); await Task.CompletedTask;
});
await Test("XML external entities rejected", async () =>
{
    var path = Path.Combine(root, "external.docx");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()); writer.Write("<!DOCTYPE x [<!ENTITY test SYSTEM 'file:///C:/Windows/win.ini'>]><x>&test;</x>"); }
    bool blocked = false; try { PreviewContent.Read(Entry(path), default); } catch (System.Xml.XmlException) { blocked = true; }
    Require(blocked, "External entity not rejected"); await Task.CompletedTask;
});
await Test("Permanent deletion affects only selected fixture", async () =>
{
    var path = Path.Combine(root, "permanent-test.txt"); await File.WriteAllTextAsync(path, "owned test fixture");
    await DeletionService.DeleteAsync(root, Entry(path), true, 0); Require(!File.Exists(path) && File.Exists(original), "Permanent delete scope mismatch");
});
await Test("Native recycle succeeds and file appears in Windows Recycle Bin", async () =>
{
    var path = Path.Combine(root, "CleanTag-recycle-test-" + Guid.NewGuid().ToString("N") + ".txt"); await File.WriteAllTextAsync(path, "CleanTag owned recycling test fixture");
    await DeletionService.DeleteAsync(root, Entry(path), false, 0); Require(!File.Exists(path), "Source still exists");
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
    dynamic bin = shell.NameSpace(10); bool found = false;
    foreach (dynamic item in bin.Items()) { string name = item.Name; if (name.StartsWith(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)) { found = true; break; } }
    Require(found, "Recycle Bin entry was not found");
});
await Test("SVG strips active and external content", async () =>
{
    var path = Path.Combine(root, "vector.svg");
    await File.WriteAllTextAsync(path, "<svg xmlns='http://www.w3.org/2000/svg' onload='run()'><script>bad()</script><image href='https://example.com/a.png'/><rect width='20' height='20'/></svg>");
    var preview = PreviewContent.Read(Entry(path), default);
    var xml = System.Text.Encoding.UTF8.GetString(preview.Image!);
    Require(preview.ImageType == "svg" && xml.Contains("rect") && !xml.Contains("https:") && !xml.Contains("script") && !xml.Contains("onload"), "Unsafe SVG retained");
});
await Test("TAR lists files without extracting", async () =>
{
    var path = Path.Combine(root, "archive.tar");
    using (var stream = File.Create(path))
    using (var writer = new System.Formats.Tar.TarWriter(stream))
    { writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "nested/hello.txt") { DataStream = new MemoryStream("hello"u8.ToArray()) }); }
    var preview = PreviewContent.Read(Entry(path), default);
    Require(preview.Text.Contains("nested/hello.txt") && !Directory.Exists(Path.Combine(root, "nested")), "Archive listing failed");
    await Task.CompletedTask;
});
await HistoryChecks.RunAsync(root, Test);
await MigrationChecks.RunAsync(root, Test);
await CatalogChecks.RunAsync(root, Test);
await FolderScanChecks.RunAsync(root, Test);
await MonitoringChecks.RunAsync(root, Test);
await Test("Deferred round preserves Back and returns after remaining cards", async () =>
{
    var a = new FileEntry(Path.Combine(root, "a"), 30, 1);
    var b = new FileEntry(Path.Combine(root, "b"), 20, 1);
    var c = new FileEntry(Path.Combine(root, "c"), 10, 1);
    List<FileEntry> cards = [a, b, c];
    ReviewOrder.Defer(cards, 0);
    int position = ReviewOrder.NextPending(cards, 0, _ => false);
    Require(cards[position] == b && cards[position - 1] == a, "Back lost deferred card");
    position = ReviewOrder.NextPending(cards, position, _ => false);
    Require(cards[position] == c, "Size order changed");
    position = ReviewOrder.NextPending(cards, position, _ => false);
    Require(cards[position] == a, "Deferred card did not return at round end");
    ReviewOrder.Defer(cards, 0);
    Require(cards.Skip(1).Count(f => f == a) == 1, "Repeated Back/Later duplicated future cards");
    Require(ReviewOrder.NextPending(cards, 2, f => f == a) == cards.Count, "Kept/deleted deferred copy not skipped");
    await Task.CompletedTask;
});
var report = new { at = DateTime.UtcNow, fixtureRoot = root, results };
var reportPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-results.json"));
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(reportPath);
// Remove only the exact per-run test tree created above, never a user-selected folder.
if (FileSafety.IsWithin(testBase, root) && Path.GetFileName(root).StartsWith("run-")) Directory.Delete(root, true);
return results.Any(r => JsonSerializer.Serialize(r).Contains("\"passed\":false")) ? 1 : 0;
