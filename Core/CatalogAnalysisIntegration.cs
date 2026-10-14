namespace CleanTag.Core;

public sealed partial class CatalogStore
{
    public Task<string?> FindAnalysisItemIdAsync(FileEntry entry) => entry.IsDirectory
        ? RunAsync(connection => Scalar(connection, null,
            "SELECT Id FROM Files WHERE PathKey=$key AND Category='Klasörler' AND Available=1 AND (NativeId IS NULL OR NativeId=$native)",
            ("$key", CatalogText.PathKey(entry.FullPath)), ("$native", entry.NativeId)) as string)
        : RunAsync(connection => Scalar(connection, null,
            "SELECT Id FROM Files WHERE PathKey=$key AND Revision=$revision AND Available=1 AND (NativeId IS NULL OR NativeId=$native)",
            ("$key",CatalogText.PathKey(entry.FullPath)),("$revision",entry.Identity),("$native",entry.NativeId)) as string);
    // Only an explicit tag action registers this one item. No Source or descendant scan is added.
    public Task<string> RegisterAnalysisEntryAsync(FileEntry entry) => RunAsync(connection =>
    {
        FileSafety.CheckAncestors(entry.IsDirectory ? entry.FullPath : entry.Folder);
        if (entry.IsDirectory)
        {
            var info = new DirectoryInfo(entry.FullPath);
            if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0) throw new IOException("Klasör etiketleme için kullanılamıyor.");
            string? native = FileIdentity.TryRead(entry.FullPath);
            if (entry.NativeId != null && native != entry.NativeId) throw new IOException("Klasör kimliği değişti. Yeniden analiz edin.");
            entry = entry with { Size = 0, ModifiedTicks = info.LastWriteTimeUtc.Ticks, NativeId = native };
        }
        else FileSafety.Validate(entry.Folder, entry);
        using var transaction = connection.BeginTransaction();
        string id = UpsertFile(connection, transaction, entry, "analysis-explicit-tag"); transaction.Commit(); return id;
    });
}
