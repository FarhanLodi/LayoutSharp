using System.Diagnostics;
using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Preprocessing;
using LayoutSharp.Recognition;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LayoutSharp.Services;

/// <summary>
/// High-level document layout-analysis service. Runs the Docling heron region detector via ONNX
/// Runtime, de-duplicates and cleans up the regions, orders them (XY-cut), and — when given an
/// <see cref="ITextRecognizer"/> — fills in the text of text-bearing regions. No Python required.
/// </summary>
/// <remarks>
/// The service is thread-safe and holds an expensive ONNX session; create one per process (or
/// register it as a singleton with <see cref="ServiceCollectionExtensions.AddLayoutSharp"/>) and
/// dispose it on shutdown. The recognizer is caller-owned and is not disposed by the service.
/// </remarks>
public sealed partial class LayoutService : ILayoutService
{
    private readonly ILayoutDetector _detector;
    private readonly ITextRecognizer? _recognizer;
    private readonly ITableRecognizer? _tableRecognizer;
    private readonly IFormulaRecognizer? _formulaRecognizer;
    private readonly ILogger<LayoutService>? _logger;
    private readonly LayoutServiceOptions _options;
    private bool _disposed;
    private readonly string _modelName;
    private bool _warnedNoModelOrder;

    /// <summary>
    /// Creates a service with default options (<see cref="LayoutModel.DoclingLayoutHeron"/>, CPU,
    /// default cache), optionally with a text recognizer.
    /// </summary>
    /// <param name="recognizer">Recognizer for text-bearing regions, or null for layout-only analysis.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="tableRecognizer">Recognizer for table regions, or null to keep tables as plain regions.</param>
    /// <param name="formulaRecognizer">Recognizer for formula regions, or null to keep formulas as plain regions.</param>
    public LayoutService(ITextRecognizer? recognizer = null, ILogger<LayoutService>? logger = null, ITableRecognizer? tableRecognizer = null, IFormulaRecognizer? formulaRecognizer = null)
        : this(new LayoutServiceOptions(), recognizer, logger, tableRecognizer, formulaRecognizer)
    {
    }

    /// <summary>Creates a service configured by <paramref name="options"/>.</summary>
    /// <param name="options">Model, cache, GPU and guard settings.</param>
    /// <param name="recognizer">Recognizer for text-bearing regions, or null for layout-only analysis.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="tableRecognizer">Recognizer for table regions, or null to keep tables as plain regions.</param>
    /// <param name="formulaRecognizer">Recognizer for formula regions, or null to keep formulas as plain regions.</param>
    public LayoutService(LayoutServiceOptions options, ITextRecognizer? recognizer = null, ILogger<LayoutService>? logger = null, ITableRecognizer? tableRecognizer = null, IFormulaRecognizer? formulaRecognizer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options.Clone();
        _recognizer = recognizer;
        _tableRecognizer = tableRecognizer;
        _formulaRecognizer = formulaRecognizer;
        _logger = logger;

        var cachePath = string.IsNullOrWhiteSpace(_options.ModelCachePath) ? null : Path.GetFullPath(_options.ModelCachePath);
        var spec = ResolveSpec(_options);
        _modelName = spec.Name;
        _detector = new OnnxLayoutEngine(spec, cachePath, _options.UseGpu, _options.Offline, logger, _options.IntraOpThreads, _options.DownloadProgress);
        // Lazy: nothing is downloaded or loaded until CorrectOrientation triggers it or ClassifyOrientationAsync is called.
        _orientation = new OnnxOrientationClassifier(cachePath, _options.UseGpu, _options.Offline, logger);
    }

    /// <summary>
    /// Test seam: run the pipeline over a scripted detector (and optionally scripted recognizers and
    /// orientation classifier).
    /// </summary>
    internal LayoutService(ILayoutDetector detector, LayoutServiceOptions? options, ITextRecognizer? recognizer, ILogger<LayoutService>? logger,
        ITableRecognizer? tableRecognizer = null, IFormulaRecognizer? formulaRecognizer = null, IOrientationClassifier? orientation = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _options = (options ?? new LayoutServiceOptions()).Clone();
        _options.Validate();
        _recognizer = recognizer;
        _tableRecognizer = tableRecognizer;
        _formulaRecognizer = formulaRecognizer;
        _logger = logger;
        _orientation = orientation;
        _modelName = ResolveSpec(_options).Name;
    }

    /// <inheritdoc />
    public LayoutModel Model => _options.Model;

    /// <summary>True when a text recognizer was supplied.</summary>
    public bool HasRecognizer => _recognizer is not null;

    /// <summary>True when a table recognizer was supplied.</summary>
    public bool HasTableRecognizer => _tableRecognizer is not null;

    /// <summary>True when a formula recognizer was supplied.</summary>
    public bool HasFormulaRecognizer => _formulaRecognizer is not null;

    /// <inheritdoc />
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        await _detector.WarmUpAsync(cancellationToken).ConfigureAwait(false);
        if (_options.CorrectOrientation && _orientation is not null)
            await _orientation.WarmUpAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LayoutResult> AnalyzeAsync(string imagePath, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        cancellationToken.ThrowIfCancellationRequested();
        var info = await Image.IdentifyAsync(fullPath, cancellationToken).ConfigureAwait(false);
        GuardImageSize(info.Width, info.Height);

        using var image = await Image.LoadAsync<Rgb24>(fullPath, cancellationToken).ConfigureAwait(false);
        return await RunPipelineAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LayoutResult> AnalyzeAsync(Stream imageStream, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
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
            GuardImageSize(info.Width, info.Height);
            source.Position = start;

            using var image = await Image.LoadAsync<Rgb24>(source, cancellationToken).ConfigureAwait(false);
            return await RunPipelineAsync(image, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <inheritdoc />
    public Task<LayoutResult> AnalyzeAsync(byte[] imageBytes, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return AnalyzeAsync(new ReadOnlyMemory<byte>(imageBytes), options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LayoutResult> AnalyzeAsync(ReadOnlyMemory<byte> imageBytes, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (imageBytes.IsEmpty)
            throw new ArgumentException("Image bytes must not be empty.", nameof(imageBytes));

        cancellationToken.ThrowIfCancellationRequested();
        var info = Image.Identify(imageBytes.Span);
        GuardImageSize(info.Width, info.Height);

        using var image = Image.Load<Rgb24>(imageBytes.Span);
        return await RunPipelineAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<LayoutResult> AnalyzeAsync(Image<Rgb24> image, LayoutAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        GuardImageSize(image.Width, image.Height);
        // Caller owns the image: RunPipelineAsync never disposes or mutates the original.
        return RunPipelineAsync(image, options, cancellationToken);
    }

    // ---- pipeline ----

    private async Task<LayoutResult> RunPipelineAsync(Image<Rgb24> source, LayoutAnalysisOptions? options, CancellationToken ct)
    {
        options ??= LayoutAnalysisOptions.Default;
        options.Validate();
        var sw = Stopwatch.StartNew();

        // 0. Page correction (opt-in): orientation, then small-angle deskew. Every later step —
        //    detection, reading order, OCR crops — runs on `image`, which is the caller's `source`
        //    itself when nothing was applied; the correction owns and disposes any copy it made.
        using var corrected = await CorrectPageAsync(source, options, ct).ConfigureAwait(false);
        var image = corrected.Image;

        var (page, rawCount, orderUsed, recognize, recognizeTables, recognizeFormulas) =
            await AnalyzePageCoreAsync(image, pageNumber: 1, corrected, options, ct).ConfigureAwait(false);

        sw.Stop();
        _logger?.LogInformation("Layout analysis: {Count} blocks ({Raw} raw detections) in {Ms:F0} ms{Ocr}{Correction}",
            page.Blocks.Count, rawCount, sw.Elapsed.TotalMilliseconds,
            RecognitionSuffix(recognize, recognizeTables, recognizeFormulas),
            page.IsCorrected ? $" (page corrected: rotation {page.Rotation}°, skew {page.SkewAngle:F1}°)" : "");

        return new LayoutResult
        {
            Document = new LayoutDocument { Pages = new[] { page } },
            Duration = sw.Elapsed,
            Model = _detector.Model,
            UsedGpu = _detector.IsGpu,
            TextRecognized = recognize,
            TablesRecognized = recognizeTables,
            FormulasRecognized = recognizeFormulas,
            ReadingOrderUsed = orderUsed,
            ModelName = _modelName,
        };
    }

    /// <summary>
    /// One page of a multi-page call: applies the same opt-in page correction as the single-image
    /// path, then runs the per-page core. Returns the page and its raw detection count (for logging).
    /// Never mutates or disposes <paramref name="image"/>.
    /// </summary>
    private async Task<(LayoutPage Page, int RawDetections)> AnalyzePageAsync(Image<Rgb24> image, int pageNumber, LayoutAnalysisOptions options, CancellationToken ct)
    {
        using var corrected = await CorrectPageAsync(image, options, ct).ConfigureAwait(false);
        var (page, rawCount, _, _, _, _) =
            await AnalyzePageCoreAsync(corrected.Image, pageNumber, corrected, options, ct).ConfigureAwait(false);
        return (page, rawCount);
    }

    /// <summary>
    /// The per-page core shared by every entry point: detect → de-duplicate → order → recognize →
    /// assemble one <see cref="LayoutPage"/>. Reading order restarts at 0 on every page. Returns the
    /// page and the raw detection count (for logging). Never mutates or disposes <paramref name="image"/>.
    /// </summary>
    private async Task<(LayoutPage Page, int RawDetections, ReadingOrderSource OrderUsed, bool Recognized, bool TablesRecognized, bool FormulasRecognized)> AnalyzePageCoreAsync(
        Image<Rgb24> image, int pageNumber, CorrectedPage corrected, LayoutAnalysisOptions options, CancellationToken ct)
    {
        // 1. Detect regions.
        var detections = await _detector.DetectAsync(image, options.ConfidenceThreshold, ct).ConfigureAwait(false);

        // 2. IoU de-duplication (several DETR queries can settle on one region, sometimes under two classes).
        var kept = Deduplicate(detections, options.DuplicateIouThreshold);

        // 3. Clean-up: nested same-type fragments and figure-sized noise.
        kept = CleanUp(kept, image.Width, image.Height, options);

        // 4. Reading order (headers first, body by model rank or XY-cut, footers/page numbers last).
        var orderUsed = ResolveReadingOrder(kept, options.ReadingOrderSource);
        var ordered = OrderForReading(kept, image.Width, image.Height, options, orderUsed == ReadingOrderSource.Model);

        // 5. Recognition (optional): text, table structure and formula LaTeX share one work list.
        bool recognize = options.RecognizeText && _recognizer is not null;
        bool recognizeTables = options.RecognizeTables && _tableRecognizer is not null;
        bool recognizeFormulas = options.RecognizeFormulas && _formulaRecognizer is not null;
        var recognized = recognize || recognizeTables || recognizeFormulas
            ? await RecognizeAllAsync(image, ordered, recognize, recognizeTables, recognizeFormulas, options.RecognitionParallelism, ct).ConfigureAwait(false)
            : null;

        // 6. Assemble.
        var blocks = new List<LayoutBlock>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            blocks.Add(new LayoutBlock
            {
                Type = d.Class.Type,
                BoundingBox = d.Box,
                Confidence = d.Score,
                ReadingOrder = i,
                RawClassId = d.Class.Index,
                RawClassName = d.Class.Name,
                Text = recognized?.Texts[i],
                Table = recognized?.Tables[i],
                Latex = recognized?.Latex[i],
            });
        }

        var page = new LayoutPage
        {
            PageNumber = pageNumber,
            Width = image.Width,
            Height = image.Height,
            Blocks = blocks,
            Rotation = corrected.Rotation,
            SkewAngle = corrected.SkewAngle,
            SourceWidth = corrected.SourceWidth,
            SourceHeight = corrected.SourceHeight,
        };

        return (page, detections.Count, orderUsed, recognize, recognizeTables, recognizeFormulas);
    }

    /// <summary>Log suffix naming the recognition passes that ran, e.g. " incl. text, table recognition".</summary>
    private static string RecognitionSuffix(bool text, bool tables, bool formulas)
    {
        if (!text && !tables && !formulas) return "";
        var parts = new List<string>(3);
        if (text) parts.Add("text");
        if (tables) parts.Add("table");
        if (formulas) parts.Add("formula");
        return " incl. " + string.Join(", ", parts) + " recognition";
    }

    /// <summary>
    /// Computes reading order: XY-cut over the body — or the detector's own order when
    /// <paramref name="useModelOrder"/> is set — with page headers pinned first and page footers /
    /// page numbers pinned last when <see cref="LayoutAnalysisOptions.PinPageFurniture"/> is set.
    /// </summary>
    internal static List<RawDetection> OrderForReading(List<RawDetection> kept, int width, int height, LayoutAnalysisOptions options, bool useModelOrder = false)
    {
        double tolerance = options.ReadingOrderOverlapTolerance * Math.Max(width, height);
        List<RawDetection> Order(List<RawDetection> group)
            => useModelOrder ? group.OrderBy(d => d.OrderHint).ToList() : XyCutOrderer.Order(group, d => d.Box, tolerance);

        if (!options.PinPageFurniture)
            return Order(kept);

        var headers = new List<RawDetection>();
        var body = new List<RawDetection>();
        var footers = new List<RawDetection>();
        foreach (var d in kept)
        {
            switch (d.Class.Type)
            {
                case LayoutBlockType.PageHeader: headers.Add(d); break;
                case LayoutBlockType.PageFooter or LayoutBlockType.PageNumber: footers.Add(d); break;
                default: body.Add(d); break;
            }
        }

        var ordered = new List<RawDetection>(kept.Count);
        ordered.AddRange(Order(headers));
        ordered.AddRange(Order(body));
        ordered.AddRange(Order(footers));
        return ordered;
    }

    /// <summary>
    /// Decides which reading-order strategy runs for this call: the model's own order needs a hint on
    /// every kept region (a detector either ranks all of its output or none of it); otherwise XY-cut,
    /// with a one-time warning when <see cref="ReadingOrderSource.Model"/> was demanded of a model
    /// that supplies none.
    /// </summary>
    private ReadingOrderSource ResolveReadingOrder(List<RawDetection> kept, ReadingOrderSource requested)
    {
        if (requested == ReadingOrderSource.XyCut) return ReadingOrderSource.XyCut;

        bool hasModelOrder = kept.Count > 0;
        foreach (var d in kept)
        {
            if (!d.HasOrderHint) { hasModelOrder = false; break; }
        }
        if (hasModelOrder) return ReadingOrderSource.Model;

        if (requested == ReadingOrderSource.Model && kept.Count > 0 && !_warnedNoModelOrder)
        {
            _warnedNoModelOrder = true;
            _logger?.LogWarning(
                "ReadingOrderSource.Model was requested but detector {Model} does not supply a reading order; " +
                "falling back to XY-cut. Use a custom model whose export emits ordered rows for model-native order.",
                _modelName);
        }
        return ReadingOrderSource.XyCut;
    }

    /// <summary>The spec the service runs: the custom model's when one is configured, else the registry entry.</summary>
    private static LayoutModelSpec ResolveSpec(LayoutServiceOptions options)
        => options.CustomModel is { } custom ? ModelRegistry.FromCustom(custom) : ModelRegistry.Get(options.Model);

    /// <summary>Which recognizer a region is routed to.</summary>
    private enum RegionKind { Text, Table, Formula }

    /// <summary>Per-detection recognition outputs (one slot per ordered detection; null where nothing ran or was recovered).</summary>
    private sealed class RecognitionOutputs
    {
        public RecognitionOutputs(int count)
        {
            Texts = new string?[count];
            Tables = new TableStructure?[count];
            Latex = new string?[count];
        }

        public string?[] Texts { get; }
        public TableStructure?[] Tables { get; }
        public string?[] Latex { get; }
    }

    /// <summary>
    /// Recognizes every eligible region — text-bearing regions through the text recognizer, tables
    /// through the table recognizer, formulas through the formula recognizer — as one work list, so
    /// <paramref name="parallelism"/> bounds all recognizer calls together. Returns one slot per ordered
    /// detection (null for regions that were not recognized).
    /// </summary>
    private async Task<RecognitionOutputs> RecognizeAllAsync(
        Image<Rgb24> image, List<RawDetection> ordered, bool text, bool tables, bool formulas, int parallelism, CancellationToken ct)
    {
        var outputs = new RecognitionOutputs(ordered.Count);
        var work = new List<(int Index, RegionKind Kind)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var type = ordered[i].Class.Type;
            if (text && type.IsTextBearing()) work.Add((i, RegionKind.Text));
            else if (tables && type == LayoutBlockType.Table) work.Add((i, RegionKind.Table));
            else if (formulas && type == LayoutBlockType.Formula) work.Add((i, RegionKind.Formula));
        }

        if (work.Count == 0) return outputs;

        if (parallelism <= 1 || work.Count == 1)
        {
            foreach (var (i, kind) in work)
                await RecognizeRegionAsync(image, ordered[i].Box, kind, i, outputs, ct).ConfigureAwait(false);
            return outputs;
        }

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (item, token) => await RecognizeRegionAsync(image, ordered[item.Index].Box, item.Kind, item.Index, outputs, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return outputs;
    }

    /// <summary>
    /// Crops the region and hands it to the recognizer for its kind, storing the normalized result
    /// (trimmed text, page-space table, delimiter-free LaTeX; empty results become null) in
    /// <paramref name="outputs"/> at <paramref name="index"/>.
    /// </summary>
    private async Task RecognizeRegionAsync(Image<Rgb24> image, LayoutBox box, RegionKind kind, int index, RecognitionOutputs outputs, CancellationToken ct)
    {
        var (x, y, w, h) = box.ToPixelRect(image.Width, image.Height);
        if (w < 2 || h < 2) return;

        // Clone reads the shared source; concurrent clones of one image are safe.
        using var crop = image.Clone(c => c.Crop(new Rectangle(x, y, w, h)));
        switch (kind)
        {
            case RegionKind.Text:
                var text = await _recognizer!.RecognizeAsync(crop, ct).ConfigureAwait(false);
                text = text?.Trim();
                outputs.Texts[index] = string.IsNullOrEmpty(text) ? null : text;
                break;

            case RegionKind.Table:
                var table = await _tableRecognizer!.RecognizeAsync(crop, ct).ConfigureAwait(false);
                // Cell boxes come back in crop pixels; shift them into page pixels.
                outputs.Tables[index] = table is null || table.IsEmpty ? null : table.Offset(x, y);
                break;

            case RegionKind.Formula:
                var latex = await _formulaRecognizer!.RecognizeAsync(crop, ct).ConfigureAwait(false);
                outputs.Latex[index] = NormalizeLatex(latex);
                break;
        }
    }

    /// <summary>Trims LaTeX and strips one surrounding pair of math delimiters ($$…$$, $…$, \[…\], \(…\)); empty becomes null.</summary>
    internal static string? NormalizeLatex(string? latex)
    {
        if (latex is null) return null;
        var s = latex.AsSpan().Trim();
        if (s.Length >= 4 && s.StartsWith("$$") && s.EndsWith("$$")) s = s[2..^2].Trim();
        else if (s.Length >= 2 && s[0] == '$' && s[^1] == '$') s = s[1..^1].Trim();
        else if (s.Length >= 4 && s.StartsWith(@"\[") && s.EndsWith(@"\]")) s = s[2..^2].Trim();
        else if (s.Length >= 4 && s.StartsWith(@"\(") && s.EndsWith(@"\)")) s = s[2..^2].Trim();
        return s.IsEmpty ? null : s.ToString();
    }

    /// <summary>
    /// Post-detection clean-up: drops figures below <see cref="LayoutAnalysisOptions.MinFigureAreaFraction"/>
    /// of the page, and — when <see cref="LayoutAnalysisOptions.SuppressNestedDuplicates"/> is set — any
    /// detection at least 90 % contained in a higher-scoring detection of the same type. Input is
    /// expected sorted by descending score (as <see cref="Deduplicate"/> returns it); order is preserved.
    /// </summary>
    internal static List<RawDetection> CleanUp(List<RawDetection> detections, int width, int height, LayoutAnalysisOptions options)
    {
        double minFigureArea = options.MinFigureAreaFraction * width * (double)height;
        var kept = new List<RawDetection>(detections.Count);
        foreach (var d in detections)
        {
            if (d.Class.Type == LayoutBlockType.Figure && d.Box.Area < minFigureArea) continue;

            if (options.SuppressNestedDuplicates)
            {
                bool nested = false;
                foreach (var k in kept)
                {
                    // kept is score-descending, so k outranks d; suppress d if k of the same type contains it.
                    if (k.Class.Type == d.Class.Type && d.Box.Area > 0
                        && d.Box.IntersectionArea(k.Box) / d.Box.Area >= NestedContainmentRatio)
                    {
                        nested = true;
                        break;
                    }
                }
                if (nested) continue;
            }

            kept.Add(d);
        }
        return kept;
    }

    /// <summary>Fraction of a box's own area that must lie inside a same-type, higher-scoring box for it to count as nested.</summary>
    internal const double NestedContainmentRatio = 0.9;

    /// <summary>
    /// Drops the lower-scoring of any two detections whose IoU exceeds the threshold. O(n²) over the
    /// kept set, which is fine for the region counts a page produces.
    /// </summary>
    internal static List<RawDetection> Deduplicate(IReadOnlyList<RawDetection> detections, float iouThreshold)
    {
        var sorted = detections.OrderByDescending(d => d.Score).ToList();
        var kept = new List<RawDetection>(sorted.Count);
        foreach (var d in sorted)
        {
            bool duplicate = false;
            foreach (var k in kept)
            {
                if (d.Box.IntersectionOverUnion(k.Box) > iouThreshold)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) kept.Add(d);
        }
        return kept;
    }

    private void GuardImageSize(int width, int height)
    {
        long pixels = (long)width * height;
        if (pixels > _options.MaxImagePixels)
        {
            throw new ImageTooLargeException(
                $"Image is {width}x{height} ({pixels:N0} px), exceeding the configured limit of " +
                $"{_options.MaxImagePixels:N0} px (LayoutServiceOptions.MaxImagePixels). Raise the limit or downscale " +
                "the image. This guard protects against decompression-bomb / pixel-flood denial of service.");
        }
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Releases the layout detector session. Prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>Asynchronously releases the layout detector session. The recognizer is caller-owned and untouched.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _detector.DisposeAsync().ConfigureAwait(false);
        if (_orientation is not null)
            await _orientation.DisposeAsync().ConfigureAwait(false);
    }

    // ---- page correction: orientation (PP-LCNet doc-ori) + small-angle deskew ----

    private readonly IOrientationClassifier? _orientation;

    /// <summary>
    /// Classifies how many degrees clockwise (0, 90, 180 or 270) the content of
    /// <paramref name="image"/> is rotated, using the PP-LCNet document-orientation model. Loads
    /// (and on first use downloads, 6.7 MB) the model lazily, independent of
    /// <see cref="LayoutServiceOptions.CorrectOrientation"/>. The image is not modified. To make the
    /// page upright, rotate it <c>(360 - Rotation) % 360</c> degrees clockwise.
    /// </summary>
    /// <returns>The winning rotation and its softmax probability.</returns>
    public async Task<(int Rotation, float Confidence)> ClassifyOrientationAsync(Image<Rgb24> image, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        if (_orientation is null)
            throw new InvalidOperationException("This service was created without an orientation classifier.");
        var p = await _orientation.ClassifyAsync(image, cancellationToken).ConfigureAwait(false);
        return (p.Rotation, p.Confidence);
    }

    /// <summary>
    /// Step 0 of the pipeline: rotates the page upright when orientation correction is enabled and
    /// confident, then straightens it when deskew is enabled and the estimate is reliable. Returns
    /// the caller's image untouched (not owned) when nothing applies.
    /// </summary>
    private async Task<CorrectedPage> CorrectPageAsync(Image<Rgb24> source, LayoutAnalysisOptions options, CancellationToken ct)
    {
        Image<Rgb24> working = source;
        bool owned = false;
        int rotation = 0;
        double skew = 0;
        try
        {
            if (_options.CorrectOrientation && _orientation is not null)
            {
                var p = await _orientation.ClassifyAsync(source, ct).ConfigureAwait(false);
                if (p.Rotation != 0 && p.Confidence >= _options.OrientationConfidenceThreshold)
                {
                    working = PageCorrection.Upright(source, p.Rotation);
                    owned = true;
                    rotation = p.Rotation;
                    _logger?.LogInformation("Page orientation: content rotated {Rotation}° clockwise (p={Confidence:F2}); rotated upright to {W}x{H}.",
                        p.Rotation, p.Confidence, working.Width, working.Height);
                }
                else if (p.Rotation != 0)
                {
                    _logger?.LogDebug("Page orientation: {Rotation}° suggested with p={Confidence:F2} < threshold {Threshold:F2}; left as-is.",
                        p.Rotation, p.Confidence, _options.OrientationConfidenceThreshold);
                }
            }

            if (options.Deskew)
            {
                ct.ThrowIfCancellationRequested();
                var est = PageDeskew.Estimate(working, options.DeskewMaxAngle);
                if (est.IsReliable)
                {
                    var straightened = PageDeskew.Rotate(working, -est.Angle);
                    if (owned) working.Dispose();
                    working = straightened;
                    owned = true;
                    skew = est.Angle;
                    _logger?.LogInformation("Page deskew: content skewed {Angle:F1}° (gain {Confidence:F2}); straightened to {W}x{H}.",
                        est.Angle, est.Confidence, working.Width, working.Height);
                }
                else
                {
                    _logger?.LogDebug("Page deskew: estimate {Angle:F1}° (gain {Confidence:F2}) not reliable; left as-is.",
                        est.Angle, est.Confidence);
                }
            }

            return new CorrectedPage(working, owned, rotation, skew, source.Width, source.Height);
        }
        catch
        {
            if (owned) working.Dispose();
            throw;
        }
    }
}
