using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class LyricsView : UserControl
{
    public LyricsView(MediaModule module, SolidColorBrush accent)
    {
        Module = module;
        Accent = accent;
        InitializeComponent();
    }

    public MediaModule Module { get; }

    /// <summary>The cover-art tint shared with the player.</summary>
    public SolidColorBrush Accent { get; }
}
