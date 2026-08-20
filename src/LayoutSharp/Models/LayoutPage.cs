namespace LayoutSharp.Models;

/// <summary>
/// One analyzed page: its pixel dimensions and the detected blocks, ordered by reading order.
/// </summary>
public sealed record LayoutPage
{
    /// <summary>One-based page number within the source document (1 for a single image).</summary>
    public required int PageNumber { get; init; }

    /// <summary>Source page width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Source page height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Detected blocks, sorted by <see cref="LayoutBlock.ReadingOrder"/>.</summary>
    public required IReadOnlyList<LayoutBlock> Blocks { get; init; }

    // ---- page correction (orientation / deskew) ----
    //
    // When Services.LayoutServiceOptions.CorrectOrientation or LayoutAnalysisOptions.Deskew applied a
    // correction, Width/Height and every block's BoundingBox are in the CORRECTED frame (the image
    // detection and OCR crops actually ran on). The properties below describe the correction and
    // map coordinates back to the caller's image.

    /// <summary>
    /// Clockwise rotation, in degrees (0, 90, 180 or 270), that the source page content had and that
    /// was undone before detection; the page was rotated <c>(360 - Rotation) % 360</c> degrees
    /// clockwise. 0 when orientation correction was off, not confident, or found the page upright.
    /// </summary>
    public int Rotation { get; init; }

    /// <summary>
    /// Skew of the source content in degrees (positive = tilted clockwise) that was corrected before
    /// detection; the page was rotated by <c>-SkewAngle</c> with the canvas expanded and filled white
    /// (see <see cref="Preprocessing.PageDeskew.Rotate"/>). 0 when deskew was off or not reliable.
    /// </summary>
    public double SkewAngle { get; init; }

    /// <summary>
    /// Width in pixels of the page as given by the caller. Equals <see cref="Width"/> unless a
    /// correction was applied; 0 in documents serialized before this property existed (treated as
    /// <see cref="Width"/>).
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>
    /// Height in pixels of the page as given by the caller. Equals <see cref="Height"/> unless a
    /// correction was applied; 0 in documents serialized before this property existed (treated as
    /// <see cref="Height"/>).
    /// </summary>
    public int SourceHeight { get; init; }

    /// <summary>True when an orientation or skew correction was applied, i.e. the block coordinates are not in the source frame.</summary>
    public bool IsCorrected => Rotation != 0 || SkewAngle != 0;

    /// <summary>
    /// Maps a point in this page's (corrected) frame back to the source image the caller supplied.
    /// Exact for 0/90/180/270 rotations; for a deskewed page it is the inverse rotation about the
    /// page centre. Identity when <see cref="IsCorrected"/> is false.
    /// </summary>
    public (double X, double Y) MapToSource(double x, double y)
        => Internal.PageCorrection.MapToSource(x, y, Rotation, SkewAngle,
            SourceWidth > 0 ? SourceWidth : Width, SourceHeight > 0 ? SourceHeight : Height, Width, Height);

    /// <summary>
    /// Maps a box in this page's (corrected) frame to the source image: exact for 0/90/180/270
    /// rotations, the axis-aligned rectangle enclosing the four mapped corners when the page was
    /// deskewed. Identity when <see cref="IsCorrected"/> is false.
    /// </summary>
    public LayoutBox MapToSource(LayoutBox box)
        => Internal.PageCorrection.MapToSource(box, Rotation, SkewAngle,
            SourceWidth > 0 ? SourceWidth : Width, SourceHeight > 0 ? SourceHeight : Height, Width, Height);
}
