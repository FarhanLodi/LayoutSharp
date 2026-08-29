using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Recognition;
using LayoutSharp.Services;
using Microsoft.Extensions.DependencyInjection;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// Exercises the whole pipeline (guards, de-duplication, reading order, recognition routing,
/// assembly, disposal) over a scripted detector, so no model download is needed.
/// </summary>
public class LayoutServiceTests
{
    private static readonly LayoutModelSpec Spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

    private static RawDetection Det(string label, double x, double y, double w, double h, float score = 0.9f)
    {
        var cls = Spec.Classes.First(c => c.Name == label);
        return new RawDetection(new LayoutBox(x, y, x + w, y + h), cls, score);
    }

    /// <summary>Returns the same detections for every call and records how it was used.</summary>
    private sealed class ScriptedDetector : ILayoutDetector
    {
        public List<RawDetection> Detections { get; } = new();
        public int Calls;
        public float LastThreshold;
        public bool WarmedUp;
        public bool Disposed;

        public LayoutModel Model => LayoutModel.DoclingLayoutHeron;
        public bool IsGpu => false;

        public Task WarmUpAsync(CancellationToken cancellationToken) { WarmedUp = true; return Task.CompletedTask; }

        public Task<IReadOnlyList<RawDetection>> DetectAsync(Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastThreshold = scoreThreshold;
            return Task.FromResult<IReadOnlyList<RawDetection>>(Detections.Where(d => d.Score >= scoreThreshold).ToList());
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>Returns the crop's size as text so tests can check exactly which region was cropped.</summary>
    private sealed class SizeRecognizer : ITextRecognizer
    {
        public int Calls;
        public int MaxConcurrency;
        private int _inFlight;

        public async Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            do { seen = MaxConcurrency; } while (now > seen && Interlocked.CompareExchange(ref MaxConcurrency, now, seen) != seen);
            await Task.Delay(20, cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            return $"{crop.Width}x{crop.Height}";
        }
    }

    private static LayoutService Create(ScriptedDetector detector, ITextRecognizer? recognizer = null, LayoutServiceOptions? options = null)
        => new(detector, options, recognizer, logger: null);

    [Fact]
    public async Task Analyze_BuildsPage_InReadingOrder_WithMetadata()
    {
        var det = new ScriptedDetector();
        det.Detections.AddRange(new[]
        {
            Det("text", 10, 200, 380, 100),        // body paragraph (below title)
            Det("title", 10, 10, 380, 40),     // title (top)
            Det("picture", 10, 320, 380, 200),       // figure (bottom)
        });
        await using var svc = Create(det);
        using var img = new Image<Rgb24>(400, 600);

        var result = await svc.AnalyzeAsync(img);

        Assert.Equal(LayoutModel.DoclingLayoutHeron, result.Model);
        Assert.False(result.UsedGpu);
        Assert.False(result.TextRecognized);
        Assert.True(result.Duration >= TimeSpan.Zero);

        var page = Assert.Single(result.Document.Pages);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(400, page.Width);
        Assert.Equal(600, page.Height);
        Assert.Equal(new[] { LayoutBlockType.Title, LayoutBlockType.Text, LayoutBlockType.Figure }, page.Blocks.Select(b => b.Type));
        Assert.Equal(new[] { 0, 1, 2 }, page.Blocks.Select(b => b.ReadingOrder));
        Assert.Equal("title", page.Blocks[0].RawClassName);
        Assert.Equal(10, page.Blocks[0].RawClassId);
        Assert.All(page.Blocks, b => Assert.Null(b.Text)); // no recognizer
    }

    [Fact]
    public async Task Analyze_PassesThresholdToDetector_AndValidatesOptions()
    {
        var det = new ScriptedDetector();
        await using var svc = Create(det);
        using var img = new Image<Rgb24>(100, 100);

        await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { ConfidenceThreshold = 0.25f });
        Assert.Equal(0.25f, det.LastThreshold);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.AnalyzeAsync(img, new LayoutAnalysisOptions { ConfidenceThreshold = 1.5f }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognitionParallelism = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.AnalyzeAsync(img, new LayoutAnalysisOptions { DuplicateIouThreshold = -0.1f }));
    }

    [Fact]
    public async Task Analyze_DeduplicatesOverlappingDetections_KeepingHigherScore()
    {
        // The RT-DETR top-k lists the same box under two classes; the better one must win.
        var det = new ScriptedDetector();
        det.Detections.Add(Det("text", 10, 10, 200, 100, 0.55f));
        det.Detections.Add(Det("section_header", 12, 11, 200, 100, 0.80f)); // IoU ≈ 0.98
        det.Detections.Add(Det("table", 10, 300, 200, 100, 0.70f));   // elsewhere
        await using var svc = Create(det);
        using var img = new Image<Rgb24>(400, 600);

        var page = (await svc.AnalyzeAsync(img)).Document.Pages[0];

        Assert.Equal(2, page.Blocks.Count);
        Assert.Contains(page.Blocks, b => b.RawClassName == "section_header" && b.Confidence == 0.80f);
        Assert.DoesNotContain(page.Blocks, b => b.RawClassName == "text");
        Assert.Contains(page.Blocks, b => b.Type == LayoutBlockType.Table);
    }

    [Fact]
    public async Task Analyze_WithRecognizer_FillsOnlyTextBearingBlocks_WithCorrectCrops()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("title", 0, 0, 100, 20));   // text-bearing → 100x20 crop
        det.Detections.Add(Det("picture", 0, 30, 60, 60));       // figure → not recognized
        det.Detections.Add(Det("table", 0, 100, 80, 40));      // table → not recognized
        det.Detections.Add(Det("text", 0, 150, 100, 30));      // text-bearing → 100x30 crop
        var rec = new SizeRecognizer();
        await using var svc = Create(det, rec);
        using var img = new Image<Rgb24>(200, 200);

        var result = await svc.AnalyzeAsync(img);
        var blocks = result.Document.Pages[0].Blocks;

        Assert.True(result.TextRecognized);
        Assert.Equal(2, rec.Calls);
        Assert.Equal("100x20", blocks.Single(b => b.Type == LayoutBlockType.Title).Text);
        Assert.Equal("100x30", blocks.Single(b => b.Type == LayoutBlockType.Text).Text);
        Assert.Null(blocks.Single(b => b.Type == LayoutBlockType.Figure).Text);
        Assert.Null(blocks.Single(b => b.Type == LayoutBlockType.Table).Text);
    }

    [Fact]
    public async Task Analyze_RecognizeTextFalse_SkipsRecognizer()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("text", 0, 0, 100, 20));
        var rec = new SizeRecognizer();
        await using var svc = Create(det, rec);
        using var img = new Image<Rgb24>(200, 200);

        var result = await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognizeText = false });

        Assert.Equal(0, rec.Calls);
        Assert.False(result.TextRecognized);
        Assert.Null(result.Document.Pages[0].Blocks[0].Text);
    }

    [Fact]
    public async Task Analyze_RecognitionParallelism_RunsConcurrently_AndKeepsOrder()
    {
        var det = new ScriptedDetector();
        for (int i = 0; i < 6; i++)
            det.Detections.Add(Det("text", 0, i * 30, 100 + i, 20)); // distinct widths → distinct texts
        var rec = new SizeRecognizer();
        await using var svc = Create(det, rec);
        using var img = new Image<Rgb24>(200, 200);

        var page = (await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognitionParallelism = 4 })).Document.Pages[0];

        Assert.Equal(6, rec.Calls);
        Assert.True(rec.MaxConcurrency > 1, $"expected concurrent recognition, saw max {rec.MaxConcurrency}");
        // Each block's text must still be its own crop, in reading order (top-to-bottom).
        for (int i = 0; i < 6; i++)
            Assert.Equal($"{100 + i}x20", page.Blocks[i].Text);
    }

    [Fact]
    public async Task Analyze_RecognizerReturningEmpty_YieldsNullText()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("text", 0, 0, 100, 20));
        var rec = TextRecognizer.FromDelegate((_, _) => Task.FromResult<string?>("   "));
        await using var svc = Create(det, rec);
        using var img = new Image<Rgb24>(200, 200);

        Assert.Null((await svc.AnalyzeAsync(img)).Document.Pages[0].Blocks[0].Text);
    }

    [Fact]
    public async Task Analyze_TinyRegion_IsNotSentToRecognizer()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("text", 0, 0, 1.2, 1.4)); // < 2px after rounding
        var rec = new SizeRecognizer();
        await using var svc = Create(det, rec);
        using var img = new Image<Rgb24>(200, 200);

        var page = (await svc.AnalyzeAsync(img)).Document.Pages[0];
        Assert.Equal(0, rec.Calls);
        Assert.Single(page.Blocks);
    }

    [Fact]
    public async Task Analyze_ImageOverloads_AllRunPipeline_AndCallerImageIsUntouched()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("text", 0, 0, 10, 10));
        await using var svc = Create(det);
        using var img = new Image<Rgb24>(20, 20, new Rgb24(1, 2, 3));

        // Image<Rgb24>
        var r1 = await svc.AnalyzeAsync(img);
        Assert.Single(r1.Document.Pages[0].Blocks);
        Assert.Equal(new Rgb24(1, 2, 3), img[0, 0]); // untouched, not disposed

        // bytes / ReadOnlyMemory / stream / path
        using var ms = new MemoryStream();
        await img.SaveAsPngAsync(ms);
        var bytes = ms.ToArray();
        Assert.Single((await svc.AnalyzeAsync(bytes)).Document.Pages[0].Blocks);
        Assert.Single((await svc.AnalyzeAsync(new ReadOnlyMemory<byte>(bytes))).Document.Pages[0].Blocks);

        ms.Position = 0;
        Assert.Single((await svc.AnalyzeAsync(ms)).Document.Pages[0].Blocks);

        // Non-seekable stream is buffered transparently.
        await using var nonSeekable = new NonSeekableStream(bytes);
        Assert.Single((await svc.AnalyzeAsync(nonSeekable)).Document.Pages[0].Blocks);

        var path = Path.Combine(Path.GetTempPath(), $"layoutsharp-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Single((await svc.AnalyzeAsync(path)).Document.Pages[0].Blocks);
        }
        finally { File.Delete(path); }

        Assert.Equal(6, det.Calls);
    }

    [Fact]
    public async Task Analyze_ArgumentGuards()
    {
        await using var svc = Create(new ScriptedDetector());
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AnalyzeAsync(""));
        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.AnalyzeAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".png")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.AnalyzeAsync((Stream)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.AnalyzeAsync((byte[])null!));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AnalyzeAsync(ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.AnalyzeAsync((Image<Rgb24>)null!));
    }

    [Fact]
    public async Task Analyze_ImageTooLarge_IsRejected_BeforeDecoding()
    {
        var det = new ScriptedDetector();
        await using var svc = Create(det, options: new LayoutServiceOptions { MaxImagePixels = 1000 });

        using var big = new Image<Rgb24>(50, 50); // 2500 px > 1000
        await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.AnalyzeAsync(big));

        using var ms = new MemoryStream();
        await big.SaveAsPngAsync(ms);
        await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.AnalyzeAsync(ms.ToArray()));
        ms.Position = 0;
        await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.AnalyzeAsync(ms));

        Assert.Equal(0, det.Calls);

        using var small = new Image<Rgb24>(20, 20); // 400 px OK
        await svc.AnalyzeAsync(small);
        Assert.Equal(1, det.Calls);
    }

    [Fact]
    public async Task WarmUp_ForwardsToDetector()
    {
        var det = new ScriptedDetector();
        await using var svc = Create(det);
        await svc.WarmUpAsync();
        Assert.True(det.WarmedUp);
    }

    [Fact]
    public async Task Dispose_ReleasesDetector_AndBlocksFurtherUse()
    {
        var det = new ScriptedDetector();
        var svc = Create(det);
        await svc.DisposeAsync();
        await svc.DisposeAsync(); // idempotent
        svc.Dispose();

        Assert.True(det.Disposed);
        using var img = new Image<Rgb24>(10, 10);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.AnalyzeAsync(img));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.WarmUpAsync());
    }

    [Fact]
    public void ServiceOptions_AreValidated_AndCopied()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutService(new LayoutServiceOptions { MaxImagePixels = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutService(new LayoutServiceOptions { Model = (LayoutModel)42 }));
        Assert.Throws<ArgumentNullException>(() => new LayoutService((LayoutServiceOptions)null!));

        var options = new LayoutServiceOptions { Model = LayoutModel.DoclingLayoutHeron, MaxImagePixels = 1_000_000 };
        using var svc = new LayoutService(options);
        options.Model = (LayoutModel)42;   // later mutation must not leak into the service
        options.MaxImagePixels = 1;
        Assert.Equal(LayoutModel.DoclingLayoutHeron, svc.Model);
        Assert.False(svc.HasRecognizer);
    }

    [Fact]
    public void AddLayoutSharp_RegistersSingleton_AndPicksUpRecognizer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITextRecognizer>(new SizeRecognizer());
        services.AddLayoutSharp(o => o.Model = LayoutModel.DoclingLayoutHeron);
        using var provider = services.BuildServiceProvider();

        var a = provider.GetRequiredService<ILayoutService>();
        var b = provider.GetRequiredService<ILayoutService>();
        Assert.Same(a, b);
        Assert.Equal(LayoutModel.DoclingLayoutHeron, a.Model);
        Assert.True(((LayoutService)a).HasRecognizer);
    }

    [Fact]
    public void AddLayoutSharp_Generic_RegistersRecognizerType()
    {
        var services = new ServiceCollection();
        services.AddLayoutSharp<SizeRecognizer>();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SizeRecognizer>(provider.GetRequiredService<ITextRecognizer>());
        Assert.True(((LayoutService)provider.GetRequiredService<ILayoutService>()).HasRecognizer);
    }

    [Fact]
    public void AddLayoutSharp_WithoutRecognizer_IsLayoutOnly()
    {
        var services = new ServiceCollection();
        services.AddLayoutSharp();
        using var provider = services.BuildServiceProvider();
        Assert.False(((LayoutService)provider.GetRequiredService<ILayoutService>()).HasRecognizer);
    }

    [Fact]
    public void Deduplicate_KeepsNestedRegions_WithLowIou()
    {
        // A caption inside a large figure has low IoU with it and must survive.
        var figure = Det("picture", 0, 0, 400, 400, 0.9f);
        var caption = Det("caption", 10, 370, 200, 20, 0.8f);
        var kept = LayoutService.Deduplicate(new[] { figure, caption }, 0.6f);
        Assert.Equal(2, kept.Count);
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
