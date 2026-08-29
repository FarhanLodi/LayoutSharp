using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// How <see cref="ReadingOrderSource"/> chooses between the detector's own reading order and the
/// geometric XY-cut, exercised over a scripted detector so no model is needed.
/// </summary>
public class ReadingOrderSourceTests
{
    private static readonly LayoutModelSpec Spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

    /// <summary>A detection with an optional model-supplied reading-order key.</summary>
    private static RawDetection Det(string label, double x, double y, double w, double h, int order = -1, float score = 0.9f)
    {
        var cls = Spec.Classes.First(c => c.Name == label);
        return new RawDetection(new LayoutBox(x, y, x + w, y + h), cls, score, order);
    }

    /// <summary>Replays a fixed detection list, like the fake in <see cref="LayoutServiceTests"/> but order-aware.</summary>
    private sealed class OrderedDetector : ILayoutDetector
    {
        public List<RawDetection> Detections { get; } = new();
        public LayoutModel Model => LayoutModel.DoclingLayoutHeron;
        public bool IsGpu => false;
        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<RawDetection>> DetectAsync(Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RawDetection>>(Detections.Where(d => d.Score >= scoreThreshold).ToList());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static LayoutService Create(OrderedDetector detector) => new(detector, null, null, null);

    /// <summary>Three body blocks whose model ranks deliberately contradict their geometry.</summary>
    private static OrderedDetector ContradictoryRanks()
    {
        var det = new OrderedDetector();
        det.Detections.Add(Det("title", 10, 10, 380, 40, order: 30));    // top of the page, reads last
        det.Detections.Add(Det("text", 10, 200, 380, 100, order: 20));       // middle, reads second
        det.Detections.Add(Det("picture", 10, 400, 380, 150, order: 10));      // bottom, reads first
        return det;
    }

    [Fact]
    public async Task Auto_UsesModelOrder_WhenEveryDetectionHasOne()
    {
        await using var svc = Create(ContradictoryRanks());
        using var img = new Image<Rgb24>(400, 600);

        var result = await svc.AnalyzeAsync(img);
        var blocks = result.Document.Pages[0].Blocks;

        Assert.Equal(ReadingOrderSource.Model, result.ReadingOrderUsed);
        Assert.Equal(new[] { LayoutBlockType.Figure, LayoutBlockType.Text, LayoutBlockType.Title }, blocks.Select(b => b.Type));
        Assert.Equal(new[] { 0, 1, 2 }, blocks.Select(b => b.ReadingOrder));
    }

    [Fact]
    public async Task XyCut_IgnoresModelOrder()
    {
        await using var svc = Create(ContradictoryRanks());
        using var img = new Image<Rgb24>(400, 600);

        var result = await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { ReadingOrderSource = ReadingOrderSource.XyCut });
        var blocks = result.Document.Pages[0].Blocks;

        Assert.Equal(ReadingOrderSource.XyCut, result.ReadingOrderUsed);
        Assert.Equal(new[] { LayoutBlockType.Title, LayoutBlockType.Text, LayoutBlockType.Figure }, blocks.Select(b => b.Type));
    }

    [Fact]
    public async Task Auto_FallsBackToXyCut_WhenAnyDetectionLacksAnOrder()
    {
        var det = ContradictoryRanks();
        det.Detections.Add(Det("table", 10, 560, 380, 30, order: -1)); // no rank → the whole page falls back

        await using var svc = Create(det);
        using var img = new Image<Rgb24>(400, 600);

        var result = await svc.AnalyzeAsync(img);
        Assert.Equal(ReadingOrderSource.XyCut, result.ReadingOrderUsed);
        Assert.Equal(LayoutBlockType.Title, result.Document.Pages[0].Blocks[0].Type);
    }

    [Fact]
    public async Task Model_WithoutModelOrder_FallsBackToXyCut()
    {
        var det = new OrderedDetector();
        det.Detections.Add(Det("title", 10, 10, 380, 40));
        det.Detections.Add(Det("text", 10, 200, 380, 100));

        await using var svc = Create(det);
        using var img = new Image<Rgb24>(400, 600);

        var result = await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { ReadingOrderSource = ReadingOrderSource.Model });
        Assert.Equal(ReadingOrderSource.XyCut, result.ReadingOrderUsed);
        Assert.Equal(LayoutBlockType.Title, result.Document.Pages[0].Blocks[0].Type);
    }

    [Fact]
    public async Task PinPageFurniture_AppliesInModelOrderToo()
    {
        var det = new OrderedDetector();
        det.Detections.Add(Det("page_footer", 10, 560, 380, 30, order: 1));      // ranked first, still pinned last
        det.Detections.Add(Det("text", 10, 300, 380, 100, order: 20));
        det.Detections.Add(Det("title", 10, 100, 380, 40, order: 10));
        det.Detections.Add(Det("page_header", 10, 10, 380, 30, order: 99));     // ranked last, still pinned first

        await using var svc = Create(det);
        using var img = new Image<Rgb24>(400, 600);

        var result = await svc.AnalyzeAsync(img);
        var blocks = result.Document.Pages[0].Blocks;

        Assert.Equal(ReadingOrderSource.Model, result.ReadingOrderUsed);
        Assert.Equal(
            new[] { LayoutBlockType.PageHeader, LayoutBlockType.Title, LayoutBlockType.Text, LayoutBlockType.PageFooter },
            blocks.Select(b => b.Type));

        // Without pinning, the pure model order applies.
        var unpinned = (await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { PinPageFurniture = false })).Document.Pages[0].Blocks;
        Assert.Equal(LayoutBlockType.PageFooter, unpinned[0].Type);
        Assert.Equal(LayoutBlockType.PageHeader, unpinned[^1].Type);
    }

    [Fact]
    public async Task Deduplicate_KeepsTheHigherScoringRow_AndItsRank()
    {
        // The RT-DETR top-k lists one region under two classes with different ranks.
        var det = new OrderedDetector();
        det.Detections.Add(Det("text", 10, 200, 380, 100, order: 5, score: 0.55f));
        det.Detections.Add(Det("text", 12, 201, 380, 100, order: 40, score: 0.80f)); // IoU ≈ 0.98, wins
        det.Detections.Add(Det("title", 10, 10, 380, 40, order: 20));

        await using var svc = Create(det);
        using var img = new Image<Rgb24>(400, 600);

        var result = await svc.AnalyzeAsync(img);
        var blocks = result.Document.Pages[0].Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.Equal(ReadingOrderSource.Model, result.ReadingOrderUsed);
        // The survivor's own rank (40) is what orders it — after the title (20), not before it (5).
        Assert.Equal(new[] { "title", "text" }, blocks.Select(b => b.RawClassName));
    }

    [Fact]
    public async Task Options_RejectAnUnknownReadingOrderSource()
    {
        await using var svc = Create(new OrderedDetector());
        using var img = new Image<Rgb24>(100, 100);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            svc.AnalyzeAsync(img, new LayoutAnalysisOptions { ReadingOrderSource = (ReadingOrderSource)42 }));
    }

    [Fact]
    public async Task Result_ReportsTheModelName()
    {
        await using var svc = Create(new OrderedDetector());
        using var img = new Image<Rgb24>(100, 100);
        var result = await svc.AnalyzeAsync(img);

        // The scripted service is constructed with default options, i.e. the PP-DocLayoutV3 spec.
        Assert.Equal("PP-DocLayoutV3", result.ModelName);
        Assert.Equal(ReadingOrderSource.XyCut, result.ReadingOrderUsed); // no detections → nothing to order
    }

    [Fact]
    public void DefaultOptions_UseAuto()
        => Assert.Equal(ReadingOrderSource.Auto, LayoutAnalysisOptions.Default.ReadingOrderSource);
}
