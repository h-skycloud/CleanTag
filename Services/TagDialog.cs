using CleanTag.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanTag.Services;

public static class TagDialog
{
    public static async Task<TagChange?> ShowAsync(XamlRoot root, IReadOnlyList<string> fileIds, bool add, string itemLabel = "dosya")
    {
        var tags = (await AppWorkspace.Catalog.GetTagsAsync()).ToList();
        bool createOnly = fileIds.Count == 0;
        if (!add)
        {
            var assigned = (await Task.WhenAll(fileIds.Select(AppWorkspace.Catalog.GetFileTagsAsync))).SelectMany(t => t).Select(t => t.Id).ToHashSet();
            tags = tags.Where(t => assigned.Contains(t.Id)).ToList();
        }
        var list = new ListView { ItemsSource = tags, SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 220, MinHeight = 90 };
        var name = new AutoSuggestBox { Header = "Yeni etiket", PlaceholderText = "Örn. Cephe" };
        name.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                name.ItemsSource = tags.Where(t => CatalogText.Key(t.Name).Contains(CatalogText.Key(name.Text))).Select(t => t.Name).Take(8).ToList();
        };
        name.SuggestionChosen += (_, args) => name.Text = args.SelectedItem as string ?? "";
        var group = new TextBox { Header = "Grup (isteğe bağlı)", PlaceholderText = "Örn. Proje veya Konu" };
        var aliases = new TextBox { Header = "Alternatif adlar (isteğe bağlı)", PlaceholderText = "Örn. facade, dış cephe" };
        var color = new ComboBox { Header = "Renk", ItemsSource = new[] { "Yeşil", "Mavi", "Mor", "Turuncu", "Gri" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var panel = new StackPanel { Spacing = 12, Width = 390 };
        panel.Children.Add(new TextBlock { Text = createOnly ? "Kütüphanede kullanmak için yeni bir etiket oluşturun." : !add && tags.Count == 0 ? "Seçili öğelerde kaldırılacak etiket yok." : $"{fileIds.Count} {itemLabel} · Birden fazla etiket seçebilirsiniz.", TextWrapping = TextWrapping.Wrap });
        if (tags.Count > 0 && !createOnly) panel.Children.Add(list);
        if (add)
        {
            panel.Children.Add(name);
            var options = new StackPanel { Spacing = 12 };
            options.Children.Add(group); options.Children.Add(aliases); options.Children.Add(color);
            panel.Children.Add(new Expander { Header = "Renk, grup ve alternatif adlar", Content = options, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = root, Title = createOnly ? "Yeni etiket" : add ? "Etiket ekle" : "Öğeden etiket kaldır", Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = createOnly ? "Oluştur" : add ? "Ekle" : "Kaldır", IsPrimaryButtonEnabled = add || tags.Count > 0, CloseButtonText = "Vazgeç", DefaultButton = ContentDialogButton.Primary };
        TagChange? result = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var selected = list.SelectedItems.Cast<CatalogTag>().Select(t => t.Id).ToList();
                if (add && !string.IsNullOrWhiteSpace(name.Text))
                {
                    var existing = tags.FirstOrDefault(t => CatalogText.Key(t.Name) == CatalogText.Key(name.Text));
                    if (createOnly && existing != null) { error.Text = "Bu etiket zaten var."; args.Cancel = true; return; }
                    var tag = existing ?? await AppWorkspace.Catalog.SaveTagAsync(name.Text, group.Text, aliases.Text, color.SelectedItem as string ?? "Yeşil");
                    selected.Add(tag.Id);
                }
                if (selected.Count == 0) { error.Text = "Bir etiket seçin veya yeni etiket yazın."; args.Cancel = true; return; }
                result = createOnly ? new TagChange([], []) : await AppWorkspace.Catalog.ChangeTagsAsync(fileIds, selected, add);
            }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
        return result;
    }
}
