using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CleanTag.Core;

public sealed partial class CatalogStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly string _connectionString;
    private bool _initialized;
    public string DatabasePath { get; }

    public CatalogStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _gate = Gates.GetOrAdd(DatabasePath, _ => new(1, 1));
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 10 }.ToString();
    }

    // Microsoft.Data.Sqlite executes async I/O synchronously. Dispatch the complete operation off the UI thread.
    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;");
                return operation(connection);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task InitializeAsync(string? legacyPath = null) => RunAsync(connection =>
    {
        if (_initialized) return true;
        long version = Convert.ToInt64(Scalar(connection, null, "PRAGMA user_version;"));
        if (version > 1) throw new IOException("Katalog daha yeni bir CleanTag sürümüyle oluşturulmuş.");
        Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Metadata(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Sources(Id TEXT PRIMARY KEY, Path TEXT NOT NULL, PathKey TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL, ScanStatus TEXT NOT NULL DEFAULT 'Henüz taranmadı');
            CREATE TABLE IF NOT EXISTS Files(Id TEXT PRIMARY KEY, Path TEXT NOT NULL, PathKey TEXT UNIQUE,
                NameKey TEXT NOT NULL, Category TEXT NOT NULL, Size INTEGER NOT NULL, Modified INTEGER NOT NULL,
                NativeId TEXT, Revision TEXT NOT NULL, Available INTEGER NOT NULL, Seen TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Files_Native ON Files(NativeId);
            CREATE TABLE IF NOT EXISTS Tags(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, NameKey TEXT NOT NULL UNIQUE,
                Color TEXT NOT NULL, TagGroup TEXT NOT NULL, Aliases TEXT NOT NULL, SearchKey TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS FileTags(FileId TEXT NOT NULL REFERENCES Files(Id),
                TagId TEXT NOT NULL REFERENCES Tags(Id), PRIMARY KEY(FileId,TagId));
            CREATE INDEX IF NOT EXISTS IX_FileTags_Tag ON FileTags(TagId,FileId);
            CREATE TABLE IF NOT EXISTS Events(Id INTEGER PRIMARY KEY AUTOINCREMENT, Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Decisions(Identity TEXT PRIMARY KEY, PathKey TEXT NOT NULL, Json TEXT NOT NULL, Action TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
        if (Scalar(connection, transaction, "SELECT Value FROM Metadata WHERE Key='legacy-import';") == null)
        {
            if (legacyPath != null)
            {
                string source = File.Exists(legacyPath) ? legacyPath : Path.ChangeExtension(legacyPath, ".jsonl");
                if (File.Exists(source))
                {
                    var events = ReadLegacy(source);
                    string backupFolder = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "backups");
                    Directory.CreateDirectory(backupFolder);
                    File.Copy(source, Path.Combine(backupFolder, $"legacy-{Guid.NewGuid():N}{Path.GetExtension(source)}"));
                    foreach (var entry in events) ApplyDecision(connection, transaction, entry);
                }
            }
            Execute(connection, transaction, "INSERT INTO Metadata VALUES('legacy-import',$value);", ("$value", DateTime.UtcNow.ToString("O")));
        }
        transaction.Commit();
        EnsureSourceContent(connection);
        _initialized = true;
        return true;
    });

    private sealed record LegacyDocument(int Version, List<ReviewDecision> Events);
    private static List<ReviewDecision> ReadLegacy(string source)
    {
        string text = File.ReadAllText(source);
        if (string.IsNullOrWhiteSpace(text)) return [];
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (text.Contains("\"Version\"", StringComparison.OrdinalIgnoreCase))
        {
            var document = JsonSerializer.Deserialize<LegacyDocument>(text, options);
            if (document is not { Version: 1, Events: not null }) throw new JsonException("Karar geçmişinin sürümü desteklenmiyor.");
            return document.Events;
        }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var events = new List<ReviewDecision>();
        for (int i = 0; i < lines.Length; i++)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<ReviewDecision>(lines[i], options);
                if (entry == null || string.IsNullOrEmpty(entry.Root)) throw new JsonException("Geçersiz eski karar.");
                DateTime? modified = null;
                if (long.TryParse(entry.Identity.Split('|').LastOrDefault(), out long ticks) && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks)
                    modified = new DateTime(ticks, DateTimeKind.Utc);
                events.Add(entry with { Name = Path.GetFileName(entry.Path), Folder = Path.GetDirectoryName(entry.Path) ?? "",
                    Extension = Path.GetExtension(entry.Path).ToLowerInvariant(), ModifiedUtc = modified });
            }
            catch (JsonException) when (i == lines.Length - 1 && !lines[i].TrimEnd().EndsWith('}')) { }
        }
        return events;
    }

    public Task<CatalogSource> AddSourceAsync(string path) => RunAsync(connection =>
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        FileSafety.CheckAncestors(path);
        using var transaction = connection.BeginTransaction();
        var source = AddSource(connection, transaction, path);
        transaction.Commit();
        return source;
    });

    private static CatalogSource AddSource(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        string key = CatalogText.PathKey(path);
        string id = Scalar(connection, transaction, "SELECT Id FROM Sources WHERE PathKey=$key", ("$key", key)) as string ?? Guid.NewGuid().ToString("N");
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length == 0) name = path;
        Execute(connection, transaction, "INSERT OR IGNORE INTO Sources(Id,Path,PathKey,Name) VALUES($id,$path,$key,$name)",
            ("$id", id), ("$path", path), ("$key", key), ("$name", name));
        return new(id, path, name, "Henüz taranmadı");
    }

    public Task<IReadOnlyList<CatalogSource>> GetSourcesAsync() => RunAsync<IReadOnlyList<CatalogSource>>(connection =>
    {
        using var command = Command(connection, null, "SELECT s.Id,s.Path,s.Name,s.ScanStatus,CAST(COALESCE(m.Value,'0') AS INTEGER) FROM Sources s LEFT JOIN Metadata m ON m.Key='source-content:'||s.Id ORDER BY s.Name");
        using var reader = command.ExecuteReader();
        var sources = new List<CatalogSource>();
        while (reader.Read()) sources.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), (SourceContent)reader.GetInt32(4)));
        return sources;
    });

    public Task IndexScanAsync(string root, ScanResult result, bool foldersOnly = false) => RunAsync(connection =>
    {
        root = Path.GetFullPath(root);
        using var transaction = connection.BeginTransaction();
        var source = AddSource(connection, transaction, root);
        AddSourceContent(connection, transaction, source.Id, foldersOnly ? SourceContent.Folders | SourceContent.FolderTree : SourceContent.Files);
        string generation = Guid.NewGuid().ToString("N");
        var known = new Dictionary<string, (string Id, string Revision, string? Native, bool Available, bool Protected)>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command(connection, transaction, """
            SELECT f.PathKey,f.Id,f.Revision,f.NativeId,f.Available,
                EXISTS(SELECT 1 FROM FileTags WHERE FileId=f.Id) OR EXISTS(SELECT 1 FROM Decisions WHERE Identity=f.Revision)
            FROM Files f WHERE (instr(f.PathKey,$prefix)=1 OR f.PathKey=$root) AND (f.Category='Klasörler')=$folders
            """, ("$prefix", Prefix(root)), ("$root", CatalogText.PathKey(root)), ("$folders", foldersOnly)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) known[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5));
        // Only unidentified entries that could be relocated tagged/reviewed items need a native identity read.
        var relocatedMetadata = new HashSet<(long Size, long Modified)>();
        using (var command = Command(connection, transaction, """
            SELECT f.Path,f.Size,f.Modified FROM Files f WHERE f.NativeId IS NOT NULL AND f.PathKey IS NOT NULL
            AND (f.Category='Klasörler')=$folders AND
            (EXISTS(SELECT 1 FROM FileTags WHERE FileId=f.Id) OR EXISTS(SELECT 1 FROM Decisions WHERE Identity=f.Revision))
            """, ("$folders", foldersOnly)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (IsAbsent(reader.GetString(0))) relocatedMetadata.Add((reader.GetInt64(1), reader.GetInt64(2)));
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in result.Files)
        {
            if (file.IsDirectory != foldersOnly) throw new IOException("Tarama modu ile kayıt türü uyuşmuyor.");
            if (!FileSafety.IsWithin(root, file.FullPath) && !(foldersOnly && CatalogText.PathKey(root) == CatalogText.PathKey(file.FullPath))) throw new IOException("Tarama sonucu kökün dışında.");
            if (CatalogText.PathKey(file.FullPath) == CatalogText.PathKey(DatabasePath) ||
                FileSafety.IsWithin(Path.GetDirectoryName(DatabasePath)!, file.FullPath)) continue;
            string key = CatalogText.PathKey(file.FullPath); observed.Add(key);
            bool exists = known.TryGetValue(key, out var previous);
            var item = file;
            if (item.NativeId == null && (exists && previous.Protected || relocatedMetadata.Contains((file.Size, file.ModifiedTicks)) || foldersOnly))
                item = item with { NativeId = FileIdentity.TryRead(file.FullPath) };
            else if (item.NativeId == null && exists && previous.Revision == file.Identity)
                item = item with { NativeId = previous.Native };
            if (exists && previous.Available && previous.Revision == item.Identity && previous.Native == item.NativeId)
            { observedIds.Add(previous.Id); continue; }
            observedIds.Add(UpsertFile(connection, transaction, item, generation));
        }
        // An interrupted/inaccessible/skipped scan is not proof that a file was removed.
        bool complete = !result.Cancelled && result.Errors.Count == 0 && result.Skipped == 0;
        if (complete)
            foreach (var (key, item) in known)
                if (item.Available && !observed.Contains(key) && !observedIds.Contains(item.Id)) Execute(connection, transaction, "UPDATE Files SET Available=0 WHERE Id=$id", ("$id", item.Id));
        string status = result.Cancelled ? "Tarama durduruldu" : result.Errors.Count > 0 ? "Kısmen tarandı" : "Tarandı";
        Execute(connection, transaction, "UPDATE Sources SET ScanStatus=$status WHERE Id=$id", ("$status", status), ("$id", source.Id));
        transaction.Commit();
        return true;
    });

    public Task AddFolderAsync(string path) => RunAsync(connection =>
    {
        path = Path.GetFullPath(path); FileSafety.CheckAncestors(path);
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException(path);
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0) throw new IOException("Bu klasör kullanılamıyor.");
        using var transaction = connection.BeginTransaction();
        UpsertFile(connection, transaction, new(path, 0, info.LastWriteTimeUtc.Ticks) { IsDirectory = true, NativeId = FileIdentity.TryRead(path) }, "direct-folder");
        string? sourceId = Scalar(connection, transaction, "SELECT Id FROM Sources WHERE PathKey=$key OR instr($key,PathKey||$separator)=1 ORDER BY length(PathKey) DESC LIMIT 1",
            ("$key", CatalogText.PathKey(path)), ("$separator", Path.DirectorySeparatorChar.ToString())) as string;
        if (sourceId != null) AddSourceContent(connection, transaction, sourceId, SourceContent.Folders);
        transaction.Commit(); return true;
    });

    // Refresh registered folders only. Discovering descendants is a separate, explicit operation.
    public Task RefreshFoldersAsync(string? root = null) => RunAsync(connection =>
    {
        var paths = new List<(string Id, string Path)>();
        using (var command = Command(connection, null, "SELECT Id,Path FROM Files WHERE Category='Klasörler' AND PathKey IS NOT NULL"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) paths.Add((reader.GetString(0), reader.GetString(1)));
        using var transaction = connection.BeginTransaction();
        foreach (var item in paths)
        {
            if (root != null && CatalogText.PathKey(root) != CatalogText.PathKey(item.Path) && !FileSafety.IsWithin(root, item.Path)) continue;
            try
            {
                FileSafety.CheckAncestors(item.Path);
                var info = new DirectoryInfo(item.Path); info.Refresh();
                if (!info.Exists) throw new DirectoryNotFoundException();
                if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0) continue;
                UpsertFile(connection, transaction, new(item.Path, 0, info.LastWriteTimeUtc.Ticks) { IsDirectory = true, NativeId = FileIdentity.TryRead(item.Path) }, "folder-refresh");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { Execute(connection, transaction, "UPDATE Files SET Available=0 WHERE Id=$id", ("$id", item.Id)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Unknown availability is not absence. */ }
        }
        transaction.Commit(); return true;
    });

    private static string UpsertFile(SqliteConnection connection, SqliteTransaction transaction, FileEntry file, string seen)
    {
        string key = CatalogText.PathKey(file.FullPath);
        string? id = null, oldNative = null, oldRevision = null;
        using (var command = Command(connection, transaction, "SELECT Id,NativeId,Revision FROM Files WHERE PathKey=$key", ("$key", key)))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { id = reader.GetString(0); oldNative = reader.IsDBNull(1) ? null : reader.GetString(1); oldRevision = reader.GetString(2); }
        if (id != null && ((oldNative != null && file.NativeId != null && oldNative != file.NativeId) ||
            ((oldNative == null || file.NativeId == null) && oldRevision != file.Identity)))
        {
            // Keep the old tagged record for audit, but do not attach it to an unrelated replacement.
            var decisionJson = Scalar(connection, transaction, "SELECT Json FROM Decisions WHERE Identity=$identity", ("$identity", oldRevision)) as string;
            if (decisionJson != null)
            {
                var oldDecision = JsonSerializer.Deserialize<ReviewDecision>(decisionJson)!;
                ApplyDecision(connection, transaction, oldDecision with { Action = "revisit", At = DateTime.UtcNow, SessionId = "catalog-observation" });
            }
            Execute(connection, transaction, "UPDATE Files SET PathKey=NULL,Available=0 WHERE Id=$id", ("$id", id));
            id = null;
        }
        if (id == null && file.NativeId != null)
        {
            var candidates = new List<(string Id, string Path, long Size, long Modified, string Revision)>();
            using (var command = Command(connection, transaction, "SELECT Id,Path,Size,Modified,Revision FROM Files WHERE NativeId=$native AND PathKey IS NOT NULL",
                ("$native", file.NativeId)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)));
            // Relink only when exactly one previous path is conclusively absent. Never merge hard links.
            if (candidates.Count == 1 && candidates[0].Size == file.Size && candidates[0].Modified == file.ModifiedTicks && IsAbsent(candidates[0].Path))
            {
                id = candidates[0].Id;
                var json = Scalar(connection, transaction, "SELECT Json FROM Decisions WHERE Identity=$identity", ("$identity", candidates[0].Revision)) as string;
                if (json != null)
                {
                    var decision = JsonSerializer.Deserialize<ReviewDecision>(json)!;
                    if (decision.Action is "keep" or "defer")
                    {
                        var relocated = decision with { Identity = file.Identity, Path = file.FullPath, Name = file.Name, Folder = file.Folder, Extension = file.Extension };
                        Execute(connection, transaction, "INSERT OR REPLACE INTO Decisions VALUES($identity,$path,$json,$action)",
                            ("$identity", relocated.Identity), ("$path", key), ("$json", JsonSerializer.Serialize(relocated)), ("$action", relocated.Action));
                    }
                }
            }
        }
        id ??= Guid.NewGuid().ToString("N");
        Execute(connection, transaction, """
            INSERT INTO Files(Id,Path,PathKey,NameKey,Category,Size,Modified,NativeId,Revision,Available,Seen)
            VALUES($id,$path,$key,$name,$category,$size,$modified,$native,$revision,1,$seen)
            ON CONFLICT(Id) DO UPDATE SET Path=excluded.Path,PathKey=excluded.PathKey,NameKey=excluded.NameKey,
                Category=excluded.Category,Size=excluded.Size,Modified=excluded.Modified,NativeId=excluded.NativeId,
                Revision=excluded.Revision,Available=1,Seen=excluded.Seen
            """, ("$id", id), ("$path", file.FullPath), ("$key", key), ("$name", CatalogText.Key(file.FullPath)),
            ("$category", file.Category), ("$size", file.Size), ("$modified", file.ModifiedTicks),
            ("$native", file.NativeId), ("$revision", file.Identity), ("$seen", seen));
        return id;
    }

    private static bool IsAbsent(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    public Task<string?> FindFileIdAsync(FileEntry file) => RunAsync(connection => Scalar(connection, null,
        "SELECT Id FROM Files WHERE PathKey=$key AND Revision=$revision", ("$key", CatalogText.PathKey(file.FullPath)), ("$revision", file.Identity)) as string);

    public Task<CatalogPage> SearchAsync(CatalogQuery query) => RunAsync<CatalogPage>(connection =>
    {
        if (query.Offset < 0 || query.Limit < 1 || query.Limit > 200) throw new ArgumentOutOfRangeException(nameof(query));
        var parameters = new List<(string, object?)>();
        var conditions = new List<string> { "f.PathKey IS NOT NULL", query.FoldersOnly ? "f.Category='Klasörler'" : "f.Category<>'Klasörler'" };
        foreach (string term in CatalogText.Key(query.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string parameter = "$q" + parameters.Count;
            parameters.Add((parameter, term));
            conditions.Add($"(instr(f.NameKey,{parameter})>0 OR EXISTS(SELECT 1 FROM FileTags ft JOIN Tags t ON t.Id=ft.TagId WHERE ft.FileId=f.Id AND instr(t.SearchKey,{parameter})>0))");
        }
        if (!string.IsNullOrEmpty(query.SourceId))
        {
            var path = Scalar(connection, null, "SELECT Path FROM Sources WHERE Id=$id", ("$id", query.SourceId)) as string;
            if (path == null) return new([], 0);
            conditions.Add("(instr(f.PathKey,$prefix)=1 OR f.PathKey=$root)"); parameters.Add(("$prefix", Prefix(path))); parameters.Add(("$root", CatalogText.PathKey(path)));
        }
        if (!string.IsNullOrEmpty(query.TagId))
        { conditions.Add("EXISTS(SELECT 1 FROM FileTags WHERE FileId=f.Id AND TagId=$tag)"); parameters.Add(("$tag", query.TagId)); }
        if (!string.IsNullOrEmpty(query.Category)) { conditions.Add("f.Category=$category"); parameters.Add(("$category", query.Category)); }
        switch (query.State)
        {
            case "keep": case "defer": conditions.Add("f.Available=1 AND d.Action=$state"); parameters.Add(("$state", query.State)); break;
            case "pending": conditions.Add("f.Available=1 AND (d.Action IS NULL OR d.Action NOT IN ('keep','defer','recycle','permanent','missing'))"); break;
            case "untagged": conditions.Add("NOT EXISTS(SELECT 1 FROM FileTags WHERE FileId=f.Id)"); break;
            case "missing": conditions.Add("f.Available=0"); break;
            default: conditions.Add("f.Available=1"); break;
        }
        string where = string.Join(" AND ", conditions);
        string from = "FROM Files f LEFT JOIN Decisions d ON d.Identity=f.Revision";
        int count = Convert.ToInt32(Scalar(connection, null, $"SELECT COUNT(*) {from} WHERE {where}", parameters.ToArray()));
        string order = query.Sort switch { "size" => "f.Size DESC,f.NameKey,f.Id", "newest" => "f.Modified DESC,f.Id", _ => "f.NameKey,f.Id" };
        parameters.Add(("$limit", query.Limit)); parameters.Add(("$offset", query.Offset));
        var files = new List<CatalogFile>();
        using (var command = Command(connection, null, $"SELECT f.Id,f.Path,f.Size,f.Modified,f.NativeId,f.Available,COALESCE(d.Action,''),f.Category='Klasörler' {from} WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $offset", parameters.ToArray()))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) files.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetString(6), [], reader.GetBoolean(7)));
        for (int i = 0; i < files.Count; i++) files[i] = files[i] with { Tags = GetFileTags(connection, files[i].Id) };
        return new(files, count);
    });

    public Task<IReadOnlyList<CatalogTag>> GetTagsAsync() => RunAsync<IReadOnlyList<CatalogTag>>(connection =>
    {
        using var command = Command(connection, null, "SELECT t.Id,t.Name,t.Color,t.TagGroup,t.Aliases,COUNT(ft.FileId) FROM Tags t LEFT JOIN FileTags ft ON ft.TagId=t.Id GROUP BY t.Id ORDER BY t.NameKey");
        using var reader = command.ExecuteReader();
        var tags = new List<CatalogTag>();
        while (reader.Read()) tags.Add(ReadTag(reader));
        return tags;
    });

    private static CatalogTag ReadTag(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5));
    private static List<CatalogTag> GetFileTags(SqliteConnection connection, string fileId)
    {
        using var command = Command(connection, null, "SELECT t.Id,t.Name,t.Color,t.TagGroup,t.Aliases,0 FROM Tags t JOIN FileTags ft ON ft.TagId=t.Id WHERE ft.FileId=$id ORDER BY t.NameKey", ("$id", fileId));
        using var reader = command.ExecuteReader();
        var tags = new List<CatalogTag>();
        while (reader.Read()) tags.Add(ReadTag(reader));
        return tags;
    }

    public Task<IReadOnlyList<CatalogTag>> GetFileTagsAsync(string fileId) => RunAsync<IReadOnlyList<CatalogTag>>(connection => GetFileTags(connection, fileId));

    public Task<CatalogTag> SaveTagAsync(string name, string group = "", string aliases = "", string color = "Yeşil") => RunAsync<CatalogTag>(connection =>
    {
        name = name.Normalize().Trim(); group = group.Trim(); aliases = aliases.Trim();
        if (name.Length is < 1 or > 80 || group.Length > 80 || aliases.Length > 500) throw new ArgumentException("Etiket adı 1–80 karakter olmalı.");
        using var transaction = connection.BeginTransaction();
        string key = CatalogText.Key(name);
        string id = Scalar(connection, transaction, "SELECT Id FROM Tags WHERE NameKey=$key", ("$key", key)) as string ?? Guid.NewGuid().ToString("N");
        Execute(connection, transaction, """
            INSERT INTO Tags(Id,Name,NameKey,Color,TagGroup,Aliases,SearchKey) VALUES($id,$name,$key,$color,$group,$aliases,$search)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Color=excluded.Color,TagGroup=excluded.TagGroup,Aliases=excluded.Aliases,SearchKey=excluded.SearchKey
            """, ("$id", id), ("$name", name), ("$key", key), ("$color", color), ("$group", group),
            ("$aliases", aliases), ("$search", CatalogText.Key(name + " " + aliases)));
        transaction.Commit();
        return new(id, name, color, group, aliases, 0);
    });

    public Task<TagChange> ChangeTagsAsync(IEnumerable<string> fileIds, IEnumerable<string> tagIds, bool add) => RunAsync<TagChange>(connection =>
    {
        var files = fileIds.Distinct().ToArray(); var tags = tagIds.Distinct().ToArray();
        using var transaction = connection.BeginTransaction();
        foreach (string file in files)
            if (Scalar(connection, transaction, "SELECT 1 FROM Files WHERE Id=$id", ("$id", file)) == null) throw new IOException("Dosya katalogda bulunamadı.");
        foreach (string tag in tags)
            if (Scalar(connection, transaction, "SELECT 1 FROM Tags WHERE Id=$id", ("$id", tag)) == null) throw new IOException("Etiket bulunamadı.");
        if (add && tags.Length > 0)
            foreach (string id in files) CaptureIdentity(connection, transaction, id);
        var changed = new List<TagPair>();
        foreach (string file in files)
        foreach (string tag in tags)
        {
            int count = Execute(connection, transaction, add ? "INSERT OR IGNORE INTO FileTags VALUES($file,$tag)" : "DELETE FROM FileTags WHERE FileId=$file AND TagId=$tag",
                ("$file", file), ("$tag", tag));
            if (count > 0) changed.Add(new(file, tag));
        }
        transaction.Commit();
        return add ? new(changed, []) : new([], changed);
    });

    private static void CaptureIdentity(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        string path; string? native; long size, modified; bool folder;
        using (var command = Command(connection, transaction, "SELECT Path,NativeId,Size,Modified,Category='Klasörler' FROM Files WHERE Id=$id", ("$id", id)))
        using (var reader = command.ExecuteReader())
        { if (!reader.Read()) return; path = reader.GetString(0); native = reader.IsDBNull(1) ? null : reader.GetString(1); size = reader.GetInt64(2); modified = reader.GetInt64(3); folder = reader.GetBoolean(4); }
        if (IsAbsent(path)) return;
        string? current = FileIdentity.TryRead(path);
        var info = new FileInfo(path);
        if (native != null && current != null && native != current || !folder && (info.Length != size || info.LastWriteTimeUtc.Ticks != modified))
            throw new IOException("Seçili öğe değişti. Yenile'den sonra yeniden seçin.");
        if (current != null) Execute(connection, transaction, "UPDATE Files SET NativeId=$native WHERE Id=$id", ("$native", current), ("$id", id));
    }

    public Task UndoTagsAsync(TagChange change) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var pair in change.Added) Execute(connection, transaction, "DELETE FROM FileTags WHERE FileId=$file AND TagId=$tag", ("$file", pair.FileId), ("$tag", pair.TagId));
        foreach (var pair in change.Removed) Execute(connection, transaction, "INSERT OR IGNORE INTO FileTags VALUES($file,$tag)", ("$file", pair.FileId), ("$tag", pair.TagId));
        transaction.Commit();
        return true;
    });

    internal Task<Dictionary<string, ReviewDecision>> LoadDecisionsAsync() => RunAsync(connection =>
    {
        var decisions = new Dictionary<string, ReviewDecision>(StringComparer.OrdinalIgnoreCase);
        using var command = Command(connection, null, "SELECT Json FROM Decisions");
        using var reader = command.ExecuteReader();
        while (reader.Read()) { var entry = JsonSerializer.Deserialize<ReviewDecision>(reader.GetString(0))!; decisions[entry.Identity] = entry; }
        return decisions;
    });

    internal Task RecordDecisionAsync(ReviewDecision entry, FileEntry? file = null) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        if (file != null)
        {
            string? id = Scalar(connection, transaction, "SELECT Id FROM Files WHERE PathKey=$key AND Revision=$revision",
                ("$key", CatalogText.PathKey(file.FullPath)), ("$revision", file.Identity)) as string;
            if (id == null && entry.Action is not "recycle" and not "permanent" and not "missing") id = UpsertFile(connection, transaction, file, Guid.NewGuid().ToString("N"));
            if (id != null && entry.Action is "keep" or "defer") CaptureIdentity(connection, transaction, id);
            if (id != null && entry.Action is "recycle" or "permanent" or "missing")
                Execute(connection, transaction, "UPDATE Files SET Available=0 WHERE Id=$id", ("$id", id));
        }
        ApplyDecision(connection, transaction, entry);
        transaction.Commit();
        return true;
    });

    private static void ApplyDecision(SqliteConnection connection, SqliteTransaction transaction, ReviewDecision entry)
    {
        string json = JsonSerializer.Serialize(entry);
        Execute(connection, transaction, "INSERT INTO Events(Json) VALUES($json)", ("$json", json));
        if (entry.Action == "reset") Execute(connection, transaction, "DELETE FROM Decisions WHERE instr(PathKey,$prefix)=1", ("$prefix", Prefix(entry.Root)));
        else if (entry.Action == "revisit") Execute(connection, transaction, "DELETE FROM Decisions WHERE Identity=$identity", ("$identity", entry.Identity));
        else if (entry.Action is "keep" or "defer" or "recycle" or "permanent" or "missing")
            Execute(connection, transaction, "INSERT INTO Decisions VALUES($identity,$path,$json,$action) ON CONFLICT(Identity) DO UPDATE SET PathKey=excluded.PathKey,Json=excluded.Json,Action=excluded.Action",
                ("$identity", entry.Identity), ("$path", CatalogText.PathKey(entry.Path)), ("$json", json), ("$action", entry.Action));
    }

    public Task ExportHistoryAsync(string destination) => RunAsync(connection =>
    {
        if (CatalogText.PathKey(destination) == CatalogText.PathKey(DatabasePath)) throw new IOException("Katalog dosyası dışa aktarma hedefi olamaz.");
        var entries = new List<ReviewDecision>();
        using (var command = Command(connection, null, "SELECT Json FROM Events ORDER BY Id"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) entries.Add(JsonSerializer.Deserialize<ReviewDecision>(reader.GetString(0))!);
        WriteJsonAtomic(destination, new LegacyDocument(1, entries));
        return true;
    });

    public Task ExportCatalogAsync(string destination) => RunAsync(connection =>
    {
        if (CatalogText.PathKey(destination) == CatalogText.PathKey(DatabasePath)) throw new IOException("Katalog dosyası dışa aktarma hedefi olamaz.");
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (string table in new[] { "Metadata", "Sources", "Files", "Tags", "FileTags", "Decisions", "Events" })
        {
            using var command = Command(connection, null, "SELECT * FROM " + table);
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            tables[table] = rows;
        }
        WriteJsonAtomic(destination, new { Version = 1, ExportedUtc = DateTime.UtcNow, Tables = tables });
        return true;
    });

    private static void WriteJsonAtomic<T>(string destination, T value)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task BackupAsync(string destination) => RunAsync(connection =>
    {
        if (CatalogText.PathKey(destination) == CatalogText.PathKey(DatabasePath) || File.Exists(destination)) throw new IOException("Yedek hedefi yeni bir dosya olmalı.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        backup.Open(); connection.BackupDatabase(backup);
        return true;
    });

    public Task RestoreAsync(string backupPath) => RunAsync(connection =>
    {
        if (CatalogText.PathKey(backupPath) == CatalogText.PathKey(DatabasePath)) throw new IOException("Katalog kendisinden geri yüklenemez.");
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        backup.Open();
        if (Convert.ToInt64(Scalar(backup, null, "PRAGMA user_version")) != 1 || !Equals(Scalar(backup, null, "PRAGMA integrity_check"), "ok")) throw new IOException("Geçerli bir CleanTag yedeği değil.");
        foreach (string table in new[] { "Metadata", "Sources", "Files", "Tags", "FileTags", "Events", "Decisions" })
            if (Scalar(backup, null, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name", ("$name", table)) == null) throw new IOException("Yedek şeması eksik.");
        string recovery = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "backups", $"before-restore-{Guid.NewGuid():N}.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = recovery, Pooling = false }.ToString()))
        { target.Open(); connection.BackupDatabase(target); }
        backup.BackupDatabase(connection);
        EnsureSourceContent(connection);
        return true;
    });

    public async Task FlushAsync() { await _gate.WaitAsync().ConfigureAwait(false); _gate.Release(); }
    private static string Prefix(string root) => CatalogText.PathKey(root) + Path.DirectorySeparatorChar;

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }
}
