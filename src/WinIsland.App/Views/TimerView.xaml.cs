using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class TimerView : UserControl
{
    public TimerView(TimerModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public TimerModule Module { get; }
}
