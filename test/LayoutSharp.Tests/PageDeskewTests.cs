using LayoutSharp.Preprocessing;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace LayoutSharp.Tests;

/// <summary>
/// The ImageSharp-only skew estimator and the white-filled rotate, on real scanned fixtures rotated
/// by known angles and on synthetic pages. No model involved.
/// </summary>
public class PageDeskewTests
{
    private readonly ITestOutputHelper _out;

    public PageDeskewTests(ITestOutputHelper output) => _out = output;

    private static Image<Rgb24> LoadAsset(string name) => Image.Load<Rgb24>(IntegrationTests.AssetPath(name));

    internal static void FillRect(Image<Rgb24> img, int x0, int y0, int w, int h, Rgb24 color)
    {
        for (int y = y0; y < y0 + h && y < img.Height; y++)
            for (int x = x0; x < x0 + w && x < img.Width; x++)
                img[x, y] = color;
    }

    /// <summary>A 1000x1400 white page with ~43 rows of black "words" (no native tilt).</summary>
    internal static Image<Rgb24> SyntheticPage()
    {
        var img = new Image<Rgb24>(1000, 1400, Color.White.ToPixel<Rgb24>());
        var rnd = new Random(42);
        int y = 100;
        while (y < 1300)
        {
            int x = 100;
            while (x < 900)
            {
                int wordW = rnd.Next(30, 90);
                int wordH = rnd.Next(8, 14);
                if (x + wordW > 900) break;
                FillRect(img, x, y, wordW, wordH, new Rgb24(0, 0, 0));
                x += wordW + rnd.Next(8, 16);
            }
            y += 28;
        }
        return img;
    }

    [Theory]
    [InlineData("structure_sample.png", 3.0)]
    [InlineData("structure_sample.png", -3.0)]
    [InlineData("structure_sample.png", 7.5)]
    [InlineData("structure_sample.png", -7.5)]
    [InlineData("structure_sample.png", 12.0)]
    [InlineData("Test_image1.png", 3.0)]
    [InlineData("Test_image1.png", -7.5)]
    [InlineData("Test_image1.png", 12.0)]
    public void Estimate_RecoversKnownRotation_WithinHalfDegree_AndCorrectSign(string asset, double degrees)
    {
        using var src = LoadAsset(asset);
        // ImageSharp Rotate(+d) turns the content clockwise by d, so the content skew is +d.
        using var rotated = PageDeskew.Rotate(src, degrees);

        var est = PageDeskew.Estimate(rotated);
        _out.WriteLine($"{asset} rotated {degrees}: estimate {est.Angle:F1} (confidence {est.Confidence:F2}, reliable {est.IsReliable})");

        Assert.InRange(est.Angle, degrees - 0.5, degrees + 0.5);
        Assert.Equal(Math.Sign(degrees), Math.Sign(est.Angle));
        Assert.True(est.IsReliable, $"expected a reliable estimate, got confidence {est.Confidence:F2}");
        Assert.True(est.Confidence >= 0.5);
    }

    [Fact]
    public void Estimate_SmallTilt_IsReportedButUnreliable()
    {
        // 0.4 degrees is below the 0.5-degree gate: reported, but not something the pipeline should act on.
        using var src = LoadAsset("Test_image1.png");
        foreach (var degrees in new[] { 0.4, -0.4 })
        {
            using var rotated = PageDeskew.Rotate(src, degrees);
            var est = PageDeskew.Estimate(rotated);
            _out.WriteLine($"Test_image1 rotated {degrees}: estimate {est.Angle:F1} (confidence {est.Confidence:F2})");
            Assert.InRange(est.Angle, degrees - 0.3, degrees + 0.3);
            Assert.False(est.IsReliable);
        }

        // structure_sample has a native ~-0.2 degree tilt; +0.4 nets ~0.2, also below the gate.
        using var form = LoadAsset("structure_sample.png");
        using var formRotated = PageDeskew.Rotate(form, 0.4);
        var formEst = PageDeskew.Estimate(formRotated);
        _out.WriteLine($"structure_sample rotated 0.4: estimate {formEst.Angle:F1} (confidence {formEst.Confidence:F2})");
        Assert.False(formEst.IsReliable);
        Assert.True(Math.Abs(formEst.Angle) < 0.5);
    }

    [Fact]
    public void Estimate_StraightPages_AreNotReliable()
    {
        foreach (var asset in new[] { "structure_sample.png", "Test_image1.png", "Multiple_Images.png" })
        {
            using var src = LoadAsset(asset);
            var est = PageDeskew.Estimate(src);
            _out.WriteLine($"{asset}: estimate {est.Angle:F1} (confidence {est.Confidence:F2})");
            Assert.False(est.IsReliable, $"{asset}: straight page flagged reliable ({est.Angle:F1}, {est.Confidence:F2})");
            Assert.True(Math.Abs(est.Angle) < 0.5);
        }

        using var synthetic = SyntheticPage();
        var s = PageDeskew.Estimate(synthetic);
        Assert.False(s.IsReliable);
        Assert.True(Math.Abs(s.Angle) < 0.3);
    }

    [Fact]
    public void Estimate_SyntheticPage_KnownAngles()
    {
        using var page = SyntheticPage();
        foreach (var degrees in new[] { 3.0, -7.5, 12.0 })
        {
            using var rotated = PageDeskew.Rotate(page, degrees);
            var est = PageDeskew.Estimate(rotated);
            _out.WriteLine($"synthetic rotated {degrees}: estimate {est.Angle:F1} (confidence {est.Confidence:F2})");
            Assert.InRange(est.Angle, degrees - 0.5, degrees + 0.5);
            Assert.True(est.IsReliable);
        }
    }

    [Fact]
    public void Estimate_BlankAndTinyImages_AreUnreliable_AndDoNotThrow()
    {
        using var blank = new Image<Rgb24>(800, 1000, Color.White.ToPixel<Rgb24>());
        var e = PageDeskew.Estimate(blank);
        Assert.Equal(new SkewEstimate(0, 0, false), e);

        using var black = new Image<Rgb24>(300, 300, Color.Black.ToPixel<Rgb24>());
        var b = PageDeskew.Estimate(black); // no ink/background contrast at all
        Assert.False(b.IsReliable);

        using var tiny = new Image<Rgb24>(40, 40, Color.White.ToPixel<Rgb24>());
        tiny[10, 10] = new Rgb24(0, 0, 0);
        var t = PageDeskew.Estimate(tiny);
        Assert.False(t.IsReliable);

        using var one = new Image<Rgb24>(1, 1);
        Assert.False(PageDeskew.Estimate(one).IsReliable);
    }

    [Fact]
    public void Estimate_ValidatesArguments()
    {
        using var img = new Image<Rgb24>(10, 10);
        Assert.Throws<ArgumentNullException>(() => PageDeskew.Estimate(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageDeskew.Estimate(img, maxAngle: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageDeskew.Estimate(img, maxAngle: 46));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageDeskew.Estimate(img, minAngle: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageDeskew.Estimate(img, minConfidence: -0.1));
    }

    [Fact]
    public void Estimate_HonoursGates()
    {
        using var src = LoadAsset("Test_image1.png");
        using var rotated = PageDeskew.Rotate(src, 3.0);
        var strict = PageDeskew.Estimate(rotated, minAngle: 5.0);
        Assert.False(strict.IsReliable);
        Assert.InRange(strict.Angle, 2.5, 3.5);

        var impossible = PageDeskew.Estimate(rotated, minConfidence: 1e9);
        Assert.False(impossible.IsReliable);

        var narrow = PageDeskew.Estimate(rotated, maxAngle: 1.0); // window excludes the true angle
        Assert.InRange(narrow.Angle, -1.05, 1.05);
    }

    [Fact]
    public void Rotate_ExpandsCanvas_FillsCornersWhite_AndIsUndoneByNegativeAngle()
    {
        using var src = LoadAsset("structure_sample.png");
        using var rotated = PageDeskew.Rotate(src, 3.0);

        Assert.True(rotated.Width > src.Width && rotated.Height > src.Height);
        Assert.Equal(new Rgb24(255, 255, 255), rotated[0, 0]);
        Assert.Equal(new Rgb24(255, 255, 255), rotated[rotated.Width - 1, 0]);
        Assert.Equal(new Rgb24(255, 255, 255), rotated[0, rotated.Height - 1]);
        Assert.Equal(new Rgb24(255, 255, 255), rotated[rotated.Width - 1, rotated.Height - 1]);

        // Straightening by the estimated angle brings the estimate back to ~0.
        var est = PageDeskew.Estimate(rotated);
        using var straight = PageDeskew.Rotate(rotated, -est.Angle);
        var again = PageDeskew.Estimate(straight);
        Assert.True(Math.Abs(again.Angle) <= 0.3, $"residual skew {again.Angle:F1}");
        Assert.False(again.IsReliable);
    }

    [Fact]
    public void Rotate_ContentSurvives_AndSourceIsUntouched()
    {
        using var src = new Image<Rgb24>(200, 100, Color.White.ToPixel<Rgb24>());
        FillRect(src, 90, 40, 20, 20, new Rgb24(0, 0, 0));
        using var rotated = PageDeskew.Rotate(src, 10);

        Assert.Equal(new Rgb24(255, 255, 255), src[0, 0]); // untouched (invert round-trip is on the clone)
        Assert.Equal(new Rgb24(0, 0, 0), src[100, 50]);
        // The black square is still black at the centre of the rotated canvas.
        Assert.Equal(new Rgb24(0, 0, 0), rotated[rotated.Width / 2, rotated.Height / 2]);
        // And its corners are white.
        Assert.Equal(new Rgb24(255, 255, 255), rotated[0, 0]);

        using var zero = PageDeskew.Rotate(src, 0);
        Assert.Equal(src.Width, zero.Width);
        Assert.Equal(src.Height, zero.Height);
        Assert.NotSame(src, zero);

        Assert.Throws<ArgumentNullException>(() => PageDeskew.Rotate(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageDeskew.Rotate(src, double.NaN));
    }
}
