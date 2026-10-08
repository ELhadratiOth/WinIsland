using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class ClipboardView : UserControl
{
    public ClipboardView(ClipboardModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public ClipboardModule Module { get; }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClipboardItem item)
        {
            Module.Paste(item);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardItem item })
        {
            Module.Remove(item);
        }
    }
}
