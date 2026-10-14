using System.Diagnostics;
using System.IO.Enumeration;

namespace CleanTag.Core;

public enum StorageScanStatus { Completed, Warnings, Cancelled, Failed }
public sealed record StorageNode(int Id, int Parent, string Name, bool IsDirectory, long Length, long ModifiedTicks, FileAttributes Attributes,
    long Bytes, long Files, long Folders, bool Incomplete)
{
    public long? AllocatedBytes => null;
    public string Extension { get; } = IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();
    public string Category { get; } = IsDirectory ? "Klasörler" : new FileEntry(Name, Length, ModifiedTicks).Category;
}
public sealed record StorageScanProgress(long Files, long Folders, long Bytes, string CurrentPath, TimeSpan Elapsed);
public sealed record StorageExtension(string Extension, string Category, long Bytes, long Files);
public sealed record StorageIssue(string Path, string Reason);
public sealed record StorageScanOptions(int MaximumIssues = 100, int ProgressIntervalMilliseconds = 150);
public interface IStorageScanner
{
    Task<StorageAnalysisSnapshot> ScanAsync(string root, StorageScanOptions options, IProgress<StorageScanProgress>? progress, CancellationToken cancellation);
}
public sealed class StorageAnalysisSnapshot
{
    public string RootPath { get; }
    public IReadOnlyList<StorageNode> Nodes { get; }
    public IReadOnlyList<StorageExtension> Extensions { get; }
    public IReadOnlyList<StorageIssue> Issues { get; }
    public long IssueCount { get; }
    public StorageScanStatus Status { get; }
    public DateTime StartedUtc { get; }
    public TimeSpan EnumerationTime { get; }
    public TimeSpan AggregationTime { get; }
    public string Scanner => "Standart dizin listeleme";
    private readonly int[] _offsets, _children;
    public StorageAnalysisSnapshot(string root, StorageNode[] nodes, StorageExtension[] extensions, StorageIssue[] issues, long issueCount,
        StorageScanStatus status, DateTime started, TimeSpan enumeration, TimeSpan aggregation)
    {
        RootPath = root; Nodes = Array.AsReadOnly((StorageNode[])nodes.Clone()); Extensions = Array.AsReadOnly((StorageExtension[])extensions.Clone()); Issues = Array.AsReadOnly((StorageIssue[])issues.Clone());
        IssueCount = issueCount; Status = status; StartedUtc = started; EnumerationTime = enumeration; AggregationTime = aggregation;
        _offsets = new int[nodes.Length + 1];
        for (int i = 1; i < nodes.Length; i++) _offsets[nodes[i].Parent + 1]++;
        for (int i = 1; i < _offsets.Length; i++) _offsets[i] += _offsets[i - 1];
        _children = new int[Math.Max(0, nodes.Length - 1)]; var cursor = (int[])_offsets.Clone();
        for (int i = 1; i < nodes.Length; i++) _children[cursor[nodes[i].Parent]++] = i;
    }
    public IEnumerable<int> Children(int id)
    { for (int i = _offsets[id]; i < _offsets[id + 1]; i++) yield return _children[i]; }
    public string GetPath(int id)
    {
        var parts = new Stack<string>();
        while (id > 0) { parts.Push(Nodes[id].Name); id = Nodes[id].Parent; }
        string path = RootPath; foreach (var part in parts) path = Path.Combine(path, part); return path;
    }
    public FileEntry GetEntry(int id)
    { var n = Nodes[id]; return new(GetPath(id), n.Length, n.ModifiedTicks) { IsDirectory = n.IsDirectory }; }
    public IEnumerable<int> Descendants(int id)
    {
        var pending = new Stack<int>(); pending.Push(id);
        while (pending.Count > 0) { var current = pending.Pop(); yield return current; foreach (int child in Children(current)) pending.Push(child); }
    }
}

public sealed class StandardStorageScanner : IStorageScanner
{
    private sealed class Builder(int id, int parent, string name, bool directory, long length, long modified, FileAttributes attributes)
    {
        public int Id = id, Parent = parent; public string Name = name; public bool Directory = directory, Incomplete;
        public long Length = length, Modified = modified, Bytes = length, Files = directory ? 0 : 1, Folders;
        public FileAttributes Attributes = attributes;
        public StorageNode Freeze() => new(Id, Parent, Name, Directory, Length, Modified, Attributes, Bytes, Files, Folders, Incomplete);
    }
    public Task<StorageAnalysisSnapshot> ScanAsync(string root, StorageScanOptions options, IProgress<StorageScanProgress>? progress, CancellationToken cancellation) => Task.Run<StorageAnalysisSnapshot>(() =>
    {
        root = Path.GetFullPath(root); DateTime started = DateTime.UtcNow; var clock = Stopwatch.StartNew();
        var nodes = new List<Builder>(); var issues = new List<StorageIssue>(); long issueCount = 0, files = 0, folders = 0, bytes = 0;
        void Issue(int id, string path, string reason) { nodes[id].Incomplete = true; issueCount++; if (issues.Count < options.MaximumIssues) issues.Add(new(path, reason)); }
        var rootInfo = new DirectoryInfo(root);
        nodes.Add(new(0, -1, rootInfo.Name.Length == 0 ? root : rootInfo.Name, true, 0, 0, 0));
        var pending = new Stack<(int Id, string Path)>();
        try
        {
            if (!rootInfo.Exists) throw new DirectoryNotFoundException("Klasör bulunamadı.");
            FileSafety.CheckAncestors(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Bağlantı kökü taranmaz.");
            nodes[0].Modified = rootInfo.LastWriteTimeUtc.Ticks; nodes[0].Attributes = rootInfo.Attributes; pending.Push((0, root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Issue(0, root, ex.Message); return new(root, [nodes[0].Freeze()], [], issues.ToArray(), issueCount, StorageScanStatus.Failed, started, clock.Elapsed, TimeSpan.Zero); }
        long lastProgress = -200;
        while (pending.Count > 0 && !cancellation.IsCancellationRequested)
        {
            var current = pending.Pop();
            try
            {
                // Verify the pending directory again: it may have become a junction since discovery.
                FileSafety.CheckAncestors(current.Path);
                if ((File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0) { Issue(current.Id, current.Path, "Bağlantı klasörü atlandı."); continue; }
                var entries = new FileSystemEnumerable<(string Name, bool Directory, long Length, long Modified, FileAttributes Attributes)>(current.Path,
                    (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks, e.Attributes),
                    new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, BufferSize = 65536 });
                foreach (var entry in entries)
                {
                    if (cancellation.IsCancellationRequested) { nodes[current.Id].Incomplete = true; break; }
                    int id = nodes.Count; var node = new Builder(id, current.Id, entry.Name, entry.Directory, entry.Length, entry.Modified, entry.Attributes);
                    nodes.Add(node);
                    if (entry.Directory) folders++; else { files++; bytes = checked(bytes + entry.Length); }
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (!entry.Directory) { node.Length = node.Bytes = 0; bytes -= entry.Length; }
                        Issue(id, Path.Combine(current.Path, entry.Name), "Yeniden ayrıştırma noktası atlandı; hedef izlenmedi.");
                    }
                    else if (entry.Directory) pending.Push((id, Path.Combine(current.Path, entry.Name)));
                    if (clock.ElapsedMilliseconds - lastProgress >= Math.Max(0,options.ProgressIntervalMilliseconds))
                    { progress?.Report(new(files, folders, bytes, current.Path, clock.Elapsed)); lastProgress = clock.ElapsedMilliseconds; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Issue(current.Id, current.Path, ex.Message); }
        }
        if (cancellation.IsCancellationRequested) { nodes[0].Incomplete = true; foreach (var p in pending) nodes[p.Id].Incomplete = true; }
        var enumeration = clock.Elapsed; var aggregate = Stopwatch.StartNew();
        for (int i = nodes.Count - 1; i > 0; i--)
        {
            var n = nodes[i]; var parent = nodes[n.Parent];
            parent.Bytes = checked(parent.Bytes + n.Bytes); parent.Files = checked(parent.Files + n.Files);
            parent.Folders = checked(parent.Folders + n.Folders + (n.Directory ? 1 : 0)); parent.Incomplete |= n.Incomplete;
        }
        var extensions = nodes.Where(n => !n.Directory).GroupBy(n => Path.GetExtension(n.Name).ToLowerInvariant())
            .Select(g => new StorageExtension(g.Key, new FileEntry(g.First().Name, 0, 0).Category, g.Sum(n => n.Length), g.LongCount())).OrderByDescending(e => e.Bytes).ToArray();
        var frozen = nodes.Select(n => n.Freeze()).ToArray(); aggregate.Stop();
        progress?.Report(new(files, folders, bytes, root, clock.Elapsed));
        return new(root, frozen, extensions, issues.ToArray(), issueCount, cancellation.IsCancellationRequested ? StorageScanStatus.Cancelled : issueCount > 0 ? StorageScanStatus.Warnings : StorageScanStatus.Completed, started, enumeration, aggregate.Elapsed);
    });
}
