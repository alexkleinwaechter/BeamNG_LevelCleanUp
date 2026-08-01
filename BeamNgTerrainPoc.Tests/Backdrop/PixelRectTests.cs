using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class PixelRectTests
{
    [Fact]
    public void ContainsRect_TrueForEqualAndInner_FalseForOverhang()
    {
        var outer = new PixelRect(10, 10, 100, 80);
        Assert.True(outer.ContainsRect(outer));
        Assert.True(outer.ContainsRect(new PixelRect(20, 20, 50, 40)));
        Assert.False(outer.ContainsRect(new PixelRect(5, 20, 50, 40)));    // west overhang
        Assert.False(outer.ContainsRect(new PixelRect(20, 20, 100, 40))); // east overhang
    }

    [Fact]
    public void RightBottom_AreExclusive()
    {
        var r = new PixelRect(3, 4, 10, 20);
        Assert.Equal(13, r.Right);
        Assert.Equal(24, r.Bottom);
    }
}
