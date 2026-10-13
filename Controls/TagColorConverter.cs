using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CleanTag.Controls;

public sealed class TagColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => new SolidColorBrush((value as string) switch
    {
        "Mavi" => Color.FromArgb(255, 66, 153, 225),
        "Mor" => Color.FromArgb(255, 167, 139, 250),
        "Turuncu" => Color.FromArgb(255, 234, 153, 65),
        "Gri" => Color.FromArgb(255, 145, 155, 165),
        _ => Color.FromArgb(255, 65, 185, 150)
    });
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
