namespace WinIsland.Platform.Windows.Shell;

public sealed record TrayMenuItem(int Id, string Text, bool IsChecked = false, bool IsEnabled = true)
{
    public static readonly TrayMenuItem Separator = new(0, string.Empty) { IsSeparator = true };

    public bool IsSeparator { get; init; }

    public IReadOnlyList<TrayMenuItem>? Children { get; init; }
}
