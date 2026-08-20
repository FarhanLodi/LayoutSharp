namespace LayoutSharp.Models;

/// <summary>
/// Normalized layout region categories — DocLayNet's 11-class taxonomy plus Docling's form
/// extensions, onto which the detector's 17 raw classes are mapped. The original detector label is
/// preserved on <see cref="LayoutBlock.RawClassName"/> when finer granularity is needed (for
/// example to tell <c>code</c> or <c>document_index</c> from plain <c>text</c>, all of which
/// normalize to <see cref="Text"/>, or <c>checkbox_selected</c> from <c>checkbox_unselected</c>,
/// both <see cref="Checkbox"/>). <see cref="PageNumber"/> and <see cref="Seal"/> are reserved for
/// detectors that emit them; the shipped model reports page numbers as page footers / headers.
/// </summary>
public enum LayoutBlockType
{
    /// <summary>The document's main title.</summary>
    Title,

    /// <summary>A section or sub-section heading (DocLayNet <c>section_header</c>).</summary>
    SectionHeader,

    /// <summary>A body-text paragraph (also code blocks and document indexes / tables of contents — see the raw label).</summary>
    Text,

    /// <summary>A bulleted or numbered list item.</summary>
    List,

    /// <summary>A table region.</summary>
    Table,

    /// <summary>A picture, chart, or other figure (kept as a region; not OCR'd).</summary>
    Figure,

    /// <summary>A caption associated with a figure, chart or table.</summary>
    Caption,

    /// <summary>A mathematical formula, equation, or equation number.</summary>
    Formula,

    /// <summary>A footnote.</summary>
    Footnote,

    /// <summary>Repeating page header / running head (including header logos).</summary>
    PageHeader,

    /// <summary>Repeating page footer (including footer images).</summary>
    PageFooter,

    /// <summary>A page number (reserved: the shipped detector folds page numbers into <see cref="PageFooter"/> / <see cref="PageHeader"/>).</summary>
    PageNumber,

    /// <summary>A stamp or seal (reserved: not emitted by the shipped detector).</summary>
    Seal,

    /// <summary>
    /// A checkbox glyph — <c>checkbox_selected</c> or <c>checkbox_unselected</c> on
    /// <see cref="LayoutBlock.RawClassName"/>. Kept as a region, not OCR'd.
    /// </summary>
    Checkbox,

    /// <summary>
    /// A form region: a container grouping fields / fillable areas. Its child fields are reported
    /// as their own blocks (<see cref="Text"/>, <see cref="KeyValueRegion"/>, <see cref="Checkbox"/>),
    /// so the container itself is not sent to the recognizer.
    /// </summary>
    Form,

    /// <summary>A key-value region (label : value pairs, as on invoices and forms).</summary>
    KeyValueRegion,

    /// <summary>A region whose detector class has no specific mapping.</summary>
    Other,
}

/// <summary>
/// Helpers describing how <see cref="LayoutBlockType"/> values participate in the pipeline.
/// </summary>
public static class LayoutBlockTypeExtensions
{
    /// <summary>
    /// True when blocks of this type carry running text worth sending to the text recognizer.
    /// Tables, figures, formulas, seals and checkboxes are kept as regions rather than OCR'd as
    /// plain text; a form is a container whose fields are recognized individually, so it is not
    /// OCR'd as a whole; key-value regions are.
    /// </summary>
    public static bool IsTextBearing(this LayoutBlockType type) => type switch
    {
        LayoutBlockType.Title or
        LayoutBlockType.SectionHeader or
        LayoutBlockType.Text or
        LayoutBlockType.List or
        LayoutBlockType.Caption or
        LayoutBlockType.Footnote or
        LayoutBlockType.PageHeader or
        LayoutBlockType.PageFooter or
        LayoutBlockType.PageNumber or
        LayoutBlockType.KeyValueRegion => true,
        _ => false,
    };

    /// <summary>
    /// True for page furniture (running headers, footers, page numbers) that repeats on every page
    /// and is usually excluded from a document's body text.
    /// </summary>
    public static bool IsPageFurniture(this LayoutBlockType type) => type is
        LayoutBlockType.PageHeader or LayoutBlockType.PageFooter or LayoutBlockType.PageNumber;
}
