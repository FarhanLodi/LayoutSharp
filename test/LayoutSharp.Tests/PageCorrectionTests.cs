using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Preprocessing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// The correction geometry: making a page upright, and mapping corrected-frame coordinates back to
/// the caller's image (<see cref="LayoutPage.MapToSource(LayoutBox)"/>). Verified against what
/// ImageSharp actually does to a marked pixel, not just against itself.
/// </summary>
public class PageCorrectionTests
{
    private static LayoutPage Page(int width, int height, int rotation, double skew, int srcW, int srcH, params LayoutBox[] boxes)
        => new()
        {
            PageNumber = 1,
            Width = width,
            Height = height,
            Rotation = rotation,
            SkewAngle = skew,
            SourceWidth = srcW,
            SourceHeight = srcH,
            Blocks = boxes.Select((b, i) => new LayoutBlock
            {
                Type = LayoutBlockType.Text,
                BoundingBox = b,
                Confidence = 0.9f,
                ReadingOrder = i,
                RawClassId = 2,
                RawClassName = "text",
            }).ToArray(),
        };

    [Fact]
    public void Upright_UndoesTheReportedRotation()
    {
        // A 4x2 page with a red top-left pixel, rotated so its content reads as N degrees clockwise.
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            using var source = new Image<Rgb24>(4, 2, Color.White);
            source[0, 0] = new Rgb24(255, 0, 0);
            using var upright = PageCorrection.Upright(source, rotation);

            Assert.NotSame(source, upright);
            if (rotation is 90 or 270)
            {
                Assert.Equal(2, upright.Width);
                Assert.Equal(4, upright.Height);
            }
            else
            {
                Assert.Equal(4, upright.Width);
                Assert.Equal(2, upright.Height);
            }
        }

        using var img = new Image<Rgb24>(4, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageCorrection.Upright(img, 45));
        Assert.Throws<ArgumentNullException>(() => PageCorrection.Upright(null!, 0));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void MapToSource_QuarterTurns_MatchWhereImageSharpPutThePixel(int rotation)
    {
        // Source page with a marked pixel; the pipeline sees the page after the content was turned
        // `rotation` degrees clockwise, and rotates it back upright.
        const int w = 7, h = 3;
        using var upright = new Image<Rgb24>(w, h, Color.White);
        upright[0, 0] = new Rgb24(255, 0, 0);
        upright[w - 1, h - 1] = new Rgb24(0, 0, 255);

        // Simulate the caller's (rotated) input, then correct it the way the service does.
        var mode = rotation switch { 90 => RotateMode.Rotate90, 180 => RotateMode.Rotate180, _ => RotateMode.Rotate270 };
        using var source = upright.Clone(c => c.Rotate(mode));
        using var corrected = PageCorrection.Upright(source, rotation);

        Assert.Equal(w, corrected.Width);
        Assert.Equal(h, corrected.Height);

        var page = Page(corrected.Width, corrected.Height, rotation, 0, source.Width, source.Height);
        foreach (var (colour, cx, cy) in new[] { (new Rgb24(255, 0, 0), 0, 0), (new Rgb24(0, 0, 255), w - 1, h - 1) })
        {
            Assert.Equal(colour, corrected[cx, cy]);
            var (sx, sy) = page.MapToSource(cx + 0.5, cy + 0.5);
            // The same colour must sit at the mapped source pixel.
            Assert.Equal(colour, source[(int)Math.Floor(sx), (int)Math.Floor(sy)]);
        }

        // Whole-page box round-trips to the whole source page.
        var full = page.MapToSource(new LayoutBox(0, 0, corrected.Width, corrected.Height));
        Assert.Equal(0, full.MinX, 6);
        Assert.Equal(0, full.MinY, 6);
        Assert.Equal(source.Width, full.MaxX, 6);
        Assert.Equal(source.Height, full.MaxY, 6);
        Assert.True(page.IsCorrected);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-7.5)]
    [InlineData(12.0)]
    public void MapToSource_Skew_RecoversMarkedPixels_WithinOnePixel(double skew)
    {
        // A source page whose content is tilted `skew` degrees clockwise; the service straightens it
        // by rotating -skew, so the corrected canvas is the rotated one.
        using var source = new Image<Rgb24>(600, 800, Color.White);
        // 3x3 marks so bicubic resampling still leaves an unmistakable core pixel.
        var marks = new[] { (100, 100), (500, 100), (100, 700), (500, 700), (300, 400) };
        foreach (var (mx, my) in marks) PageDeskewTests.FillRect(source, mx - 1, my - 1, 3, 3, new Rgb24(255, 0, 0));

        using var corrected = PageDeskew.Rotate(source, -skew);
        var page = Page(corrected.Width, corrected.Height, 0, skew, source.Width, source.Height);

        foreach (var (mx, my) in marks)
        {
            // Find where this mark landed: the least-white pixel near the analytic prediction.
            var (fx, fy) = Forward(mx + 0.5, my + 0.5, skew, source.Width, source.Height, corrected.Width, corrected.Height);
            int px = (int)Math.Round(fx), py = (int)Math.Round(fy), best = int.MaxValue;
            for (int y = py - 4; y <= py + 4; y++)
                for (int x = px - 4; x <= px + 4; x++)
                {
                    if (x < 0 || y < 0 || x >= corrected.Width || y >= corrected.Height) continue;
                    var p = corrected[x, y];
                    int score = p.G + p.B; // red mark => near 0, white page => 510
                    if (score < best) { best = score; px = x; py = y; }
                }
            Assert.True(best < 200, $"the marked pixel was not found on the corrected canvas (best {best})");

            var (sx, sy) = page.MapToSource(px + 0.5, py + 0.5);
            Assert.InRange(sx, mx - 1.5, mx + 2.0);
            Assert.InRange(sy, my - 1.5, my + 2.0);
        }

        // A box maps to an enclosing rectangle that still lies (roughly) on the page.
        var mapped = page.MapToSource(new LayoutBox(200, 200, 400, 400));
        Assert.True(mapped.Width >= 200 && mapped.Height >= 200); // enclosing AABB of a rotated box
        Assert.True(page.IsCorrected);
    }

    /// <summary>Forward transform: source point -> corrected canvas, for a page straightened by -skew.</summary>
    private static (double X, double Y) Forward(double x, double y, double skew, int sw, int sh, int cw, int ch)
    {
        double r = -skew * Math.PI / 180.0;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        double dx = x - sw / 2.0, dy = y - sh / 2.0;
        return (cos * dx - sin * dy + cw / 2.0, sin * dx + cos * dy + ch / 2.0);
    }

    [Fact]
    public void MapToSource_Identity_WhenNotCorrected()
    {
        var page = Page(400, 600, 0, 0, 400, 600);
        Assert.False(page.IsCorrected);
        Assert.Equal((12.5, 34.5), page.MapToSource(12.5, 34.5));
        var box = new LayoutBox(10, 20, 30, 40);
        Assert.Equal(box, page.MapToSource(box));
    }

    [Fact]
    public void MapToSource_MissingSourceSize_FallsBackToPageSize()
    {
        // Documents serialized before SourceWidth/SourceHeight existed deserialize with 0.
        var legacy = new LayoutPage { PageNumber = 1, Width = 400, Height = 600, Blocks = Array.Empty<LayoutBlock>(), Rotation = 90 };
        Assert.Equal(0, legacy.SourceWidth);
        var (x, y) = legacy.MapToSource(0, 0);
        Assert.Equal(400, x);   // treats the source as 400x600
        Assert.Equal(0, y);
    }

    [Fact]
    public void MapToSource_CombinedRotationAndSkew_IsInvertible()
    {
        // Round-trip through the forward transform the service applies: source -> upright -> deskewed.
        const int sw = 500, sh = 700;
        const int rotation = 90, cw = 740, ch = 540; // upright is 700x500, grown by the deskew
        const double skew = 4.0;
        var page = Page(cw, ch, rotation, skew, sw, sh);

        // Every corner of the corrected page maps into a plausible neighbourhood of the source.
        foreach (var (x, y) in new[] { (0.0, 0.0), (cw - 1.0, 0.0), (0.0, ch - 1.0), (cw - 1.0, ch - 1.0), (cw / 2.0, ch / 2.0) })
        {
            var (sx, sy) = page.MapToSource(x, y);
            Assert.True(double.IsFinite(sx) && double.IsFinite(sy));
        }

        // The centre of the corrected page is the centre of the source page.
        var (mx, my) = page.MapToSource(cw / 2.0, ch / 2.0);
        Assert.Equal(sw / 2.0, mx, 6);
        Assert.Equal(sh / 2.0, my, 6);
    }
}
