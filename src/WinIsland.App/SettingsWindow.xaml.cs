using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinIsland.App.Services;

namespace WinIsland.App;

/// <summary>A regular, activatable settings window (unlike the island, it's meant to take focus).</summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Bindings.Update();

        SystemBackdrop = new MicaBackdrop();
        AppWindow.Title = "WinIsland settings";
        AppWindow.Resize(new SizeInt32(820, 900));
    }

    public SettingsViewModel ViewModel { get; }
}
