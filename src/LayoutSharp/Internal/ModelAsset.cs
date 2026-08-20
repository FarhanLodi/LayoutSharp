namespace LayoutSharp.Internal;

/// <summary>
/// A downloadable, checksum-verified ONNX file: the minimum <see cref="ModelDownloadManager"/> needs
/// to locate, fetch and verify an asset. Detector specs (<see cref="LayoutModelSpec"/>) and auxiliary
/// models such as the document-orientation classifier both reduce to one of these.
/// </summary>
/// <param name="FileName">File name in the cache directory and under the download base URL.</param>
/// <param name="Sha256">Upper-case hex SHA-256 of the published file; downloads that do not match are discarded.</param>
internal sealed record ModelAsset(string FileName, string Sha256)
{
    /// <summary>Default download URL for this asset.</summary>
    public string Url => $"{ModelRegistry.DefaultBaseUrl}/{FileName}";
}
