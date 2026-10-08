using Microsoft.UI.Xaml.Controls;
using WinIsland.Core.Modules;

namespace WinIsland.App.Views;

public sealed partial class ApprovalView : UserControl
{
    public ApprovalView(ApprovalsModule module)
    {
        Module = module;
        InitializeComponent();
    }

    public ApprovalsModule Module { get; }
}
