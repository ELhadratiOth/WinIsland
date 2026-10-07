using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace WinIsland.App;

/// <summary>Static functions used from x:Bind.</summary>
internal static class Converters
{
    public static ImageSource? ToImage(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        var bitmap = new BitmapImage { DecodePixelWidth = 160 };
        using var stream = new MemoryStream(data, writable: false);
        bitmap.SetSource(stream.AsRandomAccessStream());
        return bitmap;
    }

    public static string Text(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static SolidColorBrush ToBrush(uint argb) => new(ToColor(argb));

    public static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Color Transparent => Colors.Transparent;
}
