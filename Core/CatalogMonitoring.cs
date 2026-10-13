using Microsoft.Data.Sqlite;

namespace CleanTag.Core;

public sealed partial class CatalogStore
{
    private static void AddSourceContent(SqliteConnection connection, SqliteTransaction transaction, string id, SourceContent content)
    {
        Execute(connection, transaction, """
            INSERT INTO Metadata(Key,Value) VALUES($key,$value) ON CONFLICT(Key)
            DO UPDATE SET Value=CAST(CAST(Metadata.Value AS INTEGER) | CAST(excluded.Value AS INTEGER) AS TEXT)
            """, ("$key", "source-content:" + id), ("$value", ((int)content).ToString()));
    }

    private static void EnsureSourceContent(SqliteConnection connection)
    {
        // Older catalogs have no mode preference. Infer it once from their existing records.
        Execute(connection, null, """
            INSERT OR IGNORE INTO Metadata(Key,Value)
            SELECT 'source-content:'||s.Id, CAST(
                CASE WHEN EXISTS(SELECT 1 FROM Files f WHERE instr(f.PathKey,s.PathKey||$separator)=1 AND f.Category<>'Klasörler') THEN 1 ELSE 0 END |
                CASE WHEN EXISTS(SELECT 1 FROM Files f WHERE (f.PathKey=s.PathKey OR instr(f.PathKey,s.PathKey||$separator)=1) AND f.Category='Klasörler') THEN 2 ELSE 0 END |
                CASE WHEN EXISTS(SELECT 1 FROM Files f WHERE instr(f.PathKey,s.PathKey||$separator)=1 AND f.Category='Klasörler') THEN 4 ELSE 0 END |
                CASE WHEN NOT EXISTS(SELECT 1 FROM Files f WHERE f.PathKey=s.PathKey OR instr(f.PathKey,s.PathKey||$separator)=1) AND s.ScanStatus='Tarandı' THEN 1 ELSE 0 END AS TEXT)
            FROM Sources s WHERE NOT EXISTS(SELECT 1 FROM Metadata m WHERE m.Key='source-content:'||s.Id)
            """, ("$separator", Path.DirectorySeparatorChar.ToString()));
    }

    public Task<bool> GetMonitoringEnabledAsync() => RunAsync(connection =>
        Scalar(connection, null, "SELECT Value FROM Metadata WHERE Key='monitoring-enabled'") as string != "0");
    public Task SetMonitoringEnabledAsync(bool enabled) => RunAsync(connection =>
    {
        Execute(connection, null, "INSERT INTO Metadata(Key,Value) VALUES('monitoring-enabled',$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$value", enabled ? "1" : "0"));
        return true;
    });

    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string root) => RunAsync<IReadOnlyList<FileEntry>>(connection =>
    {
        var entries = new List<FileEntry>();
        using var command = Command(connection, null, "SELECT Path,Size,Modified,NativeId FROM Files WHERE instr(PathKey,$prefix)=1 AND Available=1 AND Category<>'Klasörler'", ("$prefix", Prefix(root)));
        using var reader = command.ExecuteReader();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)) { NativeId = reader.IsDBNull(3) ? null : reader.GetString(3) });
        return entries;
    });

    public Task<int> ApplyChangesAsync(string root, IReadOnlyList<FileEntry> entries, IReadOnlyList<string> removedPaths,
        bool includeFiles = true, bool discoverFolders = false) => RunAsync(connection =>
    {
        root = Path.GetFullPath(root);
        // An unavailable volume is not evidence that all of its files have been deleted.
        if (!Directory.Exists(root)) return 0;
        if (Scalar(connection, null, "SELECT 1 FROM Sources WHERE PathKey=$key", ("$key", CatalogText.PathKey(root))) == null) return 0;
        FileSafety.CheckAncestors(root);
        bool InScope(string path) => CatalogText.PathKey(path) == CatalogText.PathKey(root) || FileSafety.IsWithin(root, path);
        bool OwnMetadata(string path) => CatalogText.PathKey(path) == CatalogText.PathKey(DatabasePath) || FileSafety.IsWithin(Path.GetDirectoryName(DatabasePath)!, path);
        foreach (var path in entries.Select(e => e.FullPath).Concat(removedPaths)) if (!InScope(path)) throw new IOException("İzleme değişikliği konumun dışında.");
        using var transaction = connection.BeginTransaction(); int changed = 0;
        var relocatedMetadata = new HashSet<(long Size, long Modified)>();
        using (var command = Command(connection, transaction, "SELECT f.Path,f.Size,f.Modified FROM Files f WHERE f.NativeId IS NOT NULL AND f.PathKey IS NOT NULL AND (EXISTS(SELECT 1 FROM FileTags WHERE FileId=f.Id) OR EXISTS(SELECT 1 FROM Decisions WHERE Identity=f.Revision))"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) if (IsAbsent(reader.GetString(0))) relocatedMetadata.Add((reader.GetInt64(1), reader.GetInt64(2)));
        // Relocate before processing deletions: the old path must not invalidate the new path of the same item.
        foreach (var file in entries)
        {
            if (OwnMetadata(file.FullPath) || !file.IsDirectory && (!includeFiles || FileScanner.IsTemporary(file.FullPath))) continue;
            FileSafety.CheckAncestors(file.IsDirectory ? file.FullPath : file.Folder);
            FileSystemInfo info = file.IsDirectory ? new DirectoryInfo(file.FullPath) : new FileInfo(file.FullPath); info.Refresh();
            if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0) continue;
            if (info.LastWriteTimeUtc.Ticks != file.ModifiedTicks || info is FileInfo observed && observed.Length != file.Size) continue;
            var item = file;
            if (item.NativeId == null && (item.IsDirectory || relocatedMetadata.Contains((file.Size, file.ModifiedTicks)) ||
                Scalar(connection, transaction, "SELECT NativeId FROM Files WHERE PathKey=$key", ("$key", CatalogText.PathKey(file.FullPath))) is string))
                item = item with { NativeId = FileIdentity.TryRead(file.FullPath) };
            if (item.IsDirectory && !discoverFolders)
            {
                bool registered = Scalar(connection, transaction, "SELECT 1 FROM Files WHERE Category='Klasörler' AND (PathKey=$key OR NativeId=$native) LIMIT 1",
                    ("$key", CatalogText.PathKey(item.FullPath)), ("$native", item.NativeId)) != null;
                if (!registered) continue;
            }
            bool unchanged = Scalar(connection, transaction, "SELECT 1 FROM Files WHERE PathKey=$key AND Revision=$revision AND NativeId IS $native AND Available=1 AND Category=$category",
                ("$key", CatalogText.PathKey(item.FullPath)), ("$revision", item.Identity), ("$native", item.NativeId), ("$category", item.Category)) != null;
            if (unchanged) continue;
            UpsertFile(connection, transaction, item, "watcher"); changed++;
        }
        foreach (string path in removedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (OwnMetadata(path) || !IsAbsent(path)) continue;
            changed += Execute(connection, transaction, "UPDATE Files SET Available=0 WHERE Available=1 AND (PathKey=$key OR instr(PathKey,$prefix)=1) AND ($files=1 OR Category='Klasörler')",
                ("$key", CatalogText.PathKey(path)), ("$prefix", Prefix(path)), ("$files", includeFiles));
        }
        transaction.Commit(); return changed;
    });
}
