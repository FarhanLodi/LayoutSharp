namespace LayoutSharp.Models;

/// <summary>
/// The region-detection model a <see cref="Services.LayoutService"/> runs. LayoutSharp ships one
/// detector, chosen after a head-to-head against the PP-DocLayout family on real pages: it is
/// downloaded (SHA-256 verified) on first use and cached locally. The enum exists so a future
/// variant (a fine-tune, a smaller edge model) can be added without breaking callers.
/// </summary>
public enum LayoutModel
{
    /// <summary>
    /// IBM Docling <c>docling-layout-heron</c> (Apache-2.0): RT-DETRv2 with a ResNet-50 backbone,
    /// 42.9 M parameters, 640×640 input, 17 categories — the 11 DocLayNet classes (caption, footnote,
    /// formula, list item, page footer, page header, picture, section header, table, text, title)
    /// plus document index, code, checkbox (selected / unselected), form and key-value region.
    /// Trained by IBM on ~150 k pages (DocLayNet, DocLayNet-v2, WordScape); 0.70 mAP raw / 0.78 with
    /// post-processing on DocLayNet — the strongest DocLayNet-taxonomy detector available under a
    /// permissive license. ~172 MB ONNX, ~0.5 s per page on a laptop CPU.
    /// </summary>
    DoclingLayoutHeron,

    /// <summary>
    /// A bring-your-own ONNX detector, described by <see cref="Services.LayoutServiceOptions.CustomModel"/>:
    /// loaded from a local path (never downloaded), with the label list, input size, normalization and
    /// output contract the caller supplies. Use it for a fine-tune of the shipped model or for any
    /// detector exported to one of the supported graph contracts.
    /// </summary>
    Custom,
}
