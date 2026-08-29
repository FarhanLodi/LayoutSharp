using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Recognition;
using LayoutSharp.Services;
using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// Multi-page entry points (<c>AnalyzePagesAsync</c>, <c>AnalyzeAllFramesAsync</c>) over a detector
/// whose output is keyed by the image's content, so every page (or frame) can be told apart:
/// page numbering, per-page reading order, parallelism/order, cancellation, page and pixel guards,
/// ownership, and multi-frame TIFF/GIF decoding — all without a model.
/// </summary>
public class MultiPageTests
{
    private const LayoutModel FakeModel = LayoutModel.DoclingLayoutHeron;
    private static readonly RawClass TextClass = ModelRegistry.Get(FakeModel).Classes.First(c => c.Type == LayoutBlockType.Text);

    /// <summary>
    /// Pixel (0,0) encodes the page: red = block count × <see cref="PixelKeyedDetector.BlockStep"/>
    /// (spaced out so a GIF/TIFF palette round-trip cannot merge two pages), green = detection delay in ms.
    /// </summary>
    private static Image<Rgb24> Page(int width, int height, int blocks, int delayMs = 0)
        => new(width, height, new Rgb24((byte)(blocks * PixelKeyedDetector.BlockStep), (byte)delayMs, 0));

    /// <summary>
    /// Returns as many stacked text detections as the pixel at (0,0) encodes, waiting the encoded
    /// delay first, and records calls / peak concurrency. A hook can cancel mid-run.
    /// </summary>
    private sealed class PixelKeyedDetector : ILayoutDetector
    {
        public const int BlockStep = 40;

        public int Calls;
        public int MaxConcurrency;
        private int _inFlight;
        public Action<int>? OnDetect;

        public LayoutModel Model => FakeModel;
        public bool IsGpu => false;
        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<IReadOnlyList<RawDetection>> DetectAsync(Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref Calls);
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            do { seen = MaxConcurrency; } while (now > seen && Interlocked.CompareExchange(ref MaxConcurrency, now, seen) != seen);
            try
            {
                OnDetect?.Invoke(call);
                var px = image[0, 0];
                if (px.G > 0) await Task.Delay(px.G, cancellationToken);
                int blocks = (int)Math.Round(px.R / (double)BlockStep);
                var list = new List<RawDetection>(blocks);
                for (int i = 0; i < blocks; i++)
                    list.Add(new RawDetection(new LayoutBox(2, i * 4, 12, i * 4 + 3), TextClass, 0.9f));
                return list;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static LayoutService Create(PixelKeyedDetector detector, ITextRecognizer? recognizer = null, LayoutServiceOptions? options = null)
        => new(detector, options, recognizer, logger: null);

    private static byte[] Encode(IImageEncoder encoder, params Image<Rgb24>[] frames)
    {
        using var img = frames[0].Clone();
        for (int i = 1; i < frames.Length; i++) img.Frames.AddFrame(frames[i].Frames.RootFrame);
        using var ms = new MemoryStream();
        img.Save(ms, encoder);
        return ms.ToArray();
    }

    private static void AssertPage(LayoutPage page, int number, int width, int height, int blocks)
    {
        Assert.Equal(number, page.PageNumber);
        Assert.Equal(width, page.Width);
        Assert.Equal(height, page.Height);
        Assert.Equal(blocks, page.Blocks.Count);
        Assert.Equal(Enumerable.Range(0, blocks), page.Blocks.Select(b => b.ReadingOrder)); // restarts at 0 on every page
    }

    // ---- AnalyzePagesAsync ----

    [Fact]
    public async Task AnalyzePages_ThreePages_AreNumberedInOrder_WithPerPageReadingOrder()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        using var p1 = Page(100, 100, 1);
        using var p2 = Page(200, 150, 2);
        using var p3 = Page(300, 120, 3);

        var result = await svc.AnalyzePagesAsync(new[] { p1, p2, p3 });

        Assert.Equal(3, det.Calls);
        Assert.Equal(3, result.Document.Pages.Count);
        AssertPage(result.Document.Pages[0], 1, 100, 100, 1);
        AssertPage(result.Document.Pages[1], 2, 200, 150, 2);
        AssertPage(result.Document.Pages[2], 3, 300, 120, 3);
        Assert.Equal(FakeModel, result.Model);
        Assert.False(result.UsedGpu);
        Assert.False(result.TextRecognized);
        Assert.True(result.Duration >= TimeSpan.Zero);

        // Caller keeps ownership: images are untouched and still usable.
        Assert.Equal(new Rgb24(3 * PixelKeyedDetector.BlockStep, 0, 0), p3[0, 0]);
    }

    [Fact]
    public async Task AnalyzePages_EmptySequence_YieldsEmptyDocument()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);

        var result = await svc.AnalyzePagesAsync(Array.Empty<Image<Rgb24>>());

        Assert.Empty(result.Document.Pages);
        Assert.Equal(0, det.Calls);
        Assert.Equal(FakeModel, result.Model);
        Assert.Equal("", result.Document.ToMarkdown());
    }

    [Fact]
    public async Task AnalyzePages_PageParallelism_RunsConcurrently_AndKeepsPageOrder()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        // Page 1 is the slowest, page 6 the fastest, so completion order is the reverse of page order.
        var pages = Enumerable.Range(1, 6).Select(i => Page(50 + i, 40, blocks: i, delayMs: (7 - i) * 30)).ToArray();
        try
        {
            var result = await svc.AnalyzePagesAsync(pages, new LayoutAnalysisOptions { PageParallelism = 3 });

            Assert.True(det.MaxConcurrency > 1, $"expected concurrent pages, saw max {det.MaxConcurrency}");
            Assert.True(det.MaxConcurrency <= 3, $"parallelism exceeded: {det.MaxConcurrency}");
            Assert.Equal(6, result.Document.Pages.Count);
            for (int i = 0; i < 6; i++)
                AssertPage(result.Document.Pages[i], i + 1, 50 + i + 1, 40, i + 1);
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzePages_Sequential_NeverOverlapsPages()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        var pages = Enumerable.Range(1, 4).Select(i => Page(40, 40, 1, delayMs: 15)).ToArray();
        try
        {
            await svc.AnalyzePagesAsync(pages);
            Assert.Equal(1, det.MaxConcurrency);
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzePages_Cancellation_StopsBeforeTheNextPage()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        using var cts = new CancellationTokenSource();
        det.OnDetect = call => { if (call == 2) cts.Cancel(); }; // cancel while page 2 is being detected
        var pages = Enumerable.Range(1, 4).Select(i => Page(40, 40, 1)).ToArray();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.AnalyzePagesAsync(pages, cancellationToken: cts.Token));
            Assert.Equal(2, det.Calls); // page 3 was never started
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzePages_Cancellation_WithParallelism_StopsPullingPages()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        using var cts = new CancellationTokenSource();
        det.OnDetect = call => { if (call == 1) cts.Cancel(); };
        var pages = Enumerable.Range(1, 8).Select(i => Page(40, 40, 1, delayMs: 20)).ToArray();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.AnalyzePagesAsync(pages, new LayoutAnalysisOptions { PageParallelism = 2 }, cts.Token));
            Assert.True(det.Calls <= 2, $"expected at most the two in-flight pages, saw {det.Calls}");
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzePages_MaxPagesExceeded_Throws()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det, options: new LayoutServiceOptions { MaxPages = 2 });
        var pages = Enumerable.Range(1, 3).Select(i => Page(40, 40, 1)).ToArray();
        try
        {
            // A materialized collection fails before any page is analyzed.
            var ex = await Assert.ThrowsAsync<TooManyPagesException>(() => svc.AnalyzePagesAsync(pages));
            Assert.Equal(2, ex.MaxPages);
            Assert.IsAssignableFrom<LayoutSharpException>(ex);
            Assert.Equal(0, det.Calls);

            // A lazy sequence fails when page MaxPages + 1 is pulled.
            IEnumerable<Image<Rgb24>> Lazy() { foreach (var p in pages) yield return p; }
            await Assert.ThrowsAsync<TooManyPagesException>(() => svc.AnalyzePagesAsync(Lazy()));
            Assert.Equal(2, det.Calls);

            // Exactly MaxPages is fine.
            var ok = await svc.AnalyzePagesAsync(pages.Take(2));
            Assert.Equal(2, ok.Document.Pages.Count);
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    [Fact]
    public async Task AnalyzePages_ImageTooLarge_IsRejectedPerPage()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det, options: new LayoutServiceOptions { MaxImagePixels = 1000 });
        using var small = Page(20, 20, 1);
        using var big = Page(50, 50, 1);

        await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.AnalyzePagesAsync(new[] { small, big }));
        Assert.Equal(1, det.Calls); // the small page ran, the big one was rejected before detection
    }

    [Fact]
    public async Task AnalyzePages_StreamingIterator_MayDisposeEachPageAfterTheNextIsPulled()
    {
        // The documented streaming contract for PageParallelism = 1: a rasterizer can yield one page
        // at a time and dispose it once the next is requested, keeping a single page in memory.
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        static IEnumerable<Image<Rgb24>> Rasterize()
        {
            for (int i = 1; i <= 3; i++)
            {
                using var page = Page(30 * i, 30, i);
                yield return page;
            }
        }

        var result = await svc.AnalyzePagesAsync(Rasterize());

        Assert.Equal(3, result.Document.Pages.Count);
        for (int i = 0; i < 3; i++) AssertPage(result.Document.Pages[i], i + 1, 30 * (i + 1), 30, i + 1);
    }

    [Fact]
    public async Task AnalyzePages_WithRecognizer_FillsTextOnEveryPage()
    {
        var det = new PixelKeyedDetector();
        var rec = TextRecognizer.FromDelegate((crop, _) => Task.FromResult<string?>($"{crop.Width}x{crop.Height}"));
        await using var svc = Create(det, rec);
        using var p1 = Page(40, 40, 1);
        using var p2 = Page(40, 40, 2);

        var result = await svc.AnalyzePagesAsync(new[] { p1, p2 }, new LayoutAnalysisOptions { PageParallelism = 2 });

        Assert.True(result.TextRecognized);
        Assert.All(result.Document.Pages.SelectMany(p => p.Blocks), b => Assert.Equal("10x3", b.Text));
        Assert.Equal(3, result.Document.Pages.Sum(p => p.Blocks.Count));
        Assert.Contains("\n\n", result.Document.ToPlainText().ReplaceLineEndings("\n")); // page separator
    }

    [Fact]
    public async Task AnalyzePages_ArgumentGuards_AndOptionValidation()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        using var ok = Page(20, 20, 1);

        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.AnalyzePagesAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AnalyzePagesAsync(new Image<Rgb24>[] { ok, null! }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.AnalyzePagesAsync(new[] { ok }, new LayoutAnalysisOptions { PageParallelism = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutService(new LayoutServiceOptions { MaxPages = 0 }));

        var svc2 = Create(det);
        await svc2.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc2.AnalyzePagesAsync(new[] { ok }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc2.AnalyzeAllFramesAsync(ok));
    }

    // ---- AnalyzeAllFramesAsync ----

    [Fact]
    public async Task AnalyzeAllFrames_TwoFrameTiff_RoundTrips_ThroughEveryOverload()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        using var f1 = Page(120, 90, 1);
        using var f2 = Page(120, 90, 2);
        var tiff = Encode(new TiffEncoder(), f1, f2);

        async Task Check(Task<LayoutResult> call)
        {
            var result = await call;
            Assert.Equal(2, result.Document.Pages.Count);
            AssertPage(result.Document.Pages[0], 1, 120, 90, 1);
            AssertPage(result.Document.Pages[1], 2, 120, 90, 2);
        }

        await Check(svc.AnalyzeAllFramesAsync(tiff));
        await Check(svc.AnalyzeAllFramesAsync(new ReadOnlyMemory<byte>(tiff)));
        using (var ms = new MemoryStream(tiff)) await Check(svc.AnalyzeAllFramesAsync(ms));
        await using (var nonSeekable = new NonSeekableStream(tiff)) await Check(svc.AnalyzeAllFramesAsync(nonSeekable));
        using (var decoded = Image.Load<Rgb24>(tiff))
        {
            Assert.Equal(2, decoded.Frames.Count);
            await Check(svc.AnalyzeAllFramesAsync(decoded));
            Assert.Equal(2, decoded.Frames.Count); // caller's image untouched
        }
        var path = Path.Combine(Path.GetTempPath(), $"layoutsharp-{Guid.NewGuid():N}.tif");
        try
        {
            await File.WriteAllBytesAsync(path, tiff);
            await Check(svc.AnalyzeAllFramesAsync(path));
        }
        finally { File.Delete(path); }
        Assert.Equal(12, det.Calls);

        // The single-image overloads keep analyzing only the first frame.
        var single = await svc.AnalyzeAsync(tiff);
        AssertPage(Assert.Single(single.Document.Pages), 1, 120, 90, 1);
    }

    [Fact]
    public async Task AnalyzeAllFrames_AnimatedGif_OnePagePerFrame_InOrder()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        using var f1 = Page(64, 48, 3);
        using var f2 = Page(64, 48, 1);
        using var f3 = Page(64, 48, 2);
        var gif = Encode(new GifEncoder(), f1, f2, f3);

        var result = await svc.AnalyzeAllFramesAsync(gif, new LayoutAnalysisOptions { PageParallelism = 3 });

        Assert.Equal(3, result.Document.Pages.Count);
        AssertPage(result.Document.Pages[0], 1, 64, 48, 3);
        AssertPage(result.Document.Pages[1], 2, 64, 48, 1);
        AssertPage(result.Document.Pages[2], 3, 64, 48, 2);
    }

    [Fact]
    public async Task AnalyzeAllFrames_SingleFramePng_YieldsOnePage()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det);
        using var img = Page(30, 20, 2);
        using var ms = new MemoryStream();
        await img.SaveAsPngAsync(ms);

        var result = await svc.AnalyzeAllFramesAsync(ms.ToArray());
        AssertPage(Assert.Single(result.Document.Pages), 1, 30, 20, 2);
    }

    [Fact]
    public async Task AnalyzeAllFrames_TooManyFrames_IsRejected_BeforeDecoding()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det, options: new LayoutServiceOptions { MaxPages = 2 });
        using var f = Page(20, 20, 1);
        var tiff = Encode(new TiffEncoder(), f, f, f);

        var ex = await Assert.ThrowsAsync<TooManyPagesException>(() => svc.AnalyzeAllFramesAsync(tiff));
        Assert.Equal(2, ex.MaxPages);
        using (var ms = new MemoryStream(tiff))
            await Assert.ThrowsAsync<TooManyPagesException>(() => svc.AnalyzeAllFramesAsync(ms));
        using (var decoded = Image.Load<Rgb24>(tiff))
            await Assert.ThrowsAsync<TooManyPagesException>(() => svc.AnalyzeAllFramesAsync(decoded));
        Assert.Equal(0, det.Calls);

        // Two frames are within the limit.
        var ok = await svc.AnalyzeAllFramesAsync(Encode(new TiffEncoder(), f, f));
        Assert.Equal(2, ok.Document.Pages.Count);
    }

    [Fact]
    public async Task AnalyzeAllFrames_ImageTooLarge_IsRejected_BeforeDecoding()
    {
        var det = new PixelKeyedDetector();
        await using var svc = Create(det, options: new LayoutServiceOptions { MaxImagePixels = 1000 });
        using var f = Page(50, 50, 1); // 2500 px per frame
        var tiff = Encode(new TiffEncoder(), f, f);

        await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.AnalyzeAllFramesAsync(tiff));
        using (var decoded = Image.Load<Rgb24>(tiff))
            await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.AnalyzeAllFramesAsync(decoded));
        Assert.Equal(0, det.Calls);
    }

    [Fact]
    public async Task AnalyzeAllFrames_ArgumentGuards()
    {
        await using var svc = Create(new PixelKeyedDetector());
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AnalyzeAllFramesAsync(""));
        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.AnalyzeAllFramesAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".tif")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.AnalyzeAllFramesAsync((Stream)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.AnalyzeAllFramesAsync((byte[])null!));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AnalyzeAllFramesAsync(ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.AnalyzeAllFramesAsync((Image<Rgb24>)null!));
    }

    /// <summary>
    /// Documents an ImageSharp 3.1 limitation rather than a LayoutSharp choice: a multi-page TIFF whose
    /// pages differ in size can be identified but not decoded ("Images with different sizes are not
    /// supported"). If this test starts failing after an ImageSharp upgrade, the README caveat can go.
    /// </summary>
    [Fact]
    public async Task AnalyzeAllFrames_MixedSizeTiff_DecodesEveryFrameAtItsOwnSize()
    {
        await using var svc = Create(new PixelKeyedDetector());
        var tiff = HandcraftedTiff((40, 30), (60, 20));

        Assert.Equal(2, Image.Identify(tiff).FrameCount);

        // Frames of differing sizes decode: each page reports its own dimensions, not the container's.
        var result = await svc.AnalyzeAllFramesAsync(tiff);
        Assert.Equal(2, result.Document.Pages.Count);
        Assert.Equal((40, 30), (result.Document.Pages[0].Width, result.Document.Pages[0].Height));
        Assert.Equal((60, 20), (result.Document.Pages[1].Width, result.Document.Pages[1].Height));
    }

    [Fact]
    public async Task AnalyzeAllFrames_GuardsEachFrameSize_NotJustTheFirst()
    {
        // The header check only sees the container's dimensions. A file whose first frame is small
        // and whose second is oversized must still be rejected, or the pixel guard is bypassable.
        await using var svc = Create(new PixelKeyedDetector(), options: new LayoutServiceOptions { MaxImagePixels = 2_000 });
        var tiff = HandcraftedTiff((40, 30), (200, 200));   // 1,200 px then 40,000 px

        await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.AnalyzeAllFramesAsync(tiff));
    }

    /// <summary>Minimal little-endian, uncompressed RGB TIFF with one IFD per page (pixel (0,0) red = 1).</summary>
    private static byte[] HandcraftedTiff(params (int W, int H)[] pages)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((byte)'I'); bw.Write((byte)'I'); bw.Write((ushort)42);
        long nextIfdPointer = ms.Position;
        bw.Write(0u);
        var stripOffsets = new uint[pages.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            stripOffsets[i] = (uint)ms.Position;
            var (w, h) = pages[i];
            var strip = new byte[w * h * 3];
            for (int p = 0; p < w * h; p++) strip[p * 3] = 1;
            bw.Write(strip);
            if (ms.Position % 2 == 1) bw.Write((byte)0);
        }
        for (int i = 0; i < pages.Length; i++)
        {
            var (w, h) = pages[i];
            uint bitsPerSample = (uint)ms.Position;
            bw.Write((ushort)8); bw.Write((ushort)8); bw.Write((ushort)8);
            uint ifd = (uint)ms.Position;
            long here = ms.Position; ms.Position = nextIfdPointer; bw.Write(ifd); ms.Position = here;
            var entries = new (ushort Tag, ushort Type, uint Count, uint Value)[]
            {
                (256, 4, 1, (uint)w),           // ImageWidth
                (257, 4, 1, (uint)h),           // ImageLength
                (258, 3, 3, bitsPerSample),     // BitsPerSample -> offset
                (259, 3, 1, 1),                 // Compression: none
                (262, 3, 1, 2),                 // Photometric: RGB
                (273, 4, 1, stripOffsets[i]),   // StripOffsets
                (277, 3, 1, 3),                 // SamplesPerPixel
                (278, 4, 1, (uint)h),           // RowsPerStrip
                (279, 4, 1, (uint)(w * h * 3)), // StripByteCounts
            };
            bw.Write((ushort)entries.Length);
            foreach (var e in entries)
            {
                bw.Write(e.Tag); bw.Write(e.Type); bw.Write(e.Count);
                if (e.Type == 3 && e.Count == 1) { bw.Write((ushort)e.Value); bw.Write((ushort)0); }
                else bw.Write(e.Value);
            }
            nextIfdPointer = ms.Position;
            bw.Write(0u);
        }
        return ms.ToArray();
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
