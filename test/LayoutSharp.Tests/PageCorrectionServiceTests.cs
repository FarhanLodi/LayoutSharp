using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Preprocessing;
using LayoutSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// The page-correction stage inside <see cref="LayoutService"/>, driven through the internal test
/// seam: a scripted detector that records the image it was handed, and a scripted orientation
/// classifier, so no model is downloaded.
/// </summary>
public class PageCorrectionServiceTests
{
    private static readonly LayoutModelSpec Spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

    /// <summary>Records the size of every image the pipeline detects on, and returns fixed boxes.</summary>
    private sealed class RecordingDetector : ILayoutDetector
    {
        public List<(int Width, int Height)> Seen { get; } = new();
        public List<RawDetection> Detections { get; } = new();
        public bool Disposed;

        public LayoutModel Model => LayoutModel.DoclingLayoutHeron;
        public bool IsGpu => false;
        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RawDetection>> DetectAsync(Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken)
        {
            lock (Seen) Seen.Add((image.Width, image.Height));
            return Task.FromResult<IReadOnlyList<RawDetection>>(Detections.ToList());
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>Returns a scripted orientation prediction and counts how it was used.</summary>
    private sealed class FakeOrientationClassifier : IOrientationClassifier
    {
        private readonly int _rotation;
        private readonly float _confidence;
        public int Calls;
        public bool WarmedUp;
        public bool Disposed;

        public FakeOrientationClassifier(int rotation, float confidence)
        {
            _rotation = rotation;
            _confidence = confidence;
        }

        public Task WarmUpAsync(CancellationToken cancellationToken) { WarmedUp = true; return Task.CompletedTask; }

        public Task<OrientationPrediction> ClassifyAsync(Image<Rgb24> image, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            float rest = (1f - _confidence) / 3f;
            var p = new float[4];
            for (int i = 0; i < 4; i++) p[i] = rest;
            p[_rotation / 90] = _confidence;
            return Task.FromResult(new OrientationPrediction(_rotation, _confidence, p[0], p[1], p[2], p[3]));
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static RawDetection Det(string label, double x, double y, double w, double h, float score = 0.9f)
        => new(new LayoutBox(x, y, x + w, y + h), Spec.Classes.First(c => c.Name == label), score);

    private static LayoutService Create(ILayoutDetector detector, LayoutServiceOptions? options = null, IOrientationClassifier? orientation = null)
        => new(detector, options, recognizer: null, logger: null, orientation: orientation);

    // ---- orientation ----

    [Fact]
    public async Task Orientation_AboveThreshold_RotatesBeforeDetection_AndReportsTheCorrection()
    {
        var det = new RecordingDetector();
        det.Detections.Add(Det("text", 10, 20, 100, 30));
        var clf = new FakeOrientationClassifier(90, 0.9f);
        await using var svc = Create(det, new LayoutServiceOptions { CorrectOrientation = true }, clf);
        using var img = new Image<Rgb24>(400, 600);

        var page = (await svc.AnalyzeAsync(img)).Document.Pages[0];

        Assert.Equal(1, clf.Calls);
        Assert.Equal((600, 400), det.Seen.Single());   // detector saw the upright (swapped) page
        Assert.Equal(600, page.Width);
        Assert.Equal(400, page.Height);
        Assert.Equal(400, page.SourceWidth);
        Assert.Equal(600, page.SourceHeight);
        Assert.Equal(90, page.Rotation);
        Assert.Equal(0, page.SkewAngle);
        Assert.True(page.IsCorrected);

        // The block's box maps back into the caller's 400x600 frame.
        var mapped = page.MapToSource(page.Blocks[0].BoundingBox);
        Assert.InRange(mapped.MinX, 0, 400);
        Assert.InRange(mapped.MaxX, 0, 400);
        Assert.InRange(mapped.MinY, 0, 600);
        Assert.InRange(mapped.MaxY, 0, 600);
    }

    [Fact]
    public async Task Orientation_BelowThreshold_LeavesThePageAlone()
    {
        var det = new RecordingDetector();
        var clf = new FakeOrientationClassifier(90, 0.4f);
        await using var svc = Create(det, new LayoutServiceOptions { CorrectOrientation = true }, clf); // threshold 0.6
        using var img = new Image<Rgb24>(400, 600);

        var page = (await svc.AnalyzeAsync(img)).Document.Pages[0];

        Assert.Equal((400, 600), det.Seen.Single());
        Assert.Equal(0, page.Rotation);
        Assert.False(page.IsCorrected);
        Assert.Equal(400, page.SourceWidth);

        // Lowering the threshold makes the same prediction actionable.
        var det2 = new RecordingDetector();
        await using var svc2 = Create(det2, new LayoutServiceOptions { CorrectOrientation = true, OrientationConfidenceThreshold = 0.3f },
            new FakeOrientationClassifier(90, 0.4f));
        var page2 = (await svc2.AnalyzeAsync(img)).Document.Pages[0];
        Assert.Equal(90, page2.Rotation);
        Assert.Equal((600, 400), det2.Seen.Single());
    }

    [Fact]
    public async Task Orientation_UprightPrediction_DoesNotCopyTheImage()
    {
        var det = new RecordingDetector();
        var clf = new FakeOrientationClassifier(0, 0.95f);
        await using var svc = Create(det, new LayoutServiceOptions { CorrectOrientation = true }, clf);
        using var img = new Image<Rgb24>(400, 600, new Rgb24(7, 8, 9));

        var page = (await svc.AnalyzeAsync(img)).Document.Pages[0];

        Assert.Equal((400, 600), det.Seen.Single());
        Assert.Equal(0, page.Rotation);
        Assert.False(page.IsCorrected);
        Assert.Equal(new Rgb24(7, 8, 9), img[0, 0]); // caller's image untouched and not disposed
    }

    [Fact]
    public async Task Orientation_Disabled_NeverCallsTheClassifier()
    {
        var det = new RecordingDetector();
        var clf = new FakeOrientationClassifier(180, 0.99f);
        await using var svc = Create(det, new LayoutServiceOptions(), clf); // CorrectOrientation = false
        using var img = new Image<Rgb24>(400, 600);

        var page = (await svc.AnalyzeAsync(img)).Document.Pages[0];

        Assert.Equal(0, clf.Calls);
        Assert.Equal(0, page.Rotation);
        Assert.Equal((400, 600), det.Seen.Single());
    }

    [Fact]
    public async Task WarmUp_WarmsTheClassifier_OnlyWhenEnabled_AndDisposalIsForwarded()
    {
        var det = new RecordingDetector();
        var clf = new FakeOrientationClassifier(0, 0.9f);
        var svc = Create(det, new LayoutServiceOptions { CorrectOrientation = true }, clf);
        await svc.WarmUpAsync();
        Assert.True(clf.WarmedUp);
        await svc.DisposeAsync();
        Assert.True(clf.Disposed);
        Assert.True(det.Disposed);

        var off = new FakeOrientationClassifier(0, 0.9f);
        await using (var svc2 = Create(new RecordingDetector(), new LayoutServiceOptions(), off))
            await svc2.WarmUpAsync();
        Assert.False(off.WarmedUp);
        Assert.True(off.Disposed);
    }

    [Fact]
    public async Task ClassifyOrientationAsync_ExposesTheClassifierDirectly()
    {
        var clf = new FakeOrientationClassifier(270, 0.83f);
        await using var svc = Create(new RecordingDetector(), new LayoutServiceOptions(), clf); // works even when correction is off
        using var img = new Image<Rgb24>(100, 100);

        var (rotation, confidence) = await svc.ClassifyOrientationAsync(img);

        Assert.Equal(270, rotation);
        Assert.Equal(0.83f, confidence, 5);
        Assert.Equal(1, clf.Calls);
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.ClassifyOrientationAsync(null!));
    }

    [Fact]
    public async Task ClassifyOrientationAsync_AfterDispose_Throws()
    {
        var svc = Create(new RecordingDetector(), new LayoutServiceOptions(), new FakeOrientationClassifier(0, 0.9f));
        using var img = new Image<Rgb24>(100, 100);
        await svc.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.ClassifyOrientationAsync(img));
    }

    // ---- deskew ----

    [Fact]
    public async Task Deskew_RunsDetectionOnTheStraightenedCanvas()
    {
        using var page = PageDeskewTests.SyntheticPage();
        using var skewed = PageDeskew.Rotate(page, 3.0);

        var det = new RecordingDetector();
        det.Detections.Add(Det("text", 10, 20, 100, 30));
        await using var svc = Create(det);

        var result = (await svc.AnalyzeAsync(skewed, new LayoutAnalysisOptions { Deskew = true })).Document.Pages[0];

        var seen = det.Seen.Single();
        Assert.True(seen.Width > skewed.Width && seen.Height > skewed.Height, "detector should see the expanded straightened canvas");
        Assert.Equal(seen.Width, result.Width);
        Assert.Equal(seen.Height, result.Height);
        Assert.Equal(skewed.Width, result.SourceWidth);
        Assert.Equal(skewed.Height, result.SourceHeight);
        Assert.InRange(result.SkewAngle, 2.5, 3.5);
        Assert.Equal(0, result.Rotation);
        Assert.True(result.IsCorrected);
    }

    [Fact]
    public async Task Deskew_Disabled_OrUnreliable_LeavesThePageAlone()
    {
        using var page = PageDeskewTests.SyntheticPage();
        using var skewed = PageDeskew.Rotate(page, 3.0);

        // Disabled (the default).
        var det = new RecordingDetector();
        await using var svc = Create(det);
        var plain = (await svc.AnalyzeAsync(skewed)).Document.Pages[0];
        Assert.Equal((skewed.Width, skewed.Height), det.Seen.Single());
        Assert.Equal(0, plain.SkewAngle);
        Assert.False(plain.IsCorrected);

        // Enabled but the page is straight: nothing to correct.
        var det2 = new RecordingDetector();
        await using var svc2 = Create(det2);
        var straight = (await svc2.AnalyzeAsync(page, new LayoutAnalysisOptions { Deskew = true })).Document.Pages[0];
        Assert.Equal((page.Width, page.Height), det2.Seen.Single());
        Assert.Equal(0, straight.SkewAngle);
        Assert.False(straight.IsCorrected);
    }

    [Fact]
    public async Task Deskew_MaxAngleIsHonoured_AndValidated()
    {
        using var page = PageDeskewTests.SyntheticPage();
        using var skewed = PageDeskew.Rotate(page, 12.0);

        var det = new RecordingDetector();
        await using var svc = Create(det);

        // A 2-degree window cannot reach the true 12-degree skew.
        var narrow = (await svc.AnalyzeAsync(skewed, new LayoutAnalysisOptions { Deskew = true, DeskewMaxAngle = 2 })).Document.Pages[0];
        Assert.InRange(Math.Abs(narrow.SkewAngle), 0, 2);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.AnalyzeAsync(skewed, new LayoutAnalysisOptions { DeskewMaxAngle = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.AnalyzeAsync(skewed, new LayoutAnalysisOptions { DeskewMaxAngle = 46 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutService(new LayoutServiceOptions { OrientationConfidenceThreshold = 1.5f }));
    }

    // ---- both stages together ----

    [Fact]
    public async Task OrientationThenDeskew_BothApply_AndCoordinatesMapBack()
    {
        using var page = PageDeskewTests.SyntheticPage();          // 1000x1400 upright
        using var skewed = PageDeskew.Rotate(page, 3.0);           // tilted 3 degrees clockwise
        using var source = skewed.Clone(c => c.Rotate(RotateMode.Rotate90)); // ...and turned 90 clockwise

        var det = new RecordingDetector();
        det.Detections.Add(Det("text", 100, 200, 300, 50));
        await using var svc = Create(det, new LayoutServiceOptions { CorrectOrientation = true }, new FakeOrientationClassifier(90, 0.9f));

        var result = (await svc.AnalyzeAsync(source, new LayoutAnalysisOptions { Deskew = true })).Document.Pages[0];

        Assert.Equal(90, result.Rotation);
        Assert.InRange(result.SkewAngle, 2.5, 3.5);
        Assert.Equal(source.Width, result.SourceWidth);
        Assert.Equal(source.Height, result.SourceHeight);

        // Every corner of the corrected page maps to a finite point; the centre maps to the source centre.
        var (cx, cy) = result.MapToSource(result.Width / 2.0, result.Height / 2.0);
        Assert.Equal(source.Width / 2.0, cx, 3);
        Assert.Equal(source.Height / 2.0, cy, 3);
    }

    [Fact]
    public async Task ConcurrentCalls_WithBothCorrections_AreIndependent()
    {
        using var page = PageDeskewTests.SyntheticPage();
        using var skewed = PageDeskew.Rotate(page, 3.0);

        var det = new RecordingDetector();
        det.Detections.Add(Det("text", 10, 20, 100, 30));
        await using var svc = Create(det, new LayoutServiceOptions { CorrectOrientation = true }, new FakeOrientationClassifier(180, 0.9f));

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => svc.AnalyzeAsync(skewed, new LayoutAnalysisOptions { Deskew = true })));

        Assert.All(results, r =>
        {
            var p = r.Document.Pages[0];
            Assert.Equal(180, p.Rotation);
            Assert.InRange(p.SkewAngle, 2.5, 3.5);
            Assert.Single(p.Blocks);
        });
        Assert.Equal(8, det.Seen.Count);
        Assert.Single(det.Seen.Distinct());
    }

    [Fact]
    public async Task CorrectedPage_SurvivesJsonRoundTrip()
    {
        var det = new RecordingDetector();
        det.Detections.Add(Det("text", 10, 20, 100, 30));
        await using var svc = Create(det, new LayoutServiceOptions { CorrectOrientation = true }, new FakeOrientationClassifier(270, 0.9f));
        using var img = new Image<Rgb24>(400, 600);

        var document = (await svc.AnalyzeAsync(img)).Document;
        var json = document.ToJson();
        Assert.Contains("\"Rotation\": 270", json);
        Assert.Contains("\"SourceWidth\": 400", json);

        var back = LayoutDocument.FromJson(json);
        Assert.NotNull(back);
        var page = back!.Pages[0];
        Assert.Equal(270, page.Rotation);
        Assert.Equal(400, page.SourceWidth);
        Assert.Equal(600, page.SourceHeight);
        Assert.Equal(document.Pages[0].MapToSource(page.Blocks[0].BoundingBox), page.MapToSource(page.Blocks[0].BoundingBox));
    }
}
