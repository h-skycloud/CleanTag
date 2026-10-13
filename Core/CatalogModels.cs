using System.Globalization;
using System.Text;

namespace CleanTag.Core;

[Flags]
public enum SourceContent { None = 0, Files = 1, Folders = 2, FolderTree = 4 }

public sealed record CatalogSource(string Id, string Path, string Name, string ScanStatus, SourceContent Content = SourceContent.None)
{
    public override string ToString() => Name;
}

public sealed record CatalogTag(string Id, string Name, string Color, string Group, string Aliases, int Count)
{
    public string DisplayName => string.IsNullOrEmpty(Group) ? Name : $"{Name} · {Group}";
    public override string ToString() => DisplayName;
}

public sealed record CatalogFile(string Id, string Path, long Size, long ModifiedTicks, string? NativeId,
    bool Available, string Decision, IReadOnlyList<CatalogTag> Tags, bool IsFolder = false)
{
    public FileEntry Entry => new(Path, Size, ModifiedTicks) { NativeId = NativeId, IsDirectory = IsFolder };
    public string Name => Entry.Name;
    public string Folder => Entry.Folder;
    public string TypeLabel => Entry.TypeLabel;
    public string SizeLabel => Entry.SizeLabel;
    public string TagsLabel => Tags.Count == 0 ? "Etiketsiz" : string.Join(" · ", Tags.Select(t => t.Name));
    public string StatusLabel => !Available ? "Konumda bulunamadı" : IsFolder ? "Klasör" : Decision switch
    { "keep" => "Tutuldu", "defer" => "Ertelendi", _ => "İncelenmedi" };
}

public sealed record CatalogQuery(string Text = "", string? SourceId = null, string? TagId = null,
    string Category = "", string State = "", int Offset = 0, int Limit = 100, string Sort = "name", bool FoldersOnly = false);
public sealed record CatalogPage(IReadOnlyList<CatalogFile> Files, int Total);
public sealed record TagPair(string FileId, string TagId);
public sealed record TagChange(IReadOnlyList<TagPair> Added, IReadOnlyList<TagPair> Removed);

public static class CatalogText
{
    // Preserve Turkish dotted/dotless pairs. Search keys are explicit instead of SQLite's ASCII NOCASE.
    public static string Key(string text) => text.Normalize(NormalizationForm.FormC)
        .ToLower(CultureInfo.GetCultureInfo("tr-TR")).Trim();
    public static string PathKey(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
}

public interface IReviewHistory
{
    string Root { get; }
    string FilePath { get; }
    Task LoadAsync(string root);
    bool IsReviewed(FileEntry file);
    bool IsDeferred(FileEntry file);
    ReviewDecision? GetDecision(FileEntry file);
    Task RecordAsync(FileEntry file, string action);
    Task ResetAsync();
    Task FlushAsync();
}
