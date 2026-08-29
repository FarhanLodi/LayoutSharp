using LayoutSharp.Models;
using LayoutSharp.Recognition;
using LayoutSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace LayoutSharp.Tests;

/// <summary>
/// Shares one PP-DocLayout_plus-L service across the integration tests so the model is downloaded
/// (first run only) and the ONNX session created once.
/// </summary>
public sealed class PlusLFixture : IAsyncLifetime
{
    public LayoutService Service { get; } = new(new LayoutServiceOptions { Model = LayoutModel.DoclingLayoutHeron });
    public Task InitializeAsync() => Service.WarmUpAsync();
    public async Task DisposeAsync() => await Service.DisposeAsync();
}

/// <summary>
/// Model-backed end-to-end tests over every image in <c>test/assets</c>. Excluded from CI
/// (<c>--filter "Category!=Integration"</c>) because they download the detector on first use.
/// Expectations are deliberately loose lower bounds — CPU inference is deterministic on one machine,
/// but borderline detections can flip across platforms.
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests : IClassFixture<PlusLFixture>
{
    private readonly LayoutService _svc;
    private readonly ITestOutputHelper _out;

    public IntegrationTests(PlusLFixture fixture, ITestOutputHelper output)
    {
        _svc = fixture.Service;
        _out = output;
    }

    public static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "assets", name);

    /// <summary>Every PNG under test/assets, so a newly dropped image is picked up automatically.</summary>
    public static IEnumerable<object[]> AllAssets()
        => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "assets"), "*.png")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { Path.GetFileName(p) });

    // ---- invariants over every image ----

    [Theory]
    [MemberData(nameof(AllAssets))]
    public async Task EveryAsset_ProducesAWellFormedPage(string file)
    {
        var result = await _svc.AnalyzeAsync(AssetPath(file));
        var page = Assert.Single(result.Document.Pages);
        var blocks = page.Blocks;

        _out.WriteLine($"{file}: {page.Width}x{page.Height}, {blocks.Count} blocks in {result.Duration.TotalMilliseconds:F0} ms");
        foreach (var g in blocks.GroupBy(b => b.Type).OrderByDescending(g => g.Count()))
            _out.WriteLine($"  {g.Key,-14} {g.Count()}");

        Assert.Equal(LayoutModel.DoclingLayoutHeron, result.Model);
        Assert.False(result.TextRecognized);
        Assert.NotEmpty(blocks);

        // Reading order is a contiguous 0..n-1 sequence in list order.
        Assert.Equal(Enumerable.Range(0, blocks.Count), blocks.Select(b => b.ReadingOrder));

        foreach (var b in blocks)
        {
            Assert.InRange(b.Confidence, 0.5f, 1f);
            Assert.InRange(b.BoundingBox.MinX, 0, page.Width);
            Assert.InRange(b.BoundingBox.MaxX, 0, page.Width);
            Assert.InRange(b.BoundingBox.MinY, 0, page.Height);
            Assert.InRange(b.BoundingBox.MaxY, 0, page.Height);
            Assert.True(b.BoundingBox.Width >= 1 && b.BoundingBox.Height >= 1, $"degenerate box {b.BoundingBox}");
            Assert.False(string.IsNullOrEmpty(b.RawClassName));
            Assert.InRange(b.RawClassId, 0, 19); // plus-L has 20 classes
            Assert.NotEqual(LayoutBlockType.Other, b.Type);
            Assert.Null(b.Text); // layout-only service
        }

        // No two surviving blocks overlap above the de-duplication threshold.
        for (int i = 0; i < blocks.Count; i++)
            for (int j = i + 1; j < blocks.Count; j++)
                Assert.True(blocks[i].BoundingBox.IntersectionOverUnion(blocks[j].BoundingBox) <= 0.6,
                    $"blocks {i} and {j} overlap with IoU > 0.6");

        // Page furniture is pinned: headers first, footers/page numbers last.
        var types = blocks.Select(b => b.Type).ToList();
        int lastHeader = types.LastIndexOf(LayoutBlockType.PageHeader);
        int firstBody = types.FindIndex(t => !t.IsPageFurniture());
        int firstFooter = types.FindIndex(t => t is LayoutBlockType.PageFooter or LayoutBlockType.PageNumber);
        int lastBody = types.FindLastIndex(t => !t.IsPageFurniture());
        if (lastHeader >= 0 && firstBody >= 0) Assert.True(lastHeader < firstBody, "a page header follows body content");
        if (firstFooter >= 0 && lastBody >= 0) Assert.True(firstFooter > lastBody, "a footer/page number precedes body content");

        // Exports work and JSON round-trips losslessly.
        var json = result.Document.ToJson();
        var back = LayoutDocument.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(json, back!.ToJson());
        Assert.Equal(blocks.Count, back.Pages[0].Blocks.Count);
        _ = result.Document.ToPlainText();
        _ = result.Document.ToMarkdown();
    }

    // ---- per-image content expectations (lower bounds observed with plus-L on CPU) ----

    [Fact]
    public async Task StructureSample_ScannedForm()
    {
        var blocks = (await _svc.AnalyzeAsync(AssetPath("structure_sample.png"))).Document.Pages[0].Blocks;
        Assert.True(blocks.Count >= 25, $"expected ≥25 blocks, got {blocks.Count}");
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.SectionHeader);  // the two banner headings
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Figure);         // the two logos
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.PageFooter);     // "Approval Signature/Date"
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Checkbox);       // Yes / No / Pending
        Assert.True(blocks.Count(b => b.Type == LayoutBlockType.Text) >= 15);
        // The banner heading is read before the body text below it.
        Assert.True(blocks.First(b => b.Type == LayoutBlockType.SectionHeader).ReadingOrder
                    < blocks.Last(b => b.Type == LayoutBlockType.Text).ReadingOrder);
    }

    [Fact]
    public async Task TestImage1_InstructionManualPage()
    {
        var page = (await _svc.AnalyzeAsync(AssetPath("Test_image1.png"))).Document.Pages[0];
        var blocks = page.Blocks;
        Assert.True(blocks.Count >= 7, $"expected ≥7 blocks, got {blocks.Count}");
        // Heron reads the numbered assembly steps as list items rather than paragraphs.

        // "STEP 4: ASSEMBLY OF MOTOR FRAME" heading comes first among body blocks.
        Assert.Equal(LayoutBlockType.SectionHeader, blocks.First(b => !b.Type.IsPageFurniture()).Type);

        // The exploded-view drawing is one large figure (≥ 30% of the page).
        var figure = blocks.Where(b => b.Type == LayoutBlockType.Figure).OrderByDescending(b => b.BoundingBox.Area).First();
        Assert.True(figure.BoundingBox.Area >= 0.30 * page.Width * page.Height);

        Assert.True(blocks.Count(b => b.Type is LayoutBlockType.Text or LayoutBlockType.List) >= 3); // numbered steps + warning body
        Assert.Contains(blocks, b => b.Type is LayoutBlockType.PageNumber or LayoutBlockType.PageFooter); // "14" / "Page"

        // Furniture pinned last; the figure precedes the right-hand column text.
        Assert.True(blocks[^1].Type.IsPageFurniture());
        Assert.True(figure.ReadingOrder < blocks.First(b => b.Type == LayoutBlockType.Text).ReadingOrder);
    }

    [Fact]
    public async Task TestImage2_MagazineSpread()
    {
        var blocks = (await _svc.AnalyzeAsync(AssetPath("Test_image2.png"))).Document.Pages[0].Blocks;
        Assert.True(blocks.Count >= 10, $"expected ≥10 blocks, got {blocks.Count}");
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Figure);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Caption);
        Assert.True(blocks.Count(b => b.Type == LayoutBlockType.Text) >= 8);
    }

    [Fact]
    public async Task TestImage3_ReportSpread_TablesChartsColumns()
    {
        var blocks = (await _svc.AnalyzeAsync(AssetPath("Test_image3.png"))).Document.Pages[0].Blocks;
        Assert.True(blocks.Count >= 30, $"expected ≥30 blocks, got {blocks.Count}");
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Table);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Figure);       // chart
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Caption);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.SectionHeader);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.PageHeader);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.PageFooter);
        Assert.True(blocks.Count(b => b.Type is LayoutBlockType.Text or LayoutBlockType.List) >= 20);
    }

    [Fact]
    public async Task TestImage4_PhotographedNewspaper_IsSegmentedIntoColumns()
    {
        // A blurred photo of a dense classifieds page — the densest fixture in the corpus, and the
        // page-collapse guard. A detector that cannot resolve the columns at its fixed input size
        // reports the whole sheet as one picture instead of segmenting it (PP-DocLayout did exactly
        // that). The page must come back as many text-bearing regions, with nothing swallowing it.
        var page = (await _svc.AnalyzeAsync(AssetPath("Test_image4.png"))).Document.Pages[0];
        var blocks = page.Blocks;
        Assert.True(blocks.Count >= 50, $"expected ≥50 blocks, got {blocks.Count}");

        double pageArea = (double)page.Width * page.Height;
        Assert.DoesNotContain(blocks, b => b.Type == LayoutBlockType.Figure
                                           && b.BoundingBox.Area >= 0.5 * pageArea);

        // The bulk of the page is running text in columns, not figures.
        int textBearing = blocks.Count(b => b.Type.IsTextBearing());
        Assert.True(textBearing >= 40, $"expected ≥40 text-bearing blocks, got {textBearing}");
    }

    [Fact]
    public async Task TestImage5_PhotographedManual_TablesFiguresCaptions()
    {
        var blocks = (await _svc.AnalyzeAsync(AssetPath("Test_image5.png"))).Document.Pages[0].Blocks;
        Assert.True(blocks.Count >= 15, $"expected ≥15 blocks, got {blocks.Count}");
        Assert.True(blocks.Count(b => b.Type == LayoutBlockType.Table) >= 3);
        Assert.True(blocks.Count(b => b.Type == LayoutBlockType.Figure) >= 3);
        Assert.True(blocks.Count(b => b.Type == LayoutBlockType.Caption) >= 3);
    }

    [Fact]
    public async Task TestImage6_ManualPage()
    {
        var blocks = (await _svc.AnalyzeAsync(AssetPath("Test_image6.png"))).Document.Pages[0].Blocks;
        Assert.True(blocks.Count >= 5, $"expected ≥5 blocks, got {blocks.Count}");
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.SectionHeader);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Figure);
        Assert.True(blocks.Count(b => b.Type is LayoutBlockType.Text or LayoutBlockType.List) >= 3);
    }

    [Fact]
    public async Task MultipleImages_Mosaic_FindsManyRegionTypes()
    {
        var blocks = (await _svc.AnalyzeAsync(AssetPath("Multiple_Images.png"))).Document.Pages[0].Blocks;
        Assert.True(blocks.Count >= 30, $"expected ≥30 blocks, got {blocks.Count}");
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Caption);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Table);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Figure);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.SectionHeader);
        Assert.True(blocks.Count(b => b.Type == LayoutBlockType.Text) >= 15);
    }

    // ---- options, input paths, recognizer plumbing on the real model ----

    [Fact]
    public async Task LowerThreshold_NeverReturnsFewerBlocks()
    {
        var path = AssetPath("structure_sample.png");
        var strict = (await _svc.AnalyzeAsync(path, new LayoutAnalysisOptions { ConfidenceThreshold = 0.7f })).Document.Pages[0].Blocks;
        var loose = (await _svc.AnalyzeAsync(path, new LayoutAnalysisOptions { ConfidenceThreshold = 0.3f })).Document.Pages[0].Blocks;
        Assert.True(loose.Count >= strict.Count);
        Assert.All(strict, b => Assert.True(b.Confidence >= 0.7f));
    }

    [Fact]
    public async Task AllInputOverloads_AgreeOnTheSameImage()
    {
        var path = AssetPath("structure_sample.png");
        var bytes = await File.ReadAllBytesAsync(path);
        using var image = Image.Load<Rgb24>(bytes);
        using var stream = new MemoryStream(bytes);

        var a = (await _svc.AnalyzeAsync(path)).Document.ToJson();
        var b = (await _svc.AnalyzeAsync(bytes)).Document.ToJson();
        var c = (await _svc.AnalyzeAsync(stream)).Document.ToJson();
        var d = (await _svc.AnalyzeAsync(image)).Document.ToJson();

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a, d);
    }

    [Fact]
    public async Task Recognizer_ReceivesOneCropPerTextBearingBlock_AtSourceResolution()
    {
        var crops = new List<(int W, int H)>();
        var recognizer = TextRecognizer.FromDelegate((crop, _) =>
        {
            lock (crops) crops.Add((crop.Width, crop.Height));
            return Task.FromResult<string?>($"{crop.Width}x{crop.Height}");
        });

        await using var svc = new LayoutService(new LayoutServiceOptions { Model = LayoutModel.DoclingLayoutHeron }, recognizer);
        var result = await svc.AnalyzeAsync(AssetPath("Test_image1.png"));
        var blocks = result.Document.Pages[0].Blocks;

        Assert.True(result.TextRecognized);
        var textBearing = blocks.Where(b => b.Type.IsTextBearing()).ToList();
        Assert.NotEmpty(textBearing);
        Assert.Equal(textBearing.Count, crops.Count);
        foreach (var b in textBearing)
        {
            var (x, y, w, h) = b.BoundingBox.ToPixelRect(2816, 1536);
            Assert.Equal($"{w}x{h}", b.Text);
        }
        Assert.All(blocks.Where(b => !b.Type.IsTextBearing()), b => Assert.Null(b.Text));

        // Plain text and Markdown now carry the recognized strings.
        Assert.Contains("x", result.Document.ToPlainText());
        Assert.StartsWith("## ", result.Document.ToMarkdown()); // the STEP heading
    }

    [Fact]
    public async Task ConcurrentCalls_OnOneService_AreSafe()
    {
        var path = AssetPath("structure_sample.png");
        var tasks = Enumerable.Range(0, 4).Select(_ => _svc.AnalyzeAsync(path)).ToArray();
        var results = await Task.WhenAll(tasks);
        var first = results[0].Document.ToJson();
        Assert.All(results, r => Assert.Equal(first, r.Document.ToJson()));
    }
}

/// <summary>Warm-up and offline serving for the shipped detector.</summary>
[Trait("Category", "Integration")]
public class ModelVariantIntegrationTests
{
    [Fact]
    public async Task WarmUp_ThenOffline_ServesFromCache()
    {
        // Warm up on the (already-populated or downloadable) cache, then prove an offline service
        // pointed at the same cache works without touching the network.
        await using (var online = new LayoutService())
            await online.WarmUpAsync();

        await using var offline = new LayoutService(new LayoutServiceOptions { Offline = true });
        var result = await offline.AnalyzeAsync(IntegrationTests.AssetPath("structure_sample.png"));

        Assert.Equal(LayoutModel.PPDocLayoutV3, result.Model);
        Assert.NotEmpty(result.Document.Pages[0].Blocks);
    }
}
