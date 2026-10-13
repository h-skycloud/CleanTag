using System.Text.Json;
using CleanTag.Core;

internal static class HistoryChecks
{
    public static async Task RunAsync(string fixtureRoot, Func<string, Func<Task>, Task> test)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static FileEntry Entry(string path) { var f = new FileInfo(path); return new(path, f.Length, f.LastWriteTimeUtc.Ticks); }
        var root = Path.Combine(fixtureRoot, "history");
        var child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
        var aPath = Path.Combine(child, "Türkçe [1].txt"); await File.WriteAllTextAsync(aPath, "history fixture");
        var bPath = Path.Combine(root, "sibling.txt"); await File.WriteAllTextAsync(bPath, "sibling fixture");
        var a = Entry(aPath); var b = Entry(bPath);
        var ledger = Path.Combine(root, "decisions.json");
        await test("JSON history persists defer across ancestor scans; viewed does not clear decisions", async () =>
        {
            var session = new ReviewSession(ledger); await session.LoadAsync(child);
            await session.RecordAsync(a, "defer"); await session.RecordAsync(a, "viewed");
            var reopened = new ReviewSession(ledger); await reopened.LoadAsync(root);
            Require(reopened.IsDeferred(a) && !reopened.IsReviewed(a), "Deferred state lost across ancestor open");
            await reopened.RecordAsync(a, "keep"); await reopened.RecordAsync(a, "view");
            Require(reopened.IsReviewed(a) && !reopened.IsDeferred(a), "View cleared keep");
            Require(!reopened.IsReviewed(a with { Size = a.Size + 1 }), "Changed file must be reviewed again");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ledger));
            var last = json.RootElement.GetProperty("Events")[3];
            Require(last.GetProperty("Name").GetString() == a.Name && last.GetProperty("Bytes").GetInt64() == a.Size && last.GetProperty("ModifiedUtc").ValueKind == JsonValueKind.String && last.GetProperty("SessionId").GetString()!.Length > 0, "Metadata incomplete");
        });
        await test("Subtree reset preserves sibling decisions and full audit history", async () =>
        {
            var session = new ReviewSession(ledger); await session.LoadAsync(root); await session.RecordAsync(b, "keep");
            await session.LoadAsync(child); await session.ResetAsync(); await session.LoadAsync(root);
            Require(!session.IsReviewed(a) && session.IsReviewed(b), "Reset escaped subtree");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ledger));
            Require(json.RootElement.GetProperty("Events").EnumerateArray().Any(e => e.GetProperty("Action").GetString() == "keep" && e.GetProperty("Path").GetString() == aPath), "Audit was erased");
        });
        await test("Legacy JSONL migration retains source and ignores incomplete tail", async () =>
        {
            var legacy = Path.Combine(root, "migrate.jsonl");
            await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new ReviewDecision(child, a.Identity, a.FullPath, "keep", a.Size, DateTime.UtcNow)) + "\n{\"Root\":");
            var session = new ReviewSession(Path.ChangeExtension(legacy, ".json")); await session.LoadAsync(root);
            Require(session.IsReviewed(a) && File.Exists(legacy) && File.Exists(session.FilePath), "Migration failed");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(session.FilePath));
            Require(json.RootElement.GetProperty("Events").GetArrayLength() == 1, "Legacy audit mismatch");
        });
        await test("Deletion audit retains viewed creation metadata after file disappears", async () =>
        {
            var path = Path.Combine(child, "owned-removed.txt"); await File.WriteAllTextAsync(path, "owned fixture"); var entry = Entry(path);
            var session = new ReviewSession(ledger); await session.LoadAsync(root); await session.RecordAsync(entry, "viewed");
            File.Delete(path); await session.RecordAsync(entry, "permanent");
            Require(session.GetDecision(entry)?.CreatedUtc != null && session.GetDecision(entry)?.ModifiedUtc?.Ticks == entry.ModifiedTicks, "Deletion metadata lost");
        });
        await test("Concurrent session writers preserve both decisions", async () =>
        {
            var one = new ReviewSession(ledger); var two = new ReviewSession(ledger);
            await Task.WhenAll(one.LoadAsync(root), two.LoadAsync(root));
            await Task.WhenAll(one.RecordAsync(a, "defer"), two.RecordAsync(b, "defer"));
            await one.LoadAsync(root); Require(one.IsDeferred(a) && one.IsDeferred(b), "Concurrent write lost decision");
        });
        await test("Failed history replacement preserves disk and in-memory decision", async () =>
        {
            var session = new ReviewSession(ledger); await session.LoadAsync(root); var before = await File.ReadAllTextAsync(ledger);
            using (var locked = new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false; try { await session.RecordAsync(a, "keep"); } catch (IOException) { rejected = true; }
                Require(rejected && session.IsDeferred(a), "Failed persist changed in-memory state");
            }
            Require(await File.ReadAllTextAsync(ledger) == before, "Failed persist damaged ledger");
        });
        await test("External process lock delays history mutation; Flush waits for queued record", async () =>
        {
            var session = new ReviewSession(ledger); await session.LoadAsync(root);
            var marker = Path.Combine(root, "lock-ready");
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            string command = "$f = [System.IO.File]::Open(" + Quote(ledger + ".lock") + ", 'OpenOrCreate', 'ReadWrite', 'None'); try { [System.IO.File]::WriteAllText(" + Quote(marker) + ", 'ready'); Start-Sleep -Milliseconds 1400 } finally { $f.Dispose() }";
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)));
            using var holder = System.Diagnostics.Process.Start(start)!;
            try
            {
                var deadline = Environment.TickCount64 + 5000;
                while (!File.Exists(marker) && !holder.HasExited && Environment.TickCount64 < deadline) await Task.Delay(25);
                Require(File.Exists(marker), "External process did not acquire lock");
                var record = session.RecordAsync(a, "keep"); var flush = session.FlushAsync();
                await Task.Delay(150); Require(!record.IsCompleted && !flush.IsCompleted, "External lock or Flush barrier ignored");
                await Task.WhenAll(record, flush); Require(session.IsReviewed(a), "Queued record was lost");
                await holder.WaitForExitAsync(); Require(holder.ExitCode == 0, "Lock holder failed");
            }
            finally { if (!holder.HasExited) { holder.Kill(); await holder.WaitForExitAsync(); } }
        });
    }
}
