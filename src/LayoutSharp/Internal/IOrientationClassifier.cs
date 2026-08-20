using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LayoutSharp.Internal;

/// <summary>
/// One document-orientation prediction: the clockwise rotation (0, 90, 180 or 270 degrees) the page
/// content appears to have, the probability of that class, and the full 4-way distribution.
/// </summary>
internal readonly record struct OrientationPrediction(int Rotation, float Confidence, float P0, float P90, float P180, float P270);

/// <summary>
/// The page-orientation stage as seen by <see cref="Services.LayoutService"/>. Production code uses
/// <see cref="OnnxOrientationClassifier"/>; tests substitute a scripted implementation.
/// </summary>
internal interface IOrientationClassifier : IAsyncDisposable
{
    /// <summary>Ensures the model is downloaded and the session is ready.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken);

    /// <summary>Classifies the orientation of <paramref name="image"/>.</summary>
    Task<OrientationPrediction> ClassifyAsync(Image<Rgb24> image, CancellationToken cancellationToken);
}
