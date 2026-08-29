using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Services;
using EasyImageSharp;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;
using Xunit.Abstractions;

namespace LayoutSharp.Tests;

/// <summary>
/// Owns the detector service for the multi-page integration test. It runs
/// <see cref="LayoutServiceOptions.Offline"/> and warms up only when the model is already cached, so
/// the test no-ops (rather than downloading or failing) on a machine without it — the same contract
/// as <see cref="HeronFixture"/>, kept separate so this file stands on its own.
/// </summary>
public sealed class MultiPageFixture : IAsyncLifetime
{
    /// <summary>Where the detector must be cached for the model-backed test to run.</summary>
    public static string ModelPath { get; } =
        ModelDownloadManager.GetModelPath(ModelRegistry.Get(LayoutModel.DoclingLayoutHeron), null);

    /// <summary>True when the model file is present in the cache.</summary>
    public bool IsAvailable => File.Exists(ModelPath);

    public LayoutService Service { get; } = new(new LayoutServiceOptions
    {
        Model = LayoutModel.DoclingLayoutHeron,
        Offline = true,
    });

    public Task InitializeAsync() => IsAvailable ? Service.WarmUpAsync() : Task.CompletedTask;

    public async Task DisposeAsync() => await Service.DisposeAsync();
}

/// <summary>
/// Model-backed multi-page test: a two-page TIFF built in-test from two of the sample pages must
/// produce exactly what analyzing those two pages one at a time produces — sequentially, in parallel
/// and as a page sequence. Excluded from CI like the rest of the <c>Integration</c> category, and
/// (xUnit 2.9 has no dynamic skip) it returns early with a note when the model is not cached; the
/// fake-detector suite in <c>MultiPageTests</c> covers the same behaviour without a model.
/// </summary>
[Trait("Category", "Integration")]
public class MultiPageIntegrationTests : IClassFixture<MultiPageFixture>
{
    private readonly MultiPageFixture _fixture;
    private readonly LayoutService _svc;
    private readonly ITestOutputHelper _out;

    public MultiPageIntegrationTests(MultiPageFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _svc = fixture.Service;
        _out = output;
    }

    private static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "assets", name);

    /// <summary>
    /// ImageSharp requires every frame of an image to share one size, so each page is scaled to fit a
    /// common canvas and padded with white — exactly what a rasterizer emitting a fixed page size does.
    /// </summary>
    private static Image<Rgb24> OnCanvas(string asset, Size canvas)
    {
        using var source = Image.Load<Rgb24>(AssetPath(asset));
        return source.Clone(c => c.Resize(new ResizeOptions
        {
            Size = canvas,
            Mode = ResizeMode.Pad,
            PadColor = Color.White,
            Sampler = KnownResamplers.Bicubic,
        }));
    }

    /// <summary>The page's blocks as JSON, with the page number normalized so pages compare across documents.</summary>
    private static string Normalize(LayoutPage page)
        => new LayoutDocument { Pages = new[] { page with { PageNumber = 1 } } }.ToJson();

    [Fact]
    public async Task TwoPageTiff_MatchesTheSinglePageRuns_SequentiallyAndInParallel()
    {
        if (!_fixture.IsAvailable)
        {
            _out.WriteLine($"skipped: detector not cached at {MultiPageFixture.ModelPath}");
            return;
        }

        var canvas = new Size(1400, 1000);
        using var page1 = OnCanvas("structure_sample.png", canvas);
        using var page2 = OnCanvas("Test_image1.png", canvas);

        byte[] tiff;
        using (var multi = page1.Clone())
        {
            multi.Frames.AddFrame(page2.Frames.RootFrame);
            using var ms = new MemoryStream();
            await multi.SaveAsync(ms, new TiffEncoder());
            tiff = ms.ToArray();
        }
        Assert.Equal(2, Image.Identify(tiff).FrameCount);

        // Baseline: the two pages analyzed one at a time.
        var single1 = (await _svc.AnalyzeAsync(page1)).Document.Pages[0];
        var single2 = (await _svc.AnalyzeAsync(page2)).Document.Pages[0];
        _out.WriteLine($"single-page baseline: {single1.Blocks.Count} + {single2.Blocks.Count} blocks");
        Assert.NotEmpty(single1.Blocks);
        Assert.NotEmpty(single2.Blocks);

        // Every frame of the TIFF, sequentially.
        var multiResult = await _svc.AnalyzeAllFramesAsync(tiff);
        var pages = multiResult.Document.Pages;
        _out.WriteLine($"two-page TIFF: {pages.Count} pages, {pages[0].Blocks.Count} + {pages[1].Blocks.Count} blocks in {multiResult.Duration.TotalMilliseconds:F0} ms");

        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 1, 2 }, pages.Select(p => p.PageNumber));
        Assert.All(pages, p => Assert.Equal(canvas.Width, p.Width));
        Assert.All(pages, p => Assert.Equal(canvas.Height, p.Height));
        Assert.Equal(single1.Blocks.Count, pages[0].Blocks.Count);
        Assert.Equal(single2.Blocks.Count, pages[1].Blocks.Count);
        Assert.Equal(Normalize(single1), Normalize(pages[0]));
        Assert.Equal(Normalize(single2), Normalize(pages[1]));

        // Reading order restarts at 0 on each page.
        Assert.All(pages, p => Assert.Equal(Enumerable.Range(0, p.Blocks.Count), p.Blocks.Select(b => b.ReadingOrder)));

        // Same pages, analyzed concurrently: identical document, still in page order.
        var parallel = await _svc.AnalyzeAllFramesAsync(tiff, new LayoutAnalysisOptions { PageParallelism = 2 });
        Assert.Equal(multiResult.Document.ToJson(), parallel.Document.ToJson());

        // A caller-supplied page sequence agrees with the multi-frame file.
        var sequence = await _svc.AnalyzePagesAsync(new[] { page1, page2 });
        Assert.Equal(multiResult.Document.ToJson(), sequence.Document.ToJson());

        // The single-image overloads still analyze only the first frame.
        var firstFrameOnly = await _svc.AnalyzeAsync(tiff);
        Assert.Equal(Normalize(single1), Normalize(Assert.Single(firstFrameOnly.Document.Pages)));

        // Multi-page exports carry both pages, separated.
        Assert.Contains("---", multiResult.Document.ToMarkdown());
        var json = multiResult.Document.ToJson();
        var back = LayoutDocument.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(json, back!.ToJson());
        Assert.Equal(new[] { 1, 2 }, back.Pages.Select(p => p.PageNumber));
    }
}
