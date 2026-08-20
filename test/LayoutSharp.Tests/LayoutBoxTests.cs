using LayoutSharp.Models;
using Xunit;

namespace LayoutSharp.Tests;

public class LayoutBoxTests
{
    [Fact]
    public void Dimensions_AreComputed()
    {
        var box = new LayoutBox(10, 20, 110, 70);
        Assert.Equal(100, box.Width);
        Assert.Equal(50, box.Height);
        Assert.Equal(5000, box.Area);
        Assert.Equal(60, box.CenterX);
        Assert.Equal(45, box.CenterY);
    }

    [Fact]
    public void IoU_IdenticalBoxes_IsOne()
    {
        var box = new LayoutBox(0, 0, 100, 100);
        Assert.Equal(1.0, box.IntersectionOverUnion(box), precision: 6);
    }

    [Fact]
    public void IoU_DisjointBoxes_IsZero()
    {
        var a = new LayoutBox(0, 0, 50, 50);
        var b = new LayoutBox(100, 100, 150, 150);
        Assert.Equal(0, a.IntersectionOverUnion(b));
    }

    [Fact]
    public void IoU_HalfOverlap_IsOneThird()
    {
        // Two 100×100 boxes overlapping in a 50×100 region: inter=5000, union=15000 → 1/3.
        var a = new LayoutBox(0, 0, 100, 100);
        var b = new LayoutBox(50, 0, 150, 100);
        Assert.Equal(1.0 / 3.0, a.IntersectionOverUnion(b), precision: 6);
    }

    [Fact]
    public void ToPixelRect_ClampsToImageBounds()
    {
        var box = new LayoutBox(-10, -10, 120, 60);
        var (x, y, w, h) = box.ToPixelRect(100, 50);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(100, w);
        Assert.Equal(50, h);
    }
}
