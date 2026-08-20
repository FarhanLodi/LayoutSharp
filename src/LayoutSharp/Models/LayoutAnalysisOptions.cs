namespace LayoutSharp.Models;

/// <summary>
/// Tunable parameters for a single layout-analysis call. Service-level settings (model, cache,
/// GPU) live on <see cref="Services.LayoutServiceOptions"/>.
/// </summary>
public sealed record LayoutAnalysisOptions
{
    /// <summary>Default options: confidence 0.5, clean-up on, text recognition on (when a recognizer is configured), sequential recognition.</summary>
    public static LayoutAnalysisOptions Default { get; } = new();

    /// <summary>
    /// Minimum detector confidence (0–1) for a region to be kept. Regions scoring below this are
    /// discarded. The shipped detector scores its confident regions 0.85–0.98, so 0.5 keeps the
    /// structure while dropping speculative queries; lower it (0.3) for maximum recall on faint scans.
    /// </summary>
    public float ConfidenceThreshold { get; init; } = 0.5f;

    /// <summary>
    /// When true and the service was given an <see cref="Recognition.ITextRecognizer"/>, text-bearing
    /// regions (<see cref="LayoutBlockTypeExtensions.IsTextBearing"/>) are cropped and recognized,
    /// populating <see cref="LayoutBlock.Text"/>. Has no effect without a recognizer.
    /// </summary>
    public bool RecognizeText { get; init; } = true;

    /// <summary>
    /// How many regions are sent to the recognizer concurrently. 1 (default) recognizes regions one
    /// after another in reading order and never calls the recognizer from more than one thread.
    /// Values above 1 require a thread-safe recognizer.
    /// </summary>
    public int RecognitionParallelism { get; init; } = 1;

    /// <summary>
    /// IoU threshold above which two overlapping detections are treated as duplicates and the
    /// lower-scoring one is dropped. A DETR head has no NMS: several of its object queries can
    /// settle on the same region (sometimes under two classes); this pass keeps the best one.
    /// </summary>
    public float DuplicateIouThreshold { get; init; } = 0.6f;

    /// <summary>
    /// When true (default), a detection whose area is at least 90 % inside a higher-scoring
    /// detection <em>of the same type</em> is dropped — a line-level fragment inside the paragraph
    /// box it belongs to, or a figure inside a figure. Different types are never suppressed by
    /// containment (a caption inside a figure, fields inside a form region stay), so no information
    /// is lost, only redundancy.
    /// </summary>
    public bool SuppressNestedDuplicates { get; init; } = true;

    /// <summary>
    /// <see cref="LayoutBlockType.Figure"/> detections smaller than this fraction of the page area
    /// are dropped: circled step numerals, bullet glyphs and small icons that a detector reports as
    /// pictures but nobody wants as a block. Default 0.001 (0.1 % of the page — a 63×63 px square on
    /// a 2000×2000 page). Applies to figures only, so page numbers and short lines are unaffected.
    /// Set 0 to keep every figure.
    /// </summary>
    public double MinFigureAreaFraction { get; init; } = 0.001;

    /// <summary>
    /// Fraction of the page's longer side by which two detections may overlap and still be treated
    /// as separated by whitespace when computing reading order. Detected boxes routinely touch or
    /// overlap by a few pixels; without a tolerance one such pair prevents a column split.
    /// Default 0.005 (0.5%). Set to 0 for strict whitespace-only cuts.
    /// </summary>
    public double ReadingOrderOverlapTolerance { get; init; } = 0.005;

    /// <summary>
    /// When true (default), page headers are placed first and page footers / page numbers last in
    /// reading order regardless of where they sit on the page, with the body XY-cut ordered in
    /// between — the way a reader treats running heads and page numbers. Set false to order every
    /// block purely geometrically.
    /// </summary>
    public bool PinPageFurniture { get; init; } = true;

    /// <summary>
    /// When true, the page's small-angle skew is estimated (<see cref="Preprocessing.PageDeskew"/>)
    /// before detection and, when the estimate is reliable (|angle| ≥ 0.5°, clear sharpness gain),
    /// the page is rotated straight with the canvas expanded and filled white. Detection, reading
    /// order and OCR crops then run on the straightened image; <see cref="LayoutPage.SkewAngle"/>,
    /// <see cref="LayoutPage.Width"/>/<see cref="LayoutPage.Height"/> and every block box refer to
    /// that image (use <see cref="LayoutPage.MapToSource(LayoutBox)"/> to go back). Default false.
    /// Runs after orientation correction when both are enabled. Pure ImageSharp, ~20-100 ms per page.
    /// </summary>
    public bool Deskew { get; init; }

    /// <summary>
    /// Search window for <see cref="Deskew"/>: skew is estimated within ±this many degrees. Default
    /// 15; must be in (0, 45]. Cost grows linearly with the window.
    /// </summary>
    public double DeskewMaxAngle { get; init; } = Preprocessing.PageDeskew.DefaultMaxAngle;

    /// <summary>
    /// Where reading order comes from: the detector's own order when it emits one
    /// (a PaddleDetection export with seven-column "ordered" rows, loaded through <see cref="LayoutModel.Custom"/>) or the geometric XY-cut. Default
    /// <see cref="ReadingOrderSource.Auto"/> — model order whenever every kept region carries one,
    /// else XY-cut. <see cref="LayoutResult.ReadingOrderUsed"/> reports which one ran. Page furniture
    /// pinning (<see cref="PinPageFurniture"/>) applies in every mode; <see cref="ReadingOrderOverlapTolerance"/>
    /// only when XY-cut runs.
    /// </summary>
    public ReadingOrderSource ReadingOrderSource { get; init; } = ReadingOrderSource.Auto;

    internal void Validate()
    {
        if (ConfidenceThreshold is < 0f or > 1f || float.IsNaN(ConfidenceThreshold))
            throw new ArgumentOutOfRangeException(nameof(ConfidenceThreshold), ConfidenceThreshold, "ConfidenceThreshold must be in [0, 1].");
        if (RecognitionParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(RecognitionParallelism), RecognitionParallelism, "RecognitionParallelism must be at least 1.");
        if (DuplicateIouThreshold is < 0f or > 1f || float.IsNaN(DuplicateIouThreshold))
            throw new ArgumentOutOfRangeException(nameof(DuplicateIouThreshold), DuplicateIouThreshold, "DuplicateIouThreshold must be in [0, 1].");
        if (ReadingOrderOverlapTolerance < 0 || double.IsNaN(ReadingOrderOverlapTolerance))
            throw new ArgumentOutOfRangeException(nameof(ReadingOrderOverlapTolerance), ReadingOrderOverlapTolerance, "ReadingOrderOverlapTolerance must be non-negative.");
        if (MinFigureAreaFraction is < 0 or > 1 || double.IsNaN(MinFigureAreaFraction))
            throw new ArgumentOutOfRangeException(nameof(MinFigureAreaFraction), MinFigureAreaFraction, "MinFigureAreaFraction must be in [0, 1].");
        if (!(DeskewMaxAngle > 0) || DeskewMaxAngle > 45 || double.IsNaN(DeskewMaxAngle))
            throw new ArgumentOutOfRangeException(nameof(DeskewMaxAngle), DeskewMaxAngle, "DeskewMaxAngle must be in (0, 45].");
        if (!Enum.IsDefined(ReadingOrderSource))
            throw new ArgumentOutOfRangeException(nameof(ReadingOrderSource), ReadingOrderSource, "Unknown reading-order source.");
        if (PageParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(PageParallelism), PageParallelism, "PageParallelism must be at least 1.");
    }

    /// <summary>
    /// When true (default) and the service was given an <see cref="Recognition.ITableRecognizer"/>,
    /// <see cref="LayoutBlockType.Table"/> regions are cropped and recognized, populating
    /// <see cref="LayoutBlock.Table"/>. Has no effect without a table recognizer. Table calls share
    /// <see cref="RecognitionParallelism"/> with text and formula recognition.
    /// </summary>
    public bool RecognizeTables { get; init; } = true;

    /// <summary>
    /// When true (default) and the service was given an <see cref="Recognition.IFormulaRecognizer"/>,
    /// <see cref="LayoutBlockType.Formula"/> regions are cropped and recognized, populating
    /// <see cref="LayoutBlock.Latex"/>. Has no effect without a formula recognizer. Formula calls
    /// share <see cref="RecognitionParallelism"/> with text and table recognition.
    /// </summary>
    public bool RecognizeFormulas { get; init; } = true;

    /// <summary>
    /// How many pages of a multi-page call (<see cref="Services.ILayoutService.AnalyzePagesAsync"/>,
    /// <c>AnalyzeAllFramesAsync</c>) are analyzed concurrently. 1 (default) analyzes pages one after
    /// another, in order, pulling each page from the source only after the previous one is done.
    /// Values above 1 fan pages out to the thread pool — the ONNX session is thread-safe, and results
    /// are always returned in page order — but with a recognizer configured they also require a
    /// thread-safe recognizer, since up to <c>PageParallelism × RecognitionParallelism</c> recognizer
    /// calls can be in flight. On CPU each inference already uses several cores, so gains are modest
    /// (2–4 is a sensible range); GPU inference and recognizer-bound workloads benefit more.
    /// Has no effect on single-image <c>AnalyzeAsync</c> calls.
    /// </summary>
    public int PageParallelism { get; init; } = 1;
}
