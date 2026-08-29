using LayoutSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace LayoutSharp.Services;

/// <summary>
/// Analyzes a document page image into a typed, reading-ordered block graph, optionally
/// recognizing text regions through a pluggable <see cref="Recognition.ITextRecognizer"/>.
/// Register with <c>services.AddLayoutSharp()</c>, or construct <see cref="LayoutService"/> directly.
/// </summary>
public interface ILayoutService : IAsyncDisposable, IDisposable
{
    /// <summary>The detector this service runs.</summary>
    LayoutModel Model { get; }

    /// <summary>
    /// Downloads the model if needed and creates the inference session, so the first
    /// <c>AnalyzeAsync</c> call does not pay the cold-start cost. Also the way to pre-seed a cache
    /// for offline deployment.
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>Analyze an image file on disk.</summary>
    Task<LayoutResult> AnalyzeAsync(
        string imagePath,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Analyze an image from a stream (format auto-detected).</summary>
    Task<LayoutResult> AnalyzeAsync(
        Stream imageStream,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Analyze an image from an encoded byte array (PNG/JPEG/etc.).</summary>
    Task<LayoutResult> AnalyzeAsync(
        byte[] imageBytes,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Analyze an image from encoded bytes.</summary>
    Task<LayoutResult> AnalyzeAsync(
        ReadOnlyMemory<byte> imageBytes,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze an already-decoded image. The caller retains ownership of the image
    /// (it is neither mutated nor disposed by this method).
    /// </summary>
    Task<LayoutResult> AnalyzeAsync(
        Image<Rgb24> image,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze a sequence of already-rasterized pages (for example PDF pages rendered by a rasterizer
    /// of your choice) into one <see cref="LayoutDocument"/> whose pages are numbered 1..N in sequence
    /// order. The caller retains ownership of every image (none is mutated or disposed). With
    /// <see cref="LayoutAnalysisOptions.PageParallelism"/> = 1 (default) the sequence is enumerated
    /// lazily and strictly in order — each image is fully analyzed before the next one is pulled — so
    /// a streaming iterator that yields one rasterized page at a time and disposes it afterwards keeps
    /// a single page in memory. With <c>PageParallelism</c> &gt; 1 up to that many images are pulled
    /// ahead and analyzed concurrently, so keep the images alive until the returned task completes.
    /// Reading order restarts at 0 on every page; <see cref="LayoutResult.Duration"/> is the total.
    /// An empty sequence yields a document with no pages.
    /// </summary>
    /// <exception cref="TooManyPagesException">The sequence has more than <see cref="LayoutServiceOptions.MaxPages"/> pages.</exception>
    /// <exception cref="ImageTooLargeException">A page exceeds <see cref="LayoutServiceOptions.MaxImagePixels"/>.</exception>
    Task<LayoutResult> AnalyzePagesAsync(
        IEnumerable<Image<Rgb24>> pages,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze every frame of a multi-frame image file (multi-page TIFF, animated GIF/WebP/PNG) on
    /// disk as one document, one page per frame in file order. Single-frame files yield one page. The
    /// single-image <c>AnalyzeAsync</c> overloads, by contrast, always analyze only the first frame.
    /// </summary>
    /// <remarks>
    /// All frames are decoded at once, so the whole file is held in memory for the duration of the
    /// call — bound it with <see cref="LayoutServiceOptions.MaxPages"/> and
    /// <see cref="LayoutServiceOptions.MaxImagePixels"/> for untrusted input, and prefer
    /// <see cref="AnalyzePagesAsync"/> for very long documents, which pulls one page at a time.
    /// Frames may differ in size: each is analyzed at its own dimensions and guarded on its own
    /// pixel count.
    /// </remarks>
    /// <exception cref="TooManyPagesException">The file has more than <see cref="LayoutServiceOptions.MaxPages"/> frames.</exception>
    /// <exception cref="ImageTooLargeException">The frames exceed <see cref="LayoutServiceOptions.MaxImagePixels"/>.</exception>
    Task<LayoutResult> AnalyzeAllFramesAsync(
        string imagePath,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Analyze every frame of a multi-frame image from a stream (format auto-detected). See <see cref="AnalyzeAllFramesAsync(string, LayoutAnalysisOptions?, CancellationToken)"/>.</summary>
    Task<LayoutResult> AnalyzeAllFramesAsync(
        Stream imageStream,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Analyze every frame of a multi-frame image from an encoded byte array. See <see cref="AnalyzeAllFramesAsync(string, LayoutAnalysisOptions?, CancellationToken)"/>.</summary>
    Task<LayoutResult> AnalyzeAllFramesAsync(
        byte[] imageBytes,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Analyze every frame of a multi-frame image from encoded bytes. See <see cref="AnalyzeAllFramesAsync(string, LayoutAnalysisOptions?, CancellationToken)"/>.</summary>
    Task<LayoutResult> AnalyzeAllFramesAsync(
        ReadOnlyMemory<byte> imageBytes,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze every frame of an already-decoded (possibly multi-frame) image, one page per frame.
    /// The caller retains ownership of the image (it is neither mutated nor disposed).
    /// See <see cref="AnalyzeAllFramesAsync(string, LayoutAnalysisOptions?, CancellationToken)"/>.
    /// </summary>
    Task<LayoutResult> AnalyzeAllFramesAsync(
        Image<Rgb24> image,
        LayoutAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);
}
