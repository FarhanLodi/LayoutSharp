namespace LayoutSharp.Models;

/// <summary>
/// The outcome of a layout-analysis operation: the structured document plus run metadata.
/// </summary>
public sealed record LayoutResult
{
    /// <summary>The analyzed document.</summary>
    public required LayoutDocument Document { get; init; }

    /// <summary>Wall-clock duration of the analysis, including recognition.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The detector that produced this result.</summary>
    public LayoutModel Model { get; init; }

    /// <summary>Whether the detector actually ran on a GPU execution provider.</summary>
    public bool UsedGpu { get; init; }

    /// <summary>Whether text recognition ran (a recognizer was configured and enabled for this call).</summary>
    public bool TextRecognized { get; init; }

    /// <summary>
    /// The reading-order strategy that actually produced <see cref="LayoutBlock.ReadingOrder"/> for
    /// this run: <see cref="ReadingOrderSource.Model"/> when the detector's own order was used,
    /// <see cref="ReadingOrderSource.XyCut"/> otherwise. Never <see cref="ReadingOrderSource.Auto"/>
    /// on an analyzed result (only on <see cref="Empty"/>).
    /// </summary>
    public ReadingOrderSource ReadingOrderUsed { get; init; }

    /// <summary>
    /// Human-readable name of the detector that ran: the built-in asset name (e.g.
    /// <c>PP-DocLayout_plus-L</c>, <c>PP-DocLayoutV3</c>) or, for <see cref="LayoutModel.Custom"/>,
    /// <see cref="CustomLayoutModel.Name"/> / the file name without extension.
    /// </summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>An empty result with no pages.</summary>
    public static LayoutResult Empty { get; } = new()
    {
        Document = new LayoutDocument { Pages = Array.Empty<LayoutPage>() },
        Duration = TimeSpan.Zero,
    };

    /// <summary>Whether table-structure recognition ran (a table recognizer was configured and enabled for this call).</summary>
    public bool TablesRecognized { get; init; }

    /// <summary>Whether formula recognition ran (a formula recognizer was configured and enabled for this call).</summary>
    public bool FormulasRecognized { get; init; }
}
