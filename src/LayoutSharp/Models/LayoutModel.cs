namespace LayoutSharp.Models;

/// <summary>
/// The region-detection model a <see cref="Services.LayoutService"/> runs. Whichever is selected is
/// downloaded (SHA-256 verified) on first use and cached locally. Two detectors ship, both
/// Apache-2.0: <see cref="PPDocLayoutV3"/> (the default) and <see cref="DoclingLayoutHeron"/>. They
/// differ in taxonomy more than in quality — pick V3 for the wider label set (seals, charts,
/// vertical CJK text, page numbers) and for skewed scans, heron for speed and for degraded pages.
/// </summary>
public enum LayoutModel
{
    /// <summary>
    /// Baidu <c>PP-DocLayoutV3</c> (Apache-2.0) — <b>the default</b>: RT-DETR-L, 800×800 input,
    /// 25 categories. A superset of the DocLayNet taxonomy in coverage: alongside title, text,
    /// list, table, figure, caption, formula and footnote it detects <c>seal</c> (stamps and red
    /// chops on contracts, invoices and certificates), <c>chart</c> as distinct from <c>image</c>,
    /// <c>vertical_text</c> for CJK, <c>number</c> (page numbers) and separate <c>abstract</c> /
    /// <c>reference</c> / <c>algorithm</c> regions. ~124 MB ONNX, ~1.0 s per page on a laptop CPU.
    /// </summary>
    /// <remarks>
    /// The export also carries a per-region reading-order key, so
    /// <see cref="ReadingOrderSource.Model"/> works without falling back to geometric XY-cut, and it
    /// degrades far better on rotated or skewed pages than <see cref="DoclingLayoutHeron"/>. It is
    /// roughly 1.5× slower per page in exchange, and slightly behind heron on heavy blur and very
    /// low resolution.
    /// </remarks>
    PPDocLayoutV3,

    /// <summary>
    /// IBM Docling <c>docling-layout-heron</c> (Apache-2.0): RT-DETRv2 with a ResNet-50 backbone,
    /// 42.9 M parameters, 640×640 input, 17 categories — the 11 DocLayNet classes (caption, footnote,
    /// formula, list item, page footer, page header, picture, section header, table, text, title)
    /// plus document index, code, checkbox (selected / unselected), form and key-value region.
    /// Trained by IBM on ~150 k pages (DocLayNet, DocLayNet-v2, WordScape); 0.70 mAP raw / 0.78 with
    /// post-processing on DocLayNet — the strongest DocLayNet-taxonomy detector available under a
    /// permissive license. ~172 MB ONNX, ~0.6 s per page on a laptop CPU.
    /// </summary>
    /// <remarks>
    /// Faster than <see cref="PPDocLayoutV3"/> and more robust to blur, noise and low resolution,
    /// but it has no <c>seal</c>, <c>chart</c>, <c>vertical_text</c> or page-number class, and it
    /// needs <see cref="LayoutAnalysisOptions.Deskew"/> on skewed scans.
    /// </remarks>
    DoclingLayoutHeron,

    /// <summary>
    /// A bring-your-own ONNX detector, described by <see cref="Services.LayoutServiceOptions.CustomModel"/>:
    /// loaded from a local path (never downloaded), with the label list, input size, normalization and
    /// output contract the caller supplies. Use it for a fine-tune of the shipped model or for any
    /// detector exported to one of the supported graph contracts.
    /// </summary>
    Custom,
}
