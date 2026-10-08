using WinIsland.Core.Geometry;

namespace WinIsland.Core.Tests;

public sealed class PixelRectTests
{
    [Fact]
    public void Union_covers_both_rectangles()
    {
        var small = new PixelRect(900, 6, 120, 36);
        var large = new PixelRect(700, 6, 520, 200);

        PixelRect union = small.Union(large);

        Assert.Equal(large, union);
        Assert.True(union.Contains(small));
    }

    [Fact]
    public void Union_with_an_empty_rectangle_is_the_other_one()
    {
        var rect = new PixelRect(10, 10, 50, 20);

        Assert.Equal(rect, rect.Union(default));
        Assert.Equal(rect, default(PixelRect).Union(rect));
    }

    [Fact]
    public void Union_of_offset_rectangles_spans_the_gap()
    {
        PixelRect union = new PixelRect(0, 0, 10, 10).Union(new PixelRect(20, 5, 10, 10));

        Assert.Equal(new PixelRect(0, 0, 30, 15), union);
    }
}
