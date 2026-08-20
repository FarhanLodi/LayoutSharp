using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LayoutSharp.Recognition;

/// <summary>
/// Pluggable formula recognition for <see cref="Models.LayoutBlockType.Formula"/> regions. LayoutSharp
/// ships no formula model of its own; hand a <see cref="Services.LayoutService"/> any implementation
/// (EasyOcrSharp's PP-StructureV3 LaTeX-OCR, pix2tex, a cloud API, …) and every formula block's
/// <see cref="Models.LayoutBlock.Latex"/> is filled in. Omit it to keep formulas as plain regions.
/// </summary>
/// <remarks>
/// <para>
/// The crop handed to <see cref="RecognizeAsync"/> is owned by LayoutSharp and disposed after the
/// call returns; do not keep a reference to it. Return the LaTeX <b>without</b> <c>$</c> delimiters
/// (a single surrounding <c>$…$</c> / <c>$$…$$</c> pair is stripped if present), or <c>null</c> /
/// empty when nothing was recognized.
/// </para>
/// <para>
/// When <see cref="Models.LayoutAnalysisOptions.RecognitionParallelism"/> is greater than 1 the
/// implementation is called concurrently (alongside the text and table recognizers) and must be
/// thread-safe.
/// </para>
/// </remarks>
public interface IFormulaRecognizer
{
    /// <summary>Recognizes the LaTeX of a cropped formula region.</summary>
    /// <param name="crop">The formula image, in source resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The LaTeX source (no <c>$</c> delimiters), or <c>null</c> when nothing was recognized.</returns>
    Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default);
}

/// <summary>Helpers for building <see cref="IFormulaRecognizer"/> instances.</summary>
public static class FormulaRecognizer
{
    /// <summary>Wraps an async delegate as an <see cref="IFormulaRecognizer"/>.</summary>
    public static IFormulaRecognizer FromDelegate(Func<Image<Rgb24>, CancellationToken, Task<string?>> recognize)
    {
        ArgumentNullException.ThrowIfNull(recognize);
        return new DelegateFormulaRecognizer(recognize);
    }

    private sealed class DelegateFormulaRecognizer : IFormulaRecognizer
    {
        private readonly Func<Image<Rgb24>, CancellationToken, Task<string?>> _recognize;

        public DelegateFormulaRecognizer(Func<Image<Rgb24>, CancellationToken, Task<string?>> recognize)
            => _recognize = recognize;

        public Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default)
            => _recognize(crop, cancellationToken);
    }
}
