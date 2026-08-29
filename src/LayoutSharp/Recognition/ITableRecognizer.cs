using LayoutSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace LayoutSharp.Recognition;

/// <summary>
/// Pluggable table-structure recognition for <see cref="LayoutBlockType.Table"/> regions. LayoutSharp
/// ships no table model of its own; hand a <see cref="Services.LayoutService"/> any implementation
/// (EasyOcrSharp's PP-StructureV3, a SLANet port, a cloud document API, …) and every table block's
/// <see cref="LayoutBlock.Table"/> is filled in. Omit it to keep tables as plain regions.
/// </summary>
/// <remarks>
/// <para>
/// The crop handed to <see cref="RecognizeAsync"/> is owned by LayoutSharp and disposed after the
/// call returns; do not keep a reference to it. Return <c>null</c> (or an empty structure) when
/// nothing was recognized. Cell <see cref="TableCell.BoundingBox"/> values, when supplied, are in
/// <b>crop</b> pixel coordinates — LayoutSharp shifts them into page coordinates.
/// </para>
/// <para>
/// When <see cref="LayoutAnalysisOptions.RecognitionParallelism"/> is greater than 1 the
/// implementation is called concurrently (alongside the text and formula recognizers) and must be
/// thread-safe.
/// </para>
/// </remarks>
public interface ITableRecognizer
{
    /// <summary>Recognizes the structure (and, if the engine can, the cell text) of a cropped table region.</summary>
    /// <param name="crop">The table image, in source resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recognized table, or <c>null</c> when nothing was recognized.</returns>
    Task<TableStructure?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default);
}

/// <summary>Helpers for building <see cref="ITableRecognizer"/> instances.</summary>
public static class TableRecognizer
{
    /// <summary>Wraps an async delegate as an <see cref="ITableRecognizer"/>.</summary>
    public static ITableRecognizer FromDelegate(Func<Image<Rgb24>, CancellationToken, Task<TableStructure?>> recognize)
    {
        ArgumentNullException.ThrowIfNull(recognize);
        return new DelegateTableRecognizer(recognize);
    }

    /// <summary>
    /// Wraps a delegate that returns table HTML (as PP-Structure / SLANet engines do) as an
    /// <see cref="ITableRecognizer"/>; the markup is parsed with <see cref="TableStructure.FromHtml"/>.
    /// </summary>
    public static ITableRecognizer FromHtml(Func<Image<Rgb24>, CancellationToken, Task<string?>> recognizeHtml)
    {
        ArgumentNullException.ThrowIfNull(recognizeHtml);
        return new DelegateTableRecognizer(async (crop, ct) =>
            TableStructure.FromHtml(await recognizeHtml(crop, ct).ConfigureAwait(false)));
    }

    private sealed class DelegateTableRecognizer : ITableRecognizer
    {
        private readonly Func<Image<Rgb24>, CancellationToken, Task<TableStructure?>> _recognize;

        public DelegateTableRecognizer(Func<Image<Rgb24>, CancellationToken, Task<TableStructure?>> recognize)
            => _recognize = recognize;

        public Task<TableStructure?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default)
            => _recognize(crop, cancellationToken);
    }
}
