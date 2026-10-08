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
            return RunHook(hook + 1 < args.Length ? args[hook + 1] : "permission");
        }

        if (args.Contains("--claude-statusline"))
        {
            return RunHook("statusline");
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

    /// <summary>Claude Code speaks UTF-8 on stdin/stdout regardless of the console code page.</summary>
    private static int RunHook(string kind)
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
        return ClaudeHookClient.Run(kind, input, output);
    }
}
