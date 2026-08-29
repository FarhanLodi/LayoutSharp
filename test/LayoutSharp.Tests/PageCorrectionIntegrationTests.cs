using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Preprocessing;
using LayoutSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;
using Xunit.Abstractions;

namespace LayoutSharp.Tests;

/// <summary>
/// Model-backed page-correction tests: the real PP-LCNet document-orientation classifier
/// (<c>PP-LCNet_x1_0_doc_ori.onnx</c>, 6.7 MB, downloaded and checksum-verified on first use) on
/// rotated copies of the test fixtures, and the correction stage of the pipeline end to end.
/// Excluded from CI like the other integration tests.
/// </summary>
/// <remarks>
/// These deliberately do not depend on any layout <i>detector</i> model: the pipeline runs over a
/// recording detector through the internal test seam, so the assertions are about the correction
/// (rotation, skew, canvas size, coordinate mapping) and stay valid whichever detector ships.
/// </remarks>
[Trait("Category", "Integration")]
public class PageCorrectionIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public PageCorrectionIntegrationTests(ITestOutputHelper output) => _out = output;

    private static Image<Rgb24> Asset(string name) => Image.Load<Rgb24>(IntegrationTests.AssetPath(name));

    /// <summary>Turns a page so its content reads as <paramref name="rotation"/> degrees clockwise.</summary>
    private static Image<Rgb24> TurnClockwise(Image<Rgb24> page, int rotation) => rotation switch
    {
        0 => page.Clone(),
        90 => page.Clone(c => c.Rotate(RotateMode.Rotate90)),
        180 => page.Clone(c => c.Rotate(RotateMode.Rotate180)),
        _ => page.Clone(c => c.Rotate(RotateMode.Rotate270)),
    };

    /// <summary>Records the size of every image the pipeline detected on; returns one fixed box.</summary>
    private sealed class RecordingDetector : ILayoutDetector
    {
        public List<(int Width, int Height)> Seen { get; } = new();
        public LayoutModel Model => LayoutModel.DoclingLayoutHeron;
        public bool IsGpu => false;
        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RawDetection>> DetectAsync(Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken)
        {
            lock (Seen) Seen.Add((image.Width, image.Height));
            var cls = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron).Classes.First(c => c.Name == "text");
            var box = new LayoutBox(image.Width * 0.1, image.Height * 0.1, image.Width * 0.6, image.Height * 0.3);
            return Task.FromResult<IReadOnlyList<RawDetection>>(new[] { new RawDetection(box, cls, 0.9f) });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A service whose detector is scripted but whose orientation classifier is the real ONNX model.</summary>
    private static LayoutService CreateService(RecordingDetector detector, LayoutServiceOptions options)
        => new(detector, options, recognizer: null, logger: null,
            orientation: new OnnxOrientationClassifier(options.ModelCachePath, options.UseGpu, options.Offline, logger: null));

    // ---- the classifier itself ----

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task DocOri_DetectsEveryQuarterTurn_Confidently(int rotation)
    {
        await using var svc = new LayoutService(new LayoutServiceOptions());  // detector never loaded
        using var form = Asset("structure_sample.png");
        using var turned = TurnClockwise(form, rotation);

        var (predicted, confidence) = await svc.ClassifyOrientationAsync(turned);
        _out.WriteLine($"structure_sample content {rotation}° clockwise -> predicted {predicted}° (p={confidence:F3})");

        Assert.Equal(rotation, predicted);
        Assert.True(confidence >= 0.6f, $"confidence {confidence:F3} is below the default gate");
    }

    [Fact]
    public async Task DocOri_AcrossFixtures_IsCorrectForEveryQuarterTurn()
    {
        await using var svc = new LayoutService(new LayoutServiceOptions());
        foreach (var asset in new[] { "structure_sample.png", "Test_image1.png", "Multiple_Images.png", "Test_image2.png" })
        {
            using var page = Asset(asset);
            foreach (var rotation in new[] { 0, 90, 180, 270 })
            {
                using var turned = TurnClockwise(page, rotation);
                var (predicted, confidence) = await svc.ClassifyOrientationAsync(turned);
                _out.WriteLine($"{asset,-22} {rotation,3}° -> {predicted,3}° (p={confidence:F3})");
                Assert.Equal(rotation, predicted);
                Assert.True(confidence >= 0.6f);
            }
        }
    }

    // ---- the pipeline stage ----

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Pipeline_WithCorrectOrientation_DetectsOnTheUprightPage(int rotation)
    {
        using var form = Asset("structure_sample.png");
        using var turned = TurnClockwise(form, rotation);

        var det = new RecordingDetector();
        await using var svc = CreateService(det, new LayoutServiceOptions { CorrectOrientation = true });

        var page = (await svc.AnalyzeAsync(turned)).Document.Pages[0];
        _out.WriteLine($"content {rotation}°: rotation={page.Rotation}, detector saw {det.Seen.Single()}, " +
                       $"page {page.Width}x{page.Height} (source {page.SourceWidth}x{page.SourceHeight})");

        Assert.Equal(rotation, page.Rotation);
        Assert.True(page.IsCorrected);
        Assert.Equal((form.Width, form.Height), det.Seen.Single());   // 762x1000 again
        Assert.Equal(form.Width, page.Width);
        Assert.Equal(form.Height, page.Height);
        Assert.Equal(turned.Width, page.SourceWidth);
        Assert.Equal(turned.Height, page.SourceHeight);

        foreach (var block in page.Blocks)
        {
            var mapped = page.MapToSource(block.BoundingBox);
            Assert.InRange(mapped.MinX, -1, turned.Width + 1);
            Assert.InRange(mapped.MaxX, -1, turned.Width + 1);
            Assert.InRange(mapped.MinY, -1, turned.Height + 1);
            Assert.InRange(mapped.MaxY, -1, turned.Height + 1);
            if (rotation is 90 or 270)
            {
                Assert.Equal(block.BoundingBox.Width, mapped.Height, 3);  // a quarter turn swaps the axes
                Assert.Equal(block.BoundingBox.Height, mapped.Width, 3);
            }
            else
            {
                Assert.Equal(block.BoundingBox.Width, mapped.Width, 3);   // 180° only mirrors
                Assert.Equal(block.BoundingBox.Height, mapped.Height, 3);
            }
        }
    }

    [Fact]
    public async Task Pipeline_WithoutCorrection_LeavesARotatedPageRotated()
    {
        using var form = Asset("structure_sample.png");
        using var turned = TurnClockwise(form, 90);

        var det = new RecordingDetector();
        await using var svc = CreateService(det, new LayoutServiceOptions()); // CorrectOrientation = false

        var page = (await svc.AnalyzeAsync(turned)).Document.Pages[0];

        Assert.Equal(0, page.Rotation);
        Assert.False(page.IsCorrected);
        Assert.Equal((turned.Width, turned.Height), det.Seen.Single());
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(-3.0)]
    [InlineData(7.5)]
    public async Task Pipeline_WithDeskew_StraightensARealScan(double skew)
    {
        using var form = Asset("structure_sample.png");
        using var skewed = PageDeskew.Rotate(form, skew);

        var det = new RecordingDetector();
        await using var svc = CreateService(det, new LayoutServiceOptions());

        var page = (await svc.AnalyzeAsync(skewed, new LayoutAnalysisOptions { Deskew = true })).Document.Pages[0];
        _out.WriteLine($"skew {skew}°: measured {page.SkewAngle:F1}°, detector saw {det.Seen[0]}, " +
                       $"page {page.Width}x{page.Height} (source {page.SourceWidth}x{page.SourceHeight})");

        // structure_sample carries a native ~-0.2° tilt, so allow the full half-degree tolerance.
        Assert.InRange(page.SkewAngle, skew - 0.5, skew + 0.5);
        Assert.True(page.IsCorrected);
        Assert.Equal(0, page.Rotation);
        Assert.Equal((page.Width, page.Height), det.Seen.Single());
        Assert.True(page.Width > skewed.Width && page.Height > skewed.Height, "the straightened canvas should grow");
        Assert.Equal(skewed.Width, page.SourceWidth);
        Assert.Equal(skewed.Height, page.SourceHeight);

        foreach (var block in page.Blocks)
        {
            var mapped = page.MapToSource(block.BoundingBox);
            Assert.True(mapped.Width >= block.BoundingBox.Width - 1); // enclosing AABB of a rotated box
            Assert.InRange(mapped.CenterX, -5, skewed.Width + 5);
            Assert.InRange(mapped.CenterY, -5, skewed.Height + 5);
        }
    }

    [Fact]
    public async Task Pipeline_BothCorrections_OnARotatedAndSkewedScan()
    {
        using var form = Asset("structure_sample.png");
        using var skewed = PageDeskew.Rotate(form, 5.0);
        using var turned = TurnClockwise(skewed, 270);

        var det = new RecordingDetector();
        await using var svc = CreateService(det, new LayoutServiceOptions { CorrectOrientation = true });

        var page = (await svc.AnalyzeAsync(turned, new LayoutAnalysisOptions { Deskew = true })).Document.Pages[0];
        _out.WriteLine($"270° + 5° skew: rotation={page.Rotation}, skew={page.SkewAngle:F1}°, " +
                       $"page {page.Width}x{page.Height} (source {page.SourceWidth}x{page.SourceHeight})");

        Assert.Equal(270, page.Rotation);
        Assert.InRange(page.SkewAngle, 4.5, 5.5);
        Assert.Equal(turned.Width, page.SourceWidth);
        Assert.Equal(turned.Height, page.SourceHeight);

        // The centre of the corrected page is the centre of the caller's page.
        var (cx, cy) = page.MapToSource(page.Width / 2.0, page.Height / 2.0);
        Assert.Equal(turned.Width / 2.0, cx, 3);
        Assert.Equal(turned.Height / 2.0, cy, 3);
    }

    [Fact]
    public async Task Deskew_OnAStraightScan_ChangesNothing()
    {
        using var form = Asset("structure_sample.png");
        var det = new RecordingDetector();
        await using var svc = CreateService(det, new LayoutServiceOptions());

        var page = (await svc.AnalyzeAsync(form, new LayoutAnalysisOptions { Deskew = true })).Document.Pages[0];

        Assert.Equal(0, page.SkewAngle);
        Assert.False(page.IsCorrected);
        Assert.Equal((form.Width, form.Height), det.Seen.Single());
    }

    [Fact]
    public async Task WarmUp_ThenOffline_ServesTheOrientationModelFromCache()
    {
        var det = new RecordingDetector();
        await using (var online = CreateService(det, new LayoutServiceOptions { CorrectOrientation = true }))
            await online.WarmUpAsync();

        var offlineDet = new RecordingDetector();
        await using var offline = CreateService(offlineDet, new LayoutServiceOptions { CorrectOrientation = true, Offline = true });

        using var form = Asset("structure_sample.png");
        using var turned = TurnClockwise(form, 180);
        var page = (await offline.AnalyzeAsync(turned)).Document.Pages[0];

        Assert.Equal(180, page.Rotation);
    }
}
