using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class CiView : UserControl
{
    public CiView(CiModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public CiModule Module { get; }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RunItem item)
        {
            Module.Open(item);
        }
    }
}
