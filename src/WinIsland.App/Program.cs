using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Platform.Windows.Claude;

namespace WinIsland.App;

/// <summary>
/// Entry point (replaces the XAML-generated Main). <c>WinIsland.exe --claude-hook &lt;kind&gt;</c>
/// is Claude Code calling its hook: answer over stdin/stdout without starting any UI.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int hook = Array.IndexOf(args, "--claude-hook");
        if (hook >= 0)
        {
            string kind = hook + 1 < args.Length ? args[hook + 1] : "permission";
            return ClaudeHookClient.Run(kind, Console.In, Console.Out);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
