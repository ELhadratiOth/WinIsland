using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class PrivacyView : UserControl
{
    public PrivacyView(PrivacyModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public PrivacyModule Module { get; }
}
