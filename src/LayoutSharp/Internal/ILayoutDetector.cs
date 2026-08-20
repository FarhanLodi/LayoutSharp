using LayoutSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LayoutSharp.Internal;

/// <summary>
/// A raw detector output: a box in source-image pixel coordinates, its class, and score.
/// <paramref name="OrderHint"/> is the model's own reading-order key for the region (lower reads
/// first; only relative order is meaningful, gaps are normal) or <c>-1</c> when the model emits
/// none — see <see cref="Models.ReadingOrderSource"/>.
/// </summary>
internal readonly record struct RawDetection(LayoutBox Box, RawClass Class, float Score, int OrderHint = -1)
{
    /// <summary>True when the detector supplied a reading-order key for this region.</summary>
    public bool HasOrderHint => OrderHint >= 0;
}

/// <summary>
/// The region-detection stage as seen by <see cref="Services.LayoutService"/>. Production code uses
/// <see cref="OnnxLayoutEngine"/>; tests substitute a scripted implementation so the rest of the
/// pipeline (de-duplication, reading order, recognition routing) is exercised without a model.
/// </summary>
/// <summary>
/// A loaded ONNX detector session for one <see cref="LayoutModelSpec"/>: preprocess, run, decode.
/// Implementations differ only in the graph contract they read (<see cref="DetectorKind"/>);
/// <see cref="OnnxLayoutEngine"/> owns download, session options and lifetime.
/// </summary>
internal interface IDetectionSession : IDisposable
{
    /// <summary>
    /// Detects layout regions in <paramref name="image"/>, returning every detection scoring at or
    /// above <paramref name="scoreThreshold"/>, in source-image pixel coordinates.
    /// </summary>
    IReadOnlyList<RawDetection> Detect(Image<Rgb24> image, float scoreThreshold);
}

internal interface ILayoutDetector : IAsyncDisposable
{
    /// <summary>The model this detector runs.</summary>
    LayoutModel Model { get; }

    /// <summary>
    /// True once the ONNX session has been created on a GPU execution provider. False before
    /// initialization and whenever inference runs on CPU.
    /// </summary>
    bool IsGpu { get; }

    /// <summary>Ensures the model is downloaded and the session is ready.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Detects layout regions in <paramref name="image"/>, returning every detection scoring at or
    /// above <paramref name="scoreThreshold"/>, in source-image pixel coordinates.
    /// </summary>
    Task<IReadOnlyList<RawDetection>> DetectAsync(Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken);
}
