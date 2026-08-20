namespace LayoutSharp;

/// <summary>
/// Base exception for LayoutSharp-specific failures. Model download and verification, inference,
/// input guards and pipeline errors all derive from this type, so a single catch covers the library.
/// </summary>
public class LayoutSharpException : Exception
{
    /// <summary>Creates a new <see cref="LayoutSharpException"/>.</summary>
    public LayoutSharpException(string message) : base(message) { }

    /// <summary>Creates a new <see cref="LayoutSharpException"/> with an inner cause.</summary>
    public LayoutSharpException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when a model could not be downloaded after the configured retries.</summary>
public sealed class ModelDownloadException : LayoutSharpException
{
    /// <summary>The URL that failed.</summary>
    public string Url { get; }

    /// <summary>Creates a new <see cref="ModelDownloadException"/>.</summary>
    public ModelDownloadException(string message, string url, Exception innerException)
        : base(message, innerException)
    {
        Url = url;
    }
}

/// <summary>Thrown when a downloaded model fails SHA-256 verification (the file is deleted).</summary>
public sealed class ModelChecksumException : LayoutSharpException
{
    /// <summary>Creates a new <see cref="ModelChecksumException"/>.</summary>
    public ModelChecksumException(string message) : base(message) { }
}

/// <summary>
/// Thrown when offline mode is enabled (<see cref="Services.LayoutServiceOptions.Offline"/> or
/// <c>LAYOUTSHARP_OFFLINE=1</c>) and the required model is not present in the cache.
/// </summary>
public sealed class OfflineModelMissingException : LayoutSharpException
{
    /// <summary>The path where the model was expected.</summary>
    public string ExpectedPath { get; }

    /// <summary>Creates a new <see cref="OfflineModelMissingException"/>.</summary>
    public OfflineModelMissingException(string message, string expectedPath) : base(message)
    {
        ExpectedPath = expectedPath;
    }
}

/// <summary>
/// Thrown when an input image exceeds <see cref="Services.LayoutServiceOptions.MaxImagePixels"/>
/// (decompression-bomb / pixel-flood guard).
/// </summary>
public sealed class ImageTooLargeException : LayoutSharpException
{
    /// <summary>Creates a new <see cref="ImageTooLargeException"/>.</summary>
    public ImageTooLargeException(string message) : base(message) { }
}

/// <summary>Thrown when the ONNX Runtime session could not be created or inference failed.</summary>
public sealed class LayoutInferenceException : LayoutSharpException
{
    /// <summary>Creates a new <see cref="LayoutInferenceException"/>.</summary>
    public LayoutInferenceException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a multi-page input (a page sequence or a multi-frame image file) has more pages than
/// <see cref="Services.LayoutServiceOptions.MaxPages"/> allows.
/// </summary>
public sealed class TooManyPagesException : LayoutSharpException
{
    /// <summary>The configured page limit that was exceeded.</summary>
    public int MaxPages { get; }

    /// <summary>Creates a new <see cref="TooManyPagesException"/>.</summary>
    public TooManyPagesException(string message, int maxPages) : base(message)
    {
        MaxPages = maxPages;
    }
}
