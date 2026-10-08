using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class CalendarView : UserControl
{
    public CalendarView(CalendarModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public CalendarModule Module { get; }
}
