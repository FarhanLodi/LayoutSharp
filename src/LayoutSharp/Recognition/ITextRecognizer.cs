using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LayoutSharp.Recognition;

/// <summary>
/// Pluggable text recognition for text-bearing layout regions. LayoutSharp ships no OCR engine of
/// its own; hand a <see cref="Services.LayoutService"/> any implementation (EasyOcrSharp, Tesseract,
/// a cloud vision API, …) and the text of every text-bearing block
/// (<see cref="Models.LayoutBlockTypeExtensions.IsTextBearing"/>) is filled in. Omit it to run layout-only.
/// </summary>
/// <remarks>
/// <para>
/// The crop handed to <see cref="RecognizeAsync"/> is owned by LayoutSharp and disposed after the
/// call returns; do not keep a reference to it. Return <c>null</c> or an empty string when nothing
/// legible was found.
/// </para>
/// <para>
/// When <see cref="Models.LayoutAnalysisOptions.RecognitionParallelism"/> is greater than 1 the
/// implementation is called concurrently and must be thread-safe.
/// </para>
/// </remarks>
public interface ITextRecognizer
{
    /// <summary>Recognizes the text in a cropped text region.</summary>
    /// <param name="crop">The region image, in source resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recognized text, or <c>null</c> when nothing legible was found.</returns>
    Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default);
}

/// <summary>Helpers for building <see cref="ITextRecognizer"/> instances.</summary>
public static class TextRecognizer
{
    /// <summary>Wraps an async delegate as an <see cref="ITextRecognizer"/>.</summary>
    public static ITextRecognizer FromDelegate(Func<Image<Rgb24>, CancellationToken, Task<string?>> recognize)
    {
        ArgumentNullException.ThrowIfNull(recognize);
        return new DelegateTextRecognizer(recognize);
    }

    private sealed class DelegateTextRecognizer : ITextRecognizer
    {
        private readonly Func<Image<Rgb24>, CancellationToken, Task<string?>> _recognize;

        public DelegateTextRecognizer(Func<Image<Rgb24>, CancellationToken, Task<string?>> recognize)
            => _recognize = recognize;

        public Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default)
            => _recognize(crop, cancellationToken);
    }
}
