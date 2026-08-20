namespace LayoutSharp.Models;

/// <summary>
/// A progress report for a model download, delivered to
/// <c>LayoutServiceOptions.DownloadProgress</c> while an asset is being fetched.
/// </summary>
/// <remarks>
/// Reports arrive on the download's own thread, at most a few times per second, plus one final
/// report at completion. A handler must be cheap and must not throw — an exception thrown from
/// <see cref="IProgress{T}.Report"/> would surface as a download failure.
/// </remarks>
/// <param name="FileName">The asset being downloaded, e.g. <c>docling-layout-heron.onnx</c>.</param>
/// <param name="BytesDownloaded">Bytes on disk so far, including anything carried over by a resumed download.</param>
/// <param name="TotalBytes">Total size in bytes, or <c>null</c> when the server did not report one.</param>
/// <param name="ResumedFromBytes">
/// Bytes already present when this attempt started: <c>0</c> for a fresh download, greater than zero
/// when a partial file from an interrupted attempt was continued with an HTTP range request.
/// </param>
/// <param name="Attempt">1-based attempt number (a download is retried on transient failures).</param>
public readonly record struct ModelDownloadProgress(
    string FileName,
    long BytesDownloaded,
    long? TotalBytes,
    long ResumedFromBytes,
    int Attempt)
{
    /// <summary>Completion in the range 0–100, or <c>null</c> when the total size is unknown.</summary>
    public double? PercentComplete =>
        TotalBytes is > 0 ? Math.Clamp(BytesDownloaded * 100.0 / TotalBytes.Value, 0, 100) : null;

    /// <summary>True when this attempt continued a partial file rather than starting from zero.</summary>
    public bool IsResumed => ResumedFromBytes > 0;

    /// <summary>True once every byte has been written (only meaningful when the total size is known).</summary>
    public bool IsComplete => TotalBytes is > 0 && BytesDownloaded >= TotalBytes.Value;
}
