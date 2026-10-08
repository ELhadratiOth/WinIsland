using System.Diagnostics;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Helpers;

namespace WinIsland.Platform.Windows.Helpers;

public sealed class ShellLauncher : IShellLauncher
{
    public void Open(string path) => Run(new ProcessStartInfo(path) { UseShellExecute = true });

    public void Reveal(string path) => Run(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });

    private static void Run(ProcessStartInfo info)
    {
        try
        {
            using Process? _ = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Warn(nameof(ShellLauncher), $"Could not open {info.FileName}", ex);
        }
    }
}
