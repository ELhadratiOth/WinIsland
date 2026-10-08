using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class DownloadsView : UserControl
{
    public DownloadsView(DownloadsModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public DownloadsModule Module { get; }

    private void OnOpenClick(object sender, RoutedEventArgs e) =>
        Module.Open((sender as FrameworkElement)?.Tag as CompletedItem);

    private void OnRevealClick(object sender, RoutedEventArgs e) =>
        Module.Reveal((sender as FrameworkElement)?.Tag as CompletedItem);
}
