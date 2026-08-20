namespace LayoutSharp.Models;

/// <summary>
/// A single detected layout region: its classified type, location, detector confidence, the
/// position it occupies in reading order, and (for text-bearing regions) the OCR'd text.
/// </summary>
public sealed record LayoutBlock
{
    /// <summary>Normalized region category.</summary>
    public required LayoutBlockType Type { get; init; }

    /// <summary>Axis-aligned bounding box in original-image pixel coordinates.</summary>
    public required LayoutBox BoundingBox { get; init; }

    /// <summary>Detector confidence for this region, in [0, 1].</summary>
    public required float Confidence { get; init; }

    /// <summary>
    /// Zero-based position of this block in the computed reading order within its page.
    /// </summary>
    public int ReadingOrder { get; init; }

    /// <summary>
    /// The raw class index emitted by the underlying detector before mapping to
    /// <see cref="Type"/>. Useful for diagnostics and for distinguishing categories the
    /// normalized enum collapses together.
    /// </summary>
    public int RawClassId { get; init; }

    /// <summary>The detector's own label for <see cref="RawClassId"/> (e.g. "paragraph_title").</summary>
    public string RawClassName { get; init; } = string.Empty;

    /// <summary>
    /// OCR'd text for text-bearing regions, or null when the region was not OCR'd
    /// (e.g. a figure, or when text recognition was disabled).
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Recovered table structure for <see cref="LayoutBlockType.Table"/> blocks when an
    /// <see cref="Recognition.ITableRecognizer"/> ran (cell boxes in original-image pixels), or null
    /// (no table recognizer, table recognition disabled, nothing recognized, or not a table).
    /// </summary>
    public TableStructure? Table { get; init; }

    /// <summary>
    /// Recovered LaTeX (without <c>$</c> delimiters) for <see cref="LayoutBlockType.Formula"/> blocks
    /// when an <see cref="Recognition.IFormulaRecognizer"/> ran, or null.
    /// </summary>
    public string? Latex { get; init; }
}
