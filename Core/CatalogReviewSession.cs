namespace CleanTag.Core;

public sealed class CatalogReviewSession(CatalogStore catalog) : IReviewHistory
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private Dictionary<string, ReviewDecision> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime?> _creationDates = new(StringComparer.OrdinalIgnoreCase);
    public string Root { get; private set; } = "";
    public string FilePath => catalog.DatabasePath;
    public bool IsReviewed(FileEntry file) => GetDecision(file)?.Action is "keep" or "recycle" or "permanent" or "missing";
    public bool IsDeferred(FileEntry file) => GetDecision(file)?.Action == "defer";
    public ReviewDecision? GetDecision(FileEntry file) => Volatile.Read(ref _decisions).GetValueOrDefault(file.Identity);
    public Task LoadAsync(string root) => Serialized(async () =>
    { Root = Path.GetFullPath(root); Volatile.Write(ref _decisions, await catalog.LoadDecisionsAsync().ConfigureAwait(false)); });

    public Task RecordAsync(FileEntry file, string action) => Serialized(async () =>
    {
        if (action is not ("view" or "viewed" or "keep" or "defer" or "recycle" or "permanent" or "missing" or "revisit")) throw new ArgumentException("Unknown review action", nameof(action));
        if (string.IsNullOrEmpty(Root) || !FileSafety.IsWithin(Root, file.FullPath)) throw new IOException("Dosya açık klasörün dışında.");
        DateTime? created = GetDecision(file)?.CreatedUtc ?? _creationDates.GetValueOrDefault(file.Identity);
        try { var info = new FileInfo(file.FullPath); if (info.Exists) created = info.CreationTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var decision = new ReviewDecision(Root, file.Identity, file.FullPath, action, file.Size, DateTime.UtcNow)
        { Name = file.Name, Folder = file.Folder, Extension = file.Extension, CreatedUtc = created,
            ModifiedUtc = new DateTime(file.ModifiedTicks, DateTimeKind.Utc), SessionId = _sessionId };
        await catalog.RecordDecisionAsync(decision, file).ConfigureAwait(false);
        _creationDates[file.Identity] = created;
        if (action is "view" or "viewed") return;
        var updated = new Dictionary<string, ReviewDecision>(Volatile.Read(ref _decisions), StringComparer.OrdinalIgnoreCase);
        if (action == "revisit") updated.Remove(file.Identity);
        else if (action is "keep" or "defer" or "recycle" or "permanent" or "missing") updated[file.Identity] = decision;
        Volatile.Write(ref _decisions, updated);
    });

    public Task ResetAsync() => Serialized(async () =>
    {
        if (Root.Length == 0) throw new InvalidOperationException("Open a folder first.");
        await catalog.RecordDecisionAsync(new(Root, "", "", "reset", 0, DateTime.UtcNow) { SessionId = _sessionId }).ConfigureAwait(false);
        Volatile.Write(ref _decisions, await catalog.LoadDecisionsAsync().ConfigureAwait(false));
    });

    private async Task Serialized(Func<Task> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async Task FlushAsync() { await _gate.WaitAsync().ConfigureAwait(false); _gate.Release(); await catalog.FlushAsync().ConfigureAwait(false); }
}
