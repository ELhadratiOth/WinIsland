namespace WinIsland.Core.Geometry;

/// <summary>A rectangle in physical (device) pixels, as used by Win32 window and monitor APIs.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public bool IntersectsWith(PixelRect other) =>
        !IsEmpty && !other.IsEmpty &&
        other.X < Right && X < other.Right &&
        other.Y < Bottom && Y < other.Bottom;

    public PixelRect Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + (2 * dx), Height + (2 * dy));

    public PixelRect WithY(int y) => this with { Y = y };

    /// <summary>Moves (never resizes beyond) this rectangle so it lies inside <paramref name="bounds"/>.</summary>
    public PixelRect ClampInside(PixelRect bounds)
    {
        int width = Math.Min(Width, bounds.Width);
        int height = Math.Min(Height, bounds.Height);
        int x = Math.Clamp(X, bounds.X, bounds.Right - width);
        int y = Math.Clamp(Y, bounds.Y, bounds.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}
