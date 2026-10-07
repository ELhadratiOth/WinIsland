namespace WinIsland.Platform.Windows.Windowing;

/// <summary>A window whose messages can be observed.</summary>
public interface IWindowMessageSource
{
    nint Handle { get; }

    /// <summary>Adds a handler for window messages. Return a value to mark the message handled.</summary>
    void AddHandler(Func<uint, nint, nint, nint?> handler);
}
