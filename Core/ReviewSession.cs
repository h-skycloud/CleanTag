using System.Collections.Concurrent;
using System.Text.Json;

namespace CleanTag.Core;

public sealed record ReviewDecision(string Root, string Identity, string Path, string Action, long Bytes, DateTime At)
{
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Extension { get; init; } = "";
    public DateTime? CreatedUtc { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public string SessionId { get; init; } = "";
}

public sealed class ReviewSession : IReviewHistory
{
    private sealed record HistoryDocument(int Version, List<ReviewDecision> Events);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> Actions = ["view", "viewed", "keep", "defer", "recycle", "permanent", "missing", "revisit"];
    private readonly string _file;
    private readonly SemaphoreSlim _gate;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private Dictionary<string, ReviewDecision> _decisions = new(StringComparer.OrdinalIgnoreCase);
    public string Root { get; private set; } = "";
    public string FilePath => _file;
    public ReviewSession(string file)
    {
        _file = Path.GetFullPath(file);
        _gate = Gates.GetOrAdd(_file, _ => new SemaphoreSlim(1, 1));
    }

    public Task LoadAsync(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        return RunSerializedAsync(async () =>
        {
            var (events, migrate) = await ReadAsync().ConfigureAwait(false);
            if (migrate) await PersistAsync(events).ConfigureAwait(false);
            Root = fullRoot;
            Volatile.Write(ref _decisions, Replay(events));
        });
    }

    public bool IsReviewed(FileEntry file) => GetDecision(file)?.Action is "keep" or "recycle" or "permanent" or "missing";
    public bool IsDeferred(FileEntry file) => GetDecision(file)?.Action == "defer";
    public ReviewDecision? GetDecision(FileEntry file) => Volatile.Read(ref _decisions).GetValueOrDefault(file.Identity);

    public Task RecordAsync(FileEntry file, string action)
    {
        if (!Actions.Contains(action)) throw new ArgumentException("Unknown review action", nameof(action));
        return RunSerializedAsync(async () =>
        {
            if (string.IsNullOrEmpty(Root) || !FileSafety.IsWithin(Root, file.FullPath))
                throw new IOException("İncelenen dosya açık klasörün dışında.");
            var (events, _) = await ReadAsync().ConfigureAwait(false);
            var previous = events.LastOrDefault(e => string.Equals(e.Identity, file.Identity, StringComparison.OrdinalIgnoreCase));
            DateTime? created = previous?.CreatedUtc;
            try
            {
                var info = new FileInfo(file.FullPath);
                if (info.Exists) created = info.CreationTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            events.Add(new ReviewDecision(Root, file.Identity, file.FullPath, action, file.Size, DateTime.UtcNow)
            {
                Name = file.Name, Folder = file.Folder, Extension = file.Extension,
                CreatedUtc = created, ModifiedUtc = new DateTime(file.ModifiedTicks, DateTimeKind.Utc), SessionId = _sessionId
            });
            await PersistAsync(events).ConfigureAwait(false);
            Volatile.Write(ref _decisions, Replay(events));
        });
    }

    public Task ResetAsync()
    {
        return RunSerializedAsync(async () =>
        {
            if (string.IsNullOrEmpty(Root)) throw new InvalidOperationException("Open a folder first.");
            var (events, _) = await ReadAsync().ConfigureAwait(false);
            events.Add(new(Root, "", "", "reset", 0, DateTime.UtcNow) { SessionId = _sessionId });
            await PersistAsync(events).ConfigureAwait(false);
            Volatile.Write(ref _decisions, Replay(events));
        });
    }

    // Taking the gate before dispatch means close/Flush observes already queued calls.
    private async Task RunSerializedAsync(Func<Task> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                using var lease = await AcquireProcessLockAsync().ConfigureAwait(false);
                await operation().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<FileStream> AcquireProcessLockAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var started = Environment.TickCount64;
        while (true)
        {
            try { return new FileStream(_file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - started < 10000)
            { await Task.Delay(80).ConfigureAwait(false); }
        }
    }

    public async Task FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private static Dictionary<string, ReviewDecision> Replay(List<ReviewDecision> events)
    {
        var decisions = new Dictionary<string, ReviewDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in events)
        {
            if (entry.Action == "reset")
            {
                foreach (var key in decisions.Where(p => FileSafety.IsWithin(entry.Root, p.Value.Path)).Select(p => p.Key).ToArray())
                    decisions.Remove(key);
            }
            else if (entry.Action == "revisit") decisions.Remove(entry.Identity);
            else if (entry.Action is "keep" or "defer" or "recycle" or "permanent" or "missing") decisions[entry.Identity] = entry;
        }
        return decisions;
    }

    private async Task<(List<ReviewDecision> Events, bool Migrate)> ReadAsync()
    {
        string source = _file;
        if (!File.Exists(source))
        {
            var legacy = Path.ChangeExtension(_file, ".jsonl");
            if (!string.Equals(source, legacy, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy)) source = legacy;
            else return ([], false);
        }
        string text = await File.ReadAllTextAsync(source).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return ([], false);
        // A document beginning with Version is the new format. Never replace damaged
        // new-format history with an empty ledger; surface the error instead.
        if (text.TrimStart().StartsWith("{\n", StringComparison.Ordinal) || text.Contains("\"Version\"", StringComparison.OrdinalIgnoreCase))
        {
            var document = JsonSerializer.Deserialize<HistoryDocument>(text, JsonOptions)
                ?? throw new JsonException("History document is empty.");
            if (document.Version != 1 || document.Events == null) throw new JsonException("Unsupported history format.");
            return (document.Events, !string.Equals(source, _file, StringComparison.OrdinalIgnoreCase));
        }
        List<ReviewDecision> events = [];
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var old = JsonSerializer.Deserialize<ReviewDecision>(line, JsonOptions);
                if (old == null || string.IsNullOrEmpty(old.Root) || string.IsNullOrEmpty(old.Action)) continue;
                DateTime? modified = null;
                if (long.TryParse(old.Identity.Split('|').LastOrDefault(), out var ticks) && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
                    modified = new DateTime(ticks, DateTimeKind.Utc);
                events.Add(old with { Name = Path.GetFileName(old.Path), Folder = Path.GetDirectoryName(old.Path) ?? "", Extension = Path.GetExtension(old.Path).ToLowerInvariant(), ModifiedUtc = modified });
            }
            catch (JsonException) { /* The old append-only format may have an interrupted final line. */ }
        }
        return (events, true);
    }

    private async Task PersistAsync(List<ReviewDecision> events)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new HistoryDocument(1, events), JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement is atomic. Publish in-memory decisions only
            // after this succeeds so a failed write cannot silently discard history.
            if (File.Exists(_file)) File.Replace(temporary, _file, null);
            else File.Move(temporary, _file);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
