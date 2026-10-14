using System.IO.Enumeration;

namespace CleanTag.Core;

public sealed record FileEntry(string FullPath, long Size, long ModifiedTicks)
{
    public string? NativeId { get; init; }
    public bool IsDirectory { get; init; }
    public override string ToString() => Name;
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(FullPath)) is { Length: > 0 } name ? name : FullPath;
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();
    public string Identity => $"{FullPath.ToUpperInvariant()}|{Size}|{ModifiedTicks}";
    public string SizeLabel => IsDirectory ? "İçerik boyutu hesaplanmadı" : FormatSize(Size);
    public string ModifiedLabel => new DateTime(ModifiedTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy · HH:mm");
    public string TypeLabel => IsDirectory ? "Klasör" : string.IsNullOrEmpty(Extension) ? "Dosya" : Extension.TrimStart('.').ToUpperInvariant();
    public string Category => IsDirectory ? "Klasörler" : Extension switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".svg" or ".ico" or ".avif" => "Görseller",
        ".psd" or ".psb" or ".ai" or ".indd" or ".skp" or ".skb" or ".dwg" or ".dxf" or ".rvt" or ".rfa" or ".max" or ".blend" or ".3ds" or ".obj" or ".fbx" or ".pln" => "Tasarım / model",
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" or ".m4v" => "Videolar",
        ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".wma" => "Sesler",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso" => "Arşivler",
        ".pdf" or ".docx" or ".doc" or ".xlsx" or ".xls" or ".pptx" or ".ppt" or ".odt" or ".ods" or ".odp" or ".rtf" or ".txt" or ".md" or ".csv" => "Belgeler",
        _ => "Diğer"
    };
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}

public sealed record ScanProgress(int Files, long Bytes, string Folder);
public sealed record ScanResult(List<FileEntry> Files, List<string> Errors, int Skipped, bool Cancelled);

public static class FileScanner
{
    public static bool IsTemporary(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal) || Path.GetExtension(name).ToLowerInvariant() is ".crdownload" or ".part" or ".partial" or ".tmp" or ".temp";
    }
    public static Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken token,
        IReadOnlyCollection<string>? excludedRoots = null, bool foldersOnly = false,
        Func<IReadOnlyList<FileEntry>, Task>? onBatch = null, bool ignoreTemporary = true) => Task.Run(async () =>
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        FileSafety.CheckAncestors(root);
        List<FileEntry> files = []; List<string> errors = []; int skipped = 0; long bytes = 0;
        var stack = new Stack<string>(); stack.Push(root);
        var excluded = (excludedRoots ?? []).Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray();
        bool Excluded(string path) => excluded.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (Excluded(root)) return new ScanResult([], [], 0, token.IsCancellationRequested);
        var batch = new List<FileEntry>(512);
        if (foldersOnly && !token.IsCancellationRequested)
        {
            var entry = new FileEntry(root, 0, Directory.GetLastWriteTimeUtc(root).Ticks) { IsDirectory = true };
            files.Add(entry); if (onBatch != null) batch.Add(entry);
        }
        var lastReport = Environment.TickCount64;
        while (stack.Count > 0 && !token.IsCancellationRequested)
        {
            var folder = stack.Pop();
            try
            {
                var entries = new FileSystemEnumerable<FileEntry>(folder, (ref FileSystemEntry e) =>
                    new(e.ToFullPath(), e.IsDirectory ? 0 : e.Length,
                        (e.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0 ? -1 : e.LastWriteTimeUtc.UtcTicks)
                        { IsDirectory = e.IsDirectory },
                    new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, BufferSize = 65536 });
                entries.ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory || !foldersOnly && (!ignoreTemporary || !IsTemporary(e.FileName.ToString()));
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        if (Excluded(entry.FullPath)) continue;
                        // Unsafe entries carry a sentinel from enumeration and are never descended into.
                        if (entry.ModifiedTicks < 0) { skipped++; continue; }
                        if (entry.IsDirectory) stack.Push(entry.FullPath);
                        if (entry.IsDirectory == foldersOnly)
                        {
                            files.Add(entry); if (onBatch != null) batch.Add(entry); bytes += entry.Size;
                        }
                        if (onBatch != null && batch.Count >= 512)
                        {
                            try { await onBatch(batch.ToArray()).ConfigureAwait(false); }
                            catch (Exception ex) { throw new InvalidOperationException("Tarama partisi kaydedilemedi.", ex); }
                            batch.Clear();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { skipped++; if (errors.Count < 50) errors.Add($"{entry.FullPath}: {ex.Message}"); }
                    if (Environment.TickCount64 - lastReport >= 120)
                    { progress?.Report(new(files.Count, bytes, folder)); lastReport = Environment.TickCount64; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { skipped++; if (errors.Count < 50) errors.Add($"{folder}: {ex.Message}"); }
        }
        if (onBatch != null && batch.Count > 0) await onBatch(batch.ToArray()).ConfigureAwait(false);
        progress?.Report(new(files.Count, bytes, root));
        return new ScanResult(files, errors, skipped, token.IsCancellationRequested);
    });
}

public static class FileSafety
{
    public static bool IsWithin(string root, string candidate)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        candidate = Path.GetFullPath(candidate);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);
    }
    public static void CheckAncestors(string path)
    {
        for (var d = new DirectoryInfo(path); d != null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Bağlantı veya yönlendirilmiş klasör üzerinden işlem yapılamaz.");
    }
    public static void Validate(string root, FileEntry entry)
    {
        if (!IsWithin(root, entry.FullPath)) throw new IOException("Dosya seçilen klasörün dışında.");
        CheckAncestors(entry.Folder);
        var f = new FileInfo(entry.FullPath); f.Refresh();
        if (!f.Exists) throw new FileNotFoundException("Dosya artık bu konumda bulunmuyor.", entry.FullPath);
        if ((f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline | FileAttributes.Directory)) != 0)
            throw new IOException("Sistem, çevrimdışı veya bağlantılı dosyalarda silme devre dışı.");
        if (f.Length != entry.Size || f.LastWriteTimeUtc.Ticks != entry.ModifiedTicks)
            throw new IOException("Dosya taramadan sonra değişti. Yeniden tarayıp içeriğini kontrol edin.");
        if (entry.NativeId != null && FileIdentity.TryRead(entry.FullPath) != entry.NativeId)
            throw new IOException("Dosya kimliği değişti. Yeniden tarayıp içeriğini kontrol edin.");
    }
}
