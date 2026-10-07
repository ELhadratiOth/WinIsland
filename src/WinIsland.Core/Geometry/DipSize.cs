namespace WinIsland.Core.Geometry;

/// <summary>A size in device-independent pixels (1/96 inch), the unit XAML layout uses.</summary>
public readonly record struct DipSize(double Width, double Height)
{
    public static DipSize Max(DipSize a, DipSize b) => new(Math.Max(a.Width, b.Width), Math.Max(a.Height, b.Height));

    public override string ToString() => $"{Width}x{Height} dip";
}
