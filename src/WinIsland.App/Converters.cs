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

    public static string Number(double value) => Math.Round(value).ToString(System.Globalization.CultureInfo.CurrentCulture);

    public static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static SolidColorBrush ToBrush(uint argb) => new(ToColor(argb));

    /// <summary>The same colour at 15% opacity (status badges).</summary>
    public static SolidColorBrush ToSoftBrush(uint argb) => new(ToColor((argb & 0x00FFFFFF) | 0x26000000));

    private static readonly SolidColorBrush AgendaNext = new(Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush AgendaOther = new(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C));

    public static SolidColorBrush AgendaBar(bool isNext) => isNext ? AgendaNext : AgendaOther;

    public static string Upper(string? text) => (text ?? string.Empty).ToUpperInvariant();

    public static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Dims a control the current player doesn't support instead of hiding it.</summary>
    public static double EnabledOpacity(bool enabled) => enabled ? 1.0 : 0.3;

    private static readonly SolidColorBrush SelectedChip = new(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF7));
    private static readonly SolidColorBrush NormalChip = new(Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush SelectedChipText = new(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0C));
    private static readonly SolidColorBrush NormalChipText = new(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF7));

    /// <summary>Background of a chip/tile that reads as "on" (light) or "off" (raised dark).</summary>
    public static SolidColorBrush ChipBackground(bool selected) => selected ? SelectedChip : NormalChip;

    public static SolidColorBrush ChipForeground(bool selected) => selected ? SelectedChipText : NormalChipText;

    public static Color Transparent => Colors.Transparent;
}
