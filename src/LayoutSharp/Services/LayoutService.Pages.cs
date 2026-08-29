using System.Diagnostics;
using LayoutSharp.Models;
using Microsoft.Extensions.Logging;
using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;

namespace LayoutSharp.Services;

/// <summary>
/// Multi-page entry points: a caller-supplied page sequence (<see cref="AnalyzePagesAsync"/>) and
/// every frame of a multi-frame image (<c>AnalyzeAllFramesAsync</c>). Both feed the same per-page
/// core as the single-image overloads and assemble one <see cref="LayoutDocument"/> with pages
/// numbered 1..N.
/// </summary>
public sealed partial class LayoutService
{
    /// <summary>One page queued for analysis; <see cref="Owned"/> pages are disposed by the service once analyzed.</summary>
    private readonly record struct PageInput(Image<Rgb24> Image, int PageNumber, bool Owned);

    /// <inheritdoc />
    public Task<LayoutResult> AnalyzePagesAsync(IEnumerable<Image<Rgb24>> pages, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(pages);

        // Fail fast for materialized collections; lazy sequences are checked as pages are pulled.
        if (pages.TryGetNonEnumeratedCount(out int count))
            GuardPageCount(count);

        return RunMultiPageAsync(EnumerateCallerPages(pages), options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LayoutResult> AnalyzeAllFramesAsync(string imagePath, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        cancellationToken.ThrowIfCancellationRequested();
        var info = await Image.IdentifyAsync(fullPath, cancellationToken).ConfigureAwait(false);
        GuardFrames(info);

        using var image = await Image.LoadAsync<Rgb24>(fullPath, FrameDecoderOptions(), cancellationToken).ConfigureAwait(false);
        return await RunAllFramesAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LayoutResult> AnalyzeAllFramesAsync(Stream imageStream, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(imageStream);

        Stream source = imageStream;
        MemoryStream? buffered = null;
        try
        {
            if (!source.CanSeek)
            {
                // Identify + Load need two passes; buffer non-seekable streams once.
                buffered = new MemoryStream();
                await source.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
                buffered.Position = 0;
                source = buffered;
            }

            long start = source.Position;
            var info = await Image.IdentifyAsync(source, cancellationToken).ConfigureAwait(false);
            GuardFrames(info);
            source.Position = start;

            using var image = await Image.LoadAsync<Rgb24>(source, FrameDecoderOptions(), cancellationToken).ConfigureAwait(false);
            return await RunAllFramesAsync(image, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <inheritdoc />
    public Task<LayoutResult> AnalyzeAllFramesAsync(byte[] imageBytes, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return AnalyzeAllFramesAsync(new ReadOnlyMemory<byte>(imageBytes), options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LayoutResult> AnalyzeAllFramesAsync(ReadOnlyMemory<byte> imageBytes, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (imageBytes.IsEmpty)
            throw new ArgumentException("Image bytes must not be empty.", nameof(imageBytes));

        cancellationToken.ThrowIfCancellationRequested();
        var info = Image.Identify(imageBytes.Span);
        GuardFrames(info);

        using var image = Image.Load<Rgb24>(imageBytes.Span, FrameDecoderOptions());
        return await RunAllFramesAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<LayoutResult> AnalyzeAllFramesAsync(Image<Rgb24> image, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        GuardImageSize(image.Width, image.Height);
        // Caller owns the image: only per-frame clones are created (and disposed) here.
        return RunAllFramesAsync(image, options, cancellationToken);
    }

    // ---- multi-page pipeline ----

    /// <summary>
    /// Runs the per-page core over every frame of <paramref name="image"/>. Each frame is cloned into
    /// a single-frame image first: the detector's resize and the recognizer crops clone the whole
    /// image, so handing them a multi-frame image would copy every frame per page.
    /// </summary>
    private Task<LayoutResult> RunAllFramesAsync(Image<Rgb24> image, LayoutAnalysisOptions? options, CancellationToken ct)
    {
        // Decoding is capped at MaxPages + 1 frames, so an over-long file that slipped past the header check lands here.
        GuardPageCount(image.Frames.Count);
        return RunMultiPageAsync(EnumerateFrames(image), options, ct);
    }

    private async Task<LayoutResult> RunMultiPageAsync(IEnumerable<PageInput> inputs, LayoutAnalysisOptions? options, CancellationToken ct)
    {
        options ??= LayoutAnalysisOptions.Default;
        options.Validate();
        var sw = Stopwatch.StartNew();

        var pages = options.PageParallelism <= 1
            ? await RunPagesSequentiallyAsync(inputs, options, ct).ConfigureAwait(false)
            : await RunPagesConcurrentlyAsync(inputs, options, ct).ConfigureAwait(false);

        sw.Stop();
        bool recognize = options.RecognizeText && _recognizer is not null;
        _logger?.LogInformation("Layout analysis: {Pages} pages, {Count} blocks in {Ms:F0} ms{Ocr}",
            pages.Count, pages.Sum(p => p.Blocks.Count), sw.Elapsed.TotalMilliseconds, recognize ? " incl. text recognition" : "");

        return new LayoutResult
        {
            Document = new LayoutDocument { Pages = pages },
            Duration = sw.Elapsed,
            Model = _detector.Model,
            UsedGpu = _detector.IsGpu,
            TextRecognized = recognize,
        };
    }

    /// <summary>Pages one after another, in order, on the calling context; the source is pulled lazily.</summary>
    private async Task<IReadOnlyList<LayoutPage>> RunPagesSequentiallyAsync(IEnumerable<PageInput> inputs, LayoutAnalysisOptions options, CancellationToken ct)
    {
        var pages = new List<LayoutPage>();
        foreach (var input in inputs)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                pages.Add(await AnalyzeOnePageAsync(input, options, ct).ConfigureAwait(false));
            }
            finally
            {
                if (input.Owned) input.Image.Dispose();
            }
        }
        return pages;
    }

    /// <summary>
    /// Up to <see cref="LayoutAnalysisOptions.PageParallelism"/> pages at once on the thread pool
    /// (inference is synchronous once the session exists, so real concurrency needs the pool). The
    /// source is still pulled one page at a time, in order; results are re-sorted by page number.
    /// </summary>
    private async Task<IReadOnlyList<LayoutPage>> RunPagesConcurrentlyAsync(IEnumerable<PageInput> inputs, LayoutAnalysisOptions options, CancellationToken ct)
    {
        var pages = new List<LayoutPage>();
        await Parallel.ForEachAsync(
            inputs,
            new ParallelOptions { MaxDegreeOfParallelism = options.PageParallelism, CancellationToken = ct },
            async (input, token) =>
            {
                try
                {
                    var page = await AnalyzeOnePageAsync(input, options, token).ConfigureAwait(false);
                    lock (pages) pages.Add(page);
                }
                finally
                {
                    if (input.Owned) input.Image.Dispose();
                }
            })
            .ConfigureAwait(false);

        pages.Sort((a, b) => a.PageNumber.CompareTo(b.PageNumber));
        return pages;
    }

    private async Task<LayoutPage> AnalyzeOnePageAsync(PageInput input, LayoutAnalysisOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (page, rawCount) = await AnalyzePageAsync(input.Image, input.PageNumber, options, ct).ConfigureAwait(false);
        _logger?.LogDebug("Page {Page}: {Count} blocks ({Raw} raw detections) in {Ms:F0} ms",
            page.PageNumber, page.Blocks.Count, rawCount, sw.Elapsed.TotalMilliseconds);
        return page;
    }

    /// <summary>Caller-owned pages: validated (null, page limit, pixel guard) as they are pulled, never disposed.</summary>
    private IEnumerable<PageInput> EnumerateCallerPages(IEnumerable<Image<Rgb24>> pages)
    {
        int n = 0;
        foreach (var image in pages)
        {
            n++;
            if (image is null)
                throw new ArgumentException($"Page {n} of the sequence is null.", nameof(pages));
            GuardPageCount(n);
            GuardImageSize(image.Width, image.Height);
            yield return new PageInput(image, n, Owned: false);
        }
    }

    /// <summary>
    /// One single-frame clone per frame, owned by the pipeline and disposed once analyzed. Each
    /// frame is size-guarded on its own dimensions before it is cloned: a multi-frame file may hold
    /// frames of differing sizes, so the header check in <see cref="GuardFrames"/> — which sees only
    /// the container's dimensions — is not sufficient on its own.
    /// </summary>
    private IEnumerable<PageInput> EnumerateFrames(Image<Rgb24> image)
    {
        for (int i = 0; i < image.Frames.Count; i++)
        {
            var frame = image.Frames[i];
            GuardImageSize(frame.Width, frame.Height);
            yield return new PageInput(image.Frames.CloneFrame(i), i + 1, Owned: true);
        }
    }

    // ---- guards ----

    /// <summary>Rejects an over-long or over-sized multi-frame file from its header, before pixels are decoded.</summary>
    private void GuardFrames(ImageInfo info)
    {
        // Single-frame formats may report no frames at all.
        int frames = Math.Max(1, info.FrameCount);
        if (frames > _options.MaxPages)
        {
            throw new TooManyPagesException(
                $"The image has {frames:N0} frames, exceeding the configured limit of {_options.MaxPages:N0} pages " +
                "(LayoutServiceOptions.MaxPages). Raise the limit or split the file. This guard bounds the memory and " +
                "time one call can consume.", _options.MaxPages);
        }
        // Only the container's dimensions are known from the header; frames may differ in size, so
        // each one is guarded again in EnumerateFrames before it is decoded into a page.
        GuardImageSize(info.Width, info.Height);
    }

    private void GuardPageCount(int count)
    {
        if (count > _options.MaxPages)
        {
            throw new TooManyPagesException(
                $"The input has more than {_options.MaxPages:N0} pages, exceeding the configured limit " +
                "(LayoutServiceOptions.MaxPages). Raise the limit or split the document. This guard bounds the memory and " +
                "time one call can consume.", _options.MaxPages);
        }
    }

    /// <summary>
    /// Decoder options for multi-frame loads: decode at most <c>MaxPages + 1</c> frames so a file that
    /// slipped past the header check is still bounded, while one frame too many remains detectable.
    /// </summary>
    private DecoderOptions FrameDecoderOptions()
        => new() { MaxFrames = (int)Math.Min((long)_options.MaxPages + 1, int.MaxValue) };
}
