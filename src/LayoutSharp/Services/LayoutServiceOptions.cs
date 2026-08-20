using LayoutSharp.Models;

namespace LayoutSharp.Services;

/// <summary>
/// Service-level configuration for <see cref="LayoutService"/>: which detector to run, where to
/// cache it, and which execution provider to use. Per-call knobs live on
/// <see cref="LayoutAnalysisOptions"/>.
/// </summary>
public sealed class LayoutServiceOptions
{
    /// <summary>The model a fresh <see cref="LayoutServiceOptions"/> runs.</summary>
    internal const LayoutModel DefaultModel = LayoutModel.DoclingLayoutHeron;

    /// <summary>The region-detection model to run. Defaults to <see cref="LayoutModel.DoclingLayoutHeron"/>.</summary>
    public LayoutModel Model { get; set; } = DefaultModel;

    /// <summary>
    /// Directory the ONNX model is cached in. Defaults to the <c>LAYOUTSHARP_CACHE</c> environment
    /// variable, else <c>%LocalAppData%/LayoutSharp/models</c>.
    /// </summary>
    public string? ModelCachePath { get; set; }

    /// <summary>
    /// Request the CUDA execution provider. Requires the <c>Microsoft.ML.OnnxRuntime.Gpu</c>
    /// package (and a matching CUDA/cuDNN runtime) in the application; falls back to CPU with a
    /// logged warning when unavailable. <see cref="LayoutResult.UsedGpu"/> reports what actually ran.
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>
    /// Never download. When the model is not already in the cache, analysis fails fast with
    /// <see cref="OfflineModelMissingException"/> instead of reaching the network. Also enabled by
    /// the <c>LAYOUTSHARP_OFFLINE=1</c> environment variable. Pre-seed a cache with
    /// <see cref="ILayoutService.WarmUpAsync"/> on a connected machine.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Upper bound on input image size, in pixels (width × height). Larger images are rejected with
    /// <see cref="ImageTooLargeException"/> before decoding, guarding against decompression-bomb /
    /// pixel-flood inputs. Default 100 megapixels.
    /// </summary>
    public long MaxImagePixels { get; set; } = 100_000_000;


    /// <summary>
    /// When true, every page is first classified as rotated 0/90/180/270 degrees with the PP-LCNet
    /// document-orientation model (<c>PP-LCNet_x1_0_doc_ori.onnx</c>, 6.7 MB, downloaded and
    /// checksum-verified into the same cache on first use, ~5 ms per page) and, when the winning
    /// class scores at least <see cref="OrientationConfidenceThreshold"/>, rotated upright before
    /// detection. <see cref="LayoutPage.Rotation"/> reports what was applied; block coordinates
    /// are then in the upright frame (see <see cref="LayoutPage.MapToSource(LayoutBox)"/>).
    /// Default false. Independent of this flag, <see cref="LayoutService.ClassifyOrientationAsync"/>
    /// exposes the classifier directly.
    /// </summary>
    public bool CorrectOrientation { get; set; }

    /// <summary>
    /// Minimum probability of the winning orientation class for <see cref="CorrectOrientation"/> to
    /// rotate the page; below it the page is left as-is. Default 0.6; must be in [0, 1].
    /// </summary>
    public float OrientationConfidenceThreshold { get; set; } = 0.6f;

    /// <summary>
    /// A bring-your-own ONNX detector to run instead of a built-in model. When set, <see cref="Model"/>
    /// must be <see cref="LayoutModel.Custom"/> or left at its default (it is switched to
    /// <see cref="LayoutModel.Custom"/> automatically). The file is loaded from
    /// <see cref="CustomLayoutModel.Path"/> — <see cref="ModelCachePath"/> and <see cref="Offline"/>
    /// do not apply — and verified against <see cref="CustomLayoutModel.Sha256"/> when one is given.
    /// </summary>
    public CustomLayoutModel? CustomModel { get; set; }

    /// <summary>Configures a bring-your-own detector (see <see cref="CustomModel"/>). Returns this instance for chaining.</summary>
    /// <param name="model">The custom model description.</param>
    public LayoutServiceOptions UseCustomModel(CustomLayoutModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        CustomModel = model;
        Model = LayoutModel.Custom;
        return this;
    }

    /// <summary>
    /// Configures a bring-your-own detector from its essentials (see <see cref="CustomLayoutModel"/>
    /// for the meaning of each value). Returns this instance for chaining.
    /// </summary>
    /// <param name="path">Path to the ONNX file.</param>
    /// <param name="inputSize">Square input side the graph was exported for (multiple of 32).</param>
    /// <param name="labels">Class labels in the model's index order.</param>
    /// <param name="outputContract">Which decoder reads the graph.</param>
    /// <param name="normalization">Pixel normalization the graph expects.</param>
    /// <param name="sha256">Optional expected SHA-256 of the file.</param>
    public LayoutServiceOptions UseCustomModel(
        string path,
        int inputSize,
        IReadOnlyList<string> labels,
        LayoutOutputContract outputContract = LayoutOutputContract.PaddleDetection,
        LayoutModelNormalization normalization = LayoutModelNormalization.None,
        string? sha256 = null)
        => UseCustomModel(new CustomLayoutModel
        {
            Path = path,
            InputSize = inputSize,
            Labels = labels,
            OutputContract = outputContract,
            Normalization = normalization,
            Sha256 = sha256,
        });

    /// <summary>
    /// Upper bound on the number of pages one multi-page call (<see cref="ILayoutService.AnalyzePagesAsync"/>,
    /// <c>AnalyzeAllFramesAsync</c>) will process; exceeding it throws <see cref="TooManyPagesException"/>.
    /// Multi-frame files (TIFF, GIF, WebP) are rejected from their frame count before any pixels are
    /// decoded; a page sequence fails when page <c>MaxPages + 1</c> is requested. Together with
    /// <see cref="MaxImagePixels"/> this bounds what a single multi-frame file can claim — ImageSharp
    /// decodes every frame of a file up front, so lower one of the two for untrusted input.
    /// Default 500. Single-image <c>AnalyzeAsync</c> calls are unaffected.
    /// </summary>
    public int MaxPages { get; set; } = 500;

    /// <summary>
    /// Caps the ONNX Runtime intra-op thread pool of the detector session. <c>null</c> (default) lets
    /// the runtime use every core, which is fastest for one page at a time. Set it to 2–4 when a
    /// server analyzes several pages concurrently (see
    /// <see cref="Models.LayoutAnalysisOptions.PageParallelism"/>): otherwise each session claims all
    /// cores and they spend the time fighting each other.
    /// </summary>
    public int? IntraOpThreads { get; set; }

    /// <summary>
    /// Receives progress reports while a model is downloaded on first use: bytes so far, total size,
    /// percentage, whether the transfer resumed a partial file, and the attempt number. Downloads are
    /// streamed, retried with back-off, and resumed from disk after an interruption, so a handler can
    /// drive a progress bar that survives a dropped connection. <c>null</c> (default) reports only
    /// through <c>ILogger</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// DownloadProgress = new Progress&lt;ModelDownloadProgress&gt;(
    ///     p =&gt; Console.WriteLine(p.FileName + " " + p.PercentComplete + "%"));
    /// </code>
    /// </example>
    public IProgress<Models.ModelDownloadProgress>? DownloadProgress { get; set; }

    internal LayoutServiceOptions Clone() => (LayoutServiceOptions)MemberwiseClone();

    internal void Validate()
    {
        if (MaxImagePixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxImagePixels), MaxImagePixels, "MaxImagePixels must be positive.");
        if (IntraOpThreads is <= 0)
            throw new ArgumentOutOfRangeException(nameof(IntraOpThreads), IntraOpThreads, "IntraOpThreads must be positive when set.");
        if (MaxPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPages), MaxPages, "MaxPages must be positive.");
        if (CustomModel is null)
        {
            if (Model == LayoutModel.Custom)
                throw new ArgumentException(
                    "LayoutModel.Custom requires LayoutServiceOptions.CustomModel (or UseCustomModel(...)) to describe the ONNX file to load.",
                    nameof(Model));
            _ = Internal.ModelRegistry.Get(Model); // throws for an unknown enum value
        }
        else
        {
            CustomModel.Validate();
            if (Model == DefaultModel) Model = LayoutModel.Custom;   // the common case: only CustomModel was set
            if (Model != LayoutModel.Custom)
                throw new ArgumentException(
                    $"LayoutServiceOptions.CustomModel is set but Model is {Model}. Set Model = LayoutModel.Custom (or leave it at its default) to run the custom detector.",
                    nameof(Model));
        }
        if (OrientationConfidenceThreshold is < 0f or > 1f || float.IsNaN(OrientationConfidenceThreshold))
            throw new ArgumentOutOfRangeException(nameof(OrientationConfidenceThreshold), OrientationConfidenceThreshold, "OrientationConfidenceThreshold must be in [0, 1].");
    }
}
