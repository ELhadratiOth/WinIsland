using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class ControlsView : UserControl
{
    public ControlsView(ControlsModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public ControlsModule Module { get; }
}
