using System.Collections.ObjectModel;
using System.ComponentModel;
using CleanTag.Core;
using Microsoft.UI.Xaml.Media;

namespace CleanTag.ViewModels;

public sealed class LibraryItem(CatalogFile file) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    public CatalogFile File { get; private set; } = file;
    public override string ToString() => Name;
    public string Name => File.Name;
    public string Details => $"{File.TypeLabel} · {File.SizeLabel} · {File.StatusLabel}";
    public string Folder => File.Folder;
    public string TagsLabel => File.TagsLabel;
    public string TypeLabel => File.TypeLabel;
    public bool LoadingThumbnail { get; set; }
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(CatalogFile file)
    {
        if (File.Entry.Identity != file.Entry.Identity) { _thumbnail = null; LoadingThumbnail = false; }
        File = file;
        PropertyChanged?.Invoke(this, new(null));
    }
}

public sealed class LibraryViewModel : INotifyPropertyChanged
{
    public ObservableCollection<LibraryItem> Files { get; } = [];
    public int Offset { get; set; }
    public int Total { get; private set; }
    public const int PageSize = 80;
    public void SetPage(CatalogPage page)
    {
        if (Total != page.Total) { Total = page.Total; PropertyChanged?.Invoke(this, new(nameof(Total))); }
        if (Files.Select(item => item.File.Id).SequenceEqual(page.Files.Select(file => file.Id)))
        {
            for (int i = 0; i < Files.Count; i++) Files[i].Update(page.Files[i]);
            return;
        }
        // Keep the ItemsSource itself stable. Replacing it after a multi-selection can
        // invalidate WinUI's gallery selection containers even after clearing selection.
        var wanted = page.Files.Select(file => file.Id).ToHashSet();
        for (int i = Files.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Files[i].File.Id)) Files.RemoveAt(i);
        for (int i = 0; i < page.Files.Count; i++)
        {
            var file = page.Files[i];
            int existing = -1;
            for (int j = i; j < Files.Count; j++)
                if (Files[j].File.Id == file.Id) { existing = j; break; }
            if (existing < 0) Files.Insert(i, new LibraryItem(file));
            else
            {
                if (existing != i) Files.Move(existing, i);
                Files[i].Update(file);
            }
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
