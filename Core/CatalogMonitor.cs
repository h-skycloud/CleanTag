using System.Collections.Concurrent;

namespace CleanTag.Core;

public sealed record MonitorUpdate(string Root, int Changes, string Message, bool Warning = false);

// Watcher callbacks only enqueue hints. A single background consumer observes the disk and updates SQLite.
public sealed class CatalogMonitor(CatalogStore store, IReadOnlyCollection<string>? excludedRoots = null) : IAsyncDisposable
{
    private sealed record Pending(CatalogSource Source, string Path, string? OldPath, long At, bool Descendants);
    private sealed record Observation(long Size, long Modified, long At);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CatalogSource> _rescan = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _faulted = new();
    private readonly Dictionary<string, (CatalogSource Source, FileSystemWatcher Watcher)> _watchers = [];
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = [];
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _scanCts;
    private Task? _worker;
    private volatile bool _enabled;
    private int _disposed;
    private readonly string[] _excluded = (excludedRoots ?? []).Append(Path.GetDirectoryName(store.DatabasePath)!)
        .Select(CatalogText.PathKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public bool Enabled => _enabled;
    public event Action<MonitorUpdate>? Updated;

    public async Task StartAsync()
    {
        if (_worker != null) return;
        _enabled = await store.GetMonitoringEnabledAsync();
        _worker = Task.Run(RunAsync);
    }
    public async Task SetEnabledAsync(bool enabled, bool persist = true)
    {
        if (persist) await store.SetMonitoringEnabledAsync(enabled);
        _enabled = enabled;
        if (!enabled) CancelScan();
        await _workGate.WaitAsync();
        try
        {
            if (!enabled) { DisposeWatchers(); _pending.Clear(); _rescan.Clear(); _observations.Clear(); }
        }
        finally { _workGate.Release(); }
        Publish(new("", 0, enabled ? "Otomatik güncelleme açık" : "Otomatik güncelleme kapalı"));
    }
    public void RequestReconcile(CatalogSource source) => _rescan[source.Id] = source;

    private bool Excluded(string path)
    {
        string key = CatalogText.PathKey(path);
        return _excluded.Any(root => key == root || key.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
    private void Enqueue(CatalogSource source, string path, string? oldPath = null, bool descendants = false)
    {
        if (!_enabled || _stop.IsCancellationRequested || Excluded(path)) return;
        string key = source.Id + "|" + path;
        _pending.AddOrUpdate(key, new Pending(source, path, oldPath, Environment.TickCount64, descendants), (_, prior) =>
            new(source, path, oldPath ?? prior.OldPath, Environment.TickCount64, descendants || prior.Descendants));
        // Bound memory during an event storm. A full reconciliation replaces untrustworthy hints.
        if (_pending.Count > 4096)
        { foreach (var change in _pending.Values) RequestReconcile(change.Source); _pending.Clear(); }
    }
    private void Publish(MonitorUpdate update)
    {
        foreach (Action<MonitorUpdate> subscriber in Updated?.GetInvocationList() ?? [])
            try { subscriber(update); } catch { /* A closed view cannot stop monitoring. */ }
    }
    private async Task ConfigureAsync()
    {
        var sources = (await store.GetSourcesAsync()).Where(s => s.Content != SourceContent.None && !Excluded(s.Path)).ToDictionary(s => s.Id);
        foreach (string id in _watchers.Keys.ToArray())
            if (_faulted.TryRemove(id, out _) || !sources.TryGetValue(id, out var source) || _watchers[id].Source.Path != source.Path || _watchers[id].Source.Content != source.Content)
            { _watchers[id].Watcher.Dispose(); _watchers.Remove(id); }
        foreach (var source in sources.Values)
        {
            if (_watchers.ContainsKey(source.Id)) continue;
            try
            {
                FileSafety.CheckAncestors(source.Path);
                var watcher = new FileSystemWatcher(source.Path)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 16384,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
                };
                watcher.Created += (_, e) => Enqueue(source, e.FullPath, descendants: true);
                watcher.Changed += (_, e) => Enqueue(source, e.FullPath);
                watcher.Deleted += (_, e) => Enqueue(source, e.FullPath);
                watcher.Renamed += (_, e) => Enqueue(source, e.FullPath, e.OldFullPath, true);
                watcher.Error += (_, _) =>
                {
                    RequestReconcile(source);
                    _faulted[source.Id] = true;
                    Publish(new(source.Path, 0, "İzleme kesintisi algılandı. Konum yeniden kontrol edilecek.", true));
                };
                watcher.EnableRaisingEvents = true;
                _watchers[source.Id] = (source, watcher); _failed.Remove(source.Id);
                RequestReconcile(source); // Reconcile changes that happened while the app was closed.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { if (_failed.Add(source.Id)) Publish(new(source.Path, 0, "Konuma erişilemiyor. Etiketler korundu; erişim gelince yeniden denenecek.", true)); }
        }
    }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250)); long nextConfigure = 0, retryAfter = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (!_enabled || Environment.TickCount64 < retryAfter) continue;
                await _workGate.WaitAsync(_stop.Token);
                try
                {
                    if (!_enabled) continue;
                    if (Environment.TickCount64 >= nextConfigure)
                    { await ConfigureAsync(); nextConfigure = Environment.TickCount64 + 2000; }
                    foreach (var pair in _rescan.ToArray())
                    {
                        if (!_enabled || _stop.IsCancellationRequested) break;
                        if (!_rescan.TryRemove(pair)) continue;
                        if (!_watchers.TryGetValue(pair.Key, out var current) || current.Source.Path != pair.Value.Path || current.Source.Content != pair.Value.Content) continue;
                        await ReconcileAsync(pair.Value);
                    }
                    await ProcessPendingAsync();
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested || !_enabled) { }
                catch (Exception ex)
                {
                    Publish(new("", 0, "Otomatik güncelleme tamamlanamadı. Yenile ile tekrar deneyebilirsiniz. " + ex.Message, true));
                    // Retain/recover hints after a failed database write instead of silently losing them.
                    foreach (var watcher in _watchers.Values) RequestReconcile(watcher.Source);
                    retryAfter = Environment.TickCount64 + 5000;
                }
                finally { _workGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { DisposeWatchers(); }
    }
    private async Task ReconcileAsync(CatalogSource source)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); _scanCts = cts;
        try
        {
            if ((source.Content & SourceContent.Files) != 0)
            {
                var result = await FileScanner.ScanAsync(source.Path, null, cts.Token, _excluded);
                var previous = (await store.GetEntriesAsync(source.Path)).ToDictionary(f => CatalogText.PathKey(f.FullPath));
                var unstable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidates = result.Files.Where(f => !previous.TryGetValue(CatalogText.PathKey(f.FullPath), out var known) || known.Identity != f.Identity).ToArray();
                if (candidates.Length > 0) await Task.Delay(500, cts.Token);
                foreach (var candidate in candidates)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(candidate.FullPath); info.Refresh();
                        if (info.Length != candidate.Size || info.LastWriteTimeUtc.Ticks != candidate.ModifiedTicks) throw new IOException("Dosya yazılıyor.");
                        using (File.OpenHandle(candidate.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { unstable.Add(candidate.FullPath); Enqueue(source, candidate.FullPath); }
                }
                if (unstable.Count > 0) result = result with { Files = result.Files.Where(f => !unstable.Contains(f.FullPath)).ToList(), Skipped = result.Skipped + unstable.Count };
                cts.Token.ThrowIfCancellationRequested(); await store.IndexScanAsync(source.Path, result);
                if (result.Errors.Count > 0 || result.Skipped > unstable.Count) Publish(new(source.Path, 0, "Bazı öğeler okunamadı. Önceki kayıtlar korundu.", true));
            }
            if ((source.Content & SourceContent.FolderTree) != 0)
            {
                var result = await FileScanner.ScanAsync(source.Path, null, cts.Token, _excluded, true);
                cts.Token.ThrowIfCancellationRequested(); await store.IndexScanAsync(source.Path, result, true);
            }
            else if ((source.Content & SourceContent.Folders) != 0) await store.RefreshFoldersAsync(source.Path);
            Publish(new(source.Path, 1, "Konum güncel"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Publish(new(source.Path, 0, "Konum okunamadı. Önceki kayıtlar ve etiketler korundu.", true)); }
        finally { _scanCts = null; }
    }
    private async Task ProcessPendingAsync()
    {
        foreach (var group in _pending.ToArray().Where(p => Environment.TickCount64 - p.Value.At >= 750).GroupBy(p => p.Value.Source.Id))
        {
            if (!_enabled || _stop.IsCancellationRequested) return;
            var source = group.First().Value.Source;
            if (!_watchers.TryGetValue(source.Id, out var current) || current.Source.Path != source.Path || current.Source.Content != source.Content)
            { foreach (var stale in group) _pending.TryRemove(stale); continue; }
            bool files = (source.Content & SourceContent.Files) != 0, folders = (source.Content & SourceContent.FolderTree) != 0;
            var entries = new List<FileEntry>(); var removed = new List<string>(); var processed = new List<KeyValuePair<string, Pending>>();
            foreach (var pair in group)
            {
                var pending = pair.Value;
                try
                {
                    FileSafety.CheckAncestors(Path.GetDirectoryName(pending.Path)!);
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(pending.Path); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        removed.Add(pending.Path); if (pending.OldPath != null) removed.Add(pending.OldPath);
                        processed.Add(pair); continue;
                    }
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0) { processed.Add(pair); continue; }
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    if (!directory && (!files || FileScanner.IsTemporary(pending.Path))) { processed.Add(pair); continue; }
                    if (directory)
                    {
                        entries.Add(new(pending.Path, 0, Directory.GetLastWriteTimeUtc(pending.Path).Ticks) { IsDirectory = true });
                        // A moved directory may raise only one event. Scan that subtree, never the whole source.
                        if (pending.Descendants)
                        {
                            if (files)
                                foreach (var child in (await FileScanner.ScanAsync(pending.Path, null, _stop.Token, _excluded)).Files)
                                    Enqueue(source, child.FullPath); // Descendants use the same stability checks as individual file events.
                            if (folders) entries.AddRange((await FileScanner.ScanAsync(pending.Path, null, _stop.Token, _excluded, true)).Files);
                        }
                    }
                    else
                    {
                        var info = new FileInfo(pending.Path); info.Refresh(); long now = Environment.TickCount64;
                        if (!_observations.TryGetValue(pair.Key, out var prior) || prior.Size != info.Length || prior.Modified != info.LastWriteTimeUtc.Ticks)
                        { _observations[pair.Key] = new(info.Length, info.LastWriteTimeUtc.Ticks, now); continue; }
                        if (now - prior.At < 500) continue;
                        // A size/date plateau is not enough while a writer still holds the file open.
                        using (File.OpenHandle(pending.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }
                        entries.Add(new(pending.Path, info.Length, info.LastWriteTimeUtc.Ticks));
                    }
                    if (pending.OldPath != null && !Excluded(pending.OldPath)) removed.Add(pending.OldPath);
                    processed.Add(pair);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Retry locked/newly removed parents for a bounded period, then reconcile conservatively.
                    if (!File.Exists(pending.Path) && !Directory.Exists(pending.Path) && Environment.TickCount64 - pending.At > 10000)
                    { RequestReconcile(source); processed.Add(pair); }
                }
            }
            int changes = await store.ApplyChangesAsync(source.Path, entries, removed, files, folders);
            foreach (var pair in processed) if (_pending.TryRemove(pair)) _observations.Remove(pair.Key);
            if (changes > 0) Publish(new(source.Path, changes, "Yeni değişiklikler katalogda"));
        }
        foreach (string key in _observations.Keys.Where(k => !_pending.ContainsKey(k)).ToArray()) _observations.Remove(key);
    }
    private void CancelScan() { try { _scanCts?.Cancel(); } catch (ObjectDisposedException) { } }
    private void DisposeWatchers() { foreach (var watcher in _watchers.Values) watcher.Watcher.Dispose(); _watchers.Clear(); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); CancelScan(); if (_worker != null) await _worker;
        _pending.Clear(); _observations.Clear(); _stop.Dispose();
    }
}
