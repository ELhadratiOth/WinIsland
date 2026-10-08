using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.ViewModels;

namespace WinIsland.App.Views;

public sealed partial class GenericCompactView : UserControl
{
    public GenericCompactView(IslandViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public IslandViewModel ViewModel { get; }
}
