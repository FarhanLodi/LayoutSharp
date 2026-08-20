namespace LayoutSharp.Models;

/// <summary>
/// Where the reading order of a page's blocks (<see cref="LayoutBlock.ReadingOrder"/>) comes from.
/// Set per call on <see cref="LayoutAnalysisOptions.ReadingOrderSource"/>; the strategy that
/// actually ran is reported on <see cref="LayoutResult.ReadingOrderUsed"/>.
/// </summary>
/// <remarks>
/// Only some detectors emit an order of their own. A PaddleDetection export whose rows carry a
/// seventh "ordered object detection" column — PP-DocLayoutV3 and its fine-tunes, loaded through
/// <see cref="CustomLayoutModel"/> — ranks every region it detects: a learned, layout-aware order
/// that handles multi-column pages, side bars and captions better than a purely geometric sort.
/// Detectors without such an order fall back to the recursive XY-cut. Regardless of the source,
/// <see cref="LayoutAnalysisOptions.PinPageFurniture"/> still moves page headers first and page
/// footers / page numbers last; the source only decides how blocks are sorted within each partition.
/// </remarks>
public enum ReadingOrderSource
{
    /// <summary>
    /// Use the detector's own order when it supplied one for every kept region, otherwise XY-cut.
    /// The default.
    /// </summary>
    Auto,

    /// <summary>
    /// Require the detector's own order. When the model does not supply one, the service logs a
    /// warning and falls back to XY-cut (which is what <see cref="LayoutResult.ReadingOrderUsed"/> then reports).
    /// </summary>
    Model,

    /// <summary>
    /// Always order geometrically with the recursive XY-cut, ignoring any model-supplied order.
    /// <see cref="LayoutAnalysisOptions.ReadingOrderOverlapTolerance"/> applies.
    /// </summary>
    XyCut,
}
