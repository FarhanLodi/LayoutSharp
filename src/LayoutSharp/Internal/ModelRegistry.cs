using LayoutSharp.Models;

namespace LayoutSharp.Internal;

/// <summary>A detector output class: its index, the detector's own label, and the normalized type.</summary>
internal readonly record struct RawClass(int Index, string Name, LayoutBlockType Type);

/// <summary>How a detector's ONNX graph reports its detections, i.e. which decoder reads it.</summary>
internal enum DetectorKind
{
    /// <summary>
    /// PaddleDetection export: post-processing baked into the graph, output rows of
    /// <c>[class_id, score, x1, y1, x2, y2]</c> in input-pixel space (see <see cref="PaddleLayoutDetector"/>).
    /// </summary>
    PaddleDetection,

    /// <summary>
    /// Raw DETR-family head (RT-DETR / RT-DETRv2 / D-FINE as exported from Hugging Face
    /// <c>transformers</c>): <c>logits [1, Q, C]</c> + <c>pred_boxes [1, Q, 4]</c> normalized
    /// <c>cx, cy, w, h</c>, no NMS (see <see cref="DetrLayoutDetector"/>).
    /// </summary>
    Detr,

    /// <summary>
    /// Ultralytics-style end-to-end export with NMS in the graph: letterboxed input, output rows of
    /// <c>[x1, y1, x2, y2, score, class_id]</c> (see <see cref="YoloLayoutDetector"/>).
    /// </summary>
    YoloEndToEnd,
}

/// <summary>
/// Everything LayoutSharp needs to know about one downloadable detector: where it lives, how to
/// verify it, how to feed it, and how to read its classes.
/// </summary>
internal sealed record LayoutModelSpec(
    LayoutModel Model,
    string FileName,
    string Sha256,
    string Family,
    int InputSize,
    bool ImageNetNormalize,
    IReadOnlyList<RawClass> Classes,
    DetectorKind Kind = DetectorKind.Detr)
{
    /// <summary>Default download URL for this asset.</summary>
    public string Url => $"{ModelRegistry.DefaultBaseUrl}/{FileName}";

    /// <summary>Number of categories the detector emits.</summary>
    public int ClassCount => Classes.Count;

    /// <summary>
    /// Maps a raw detector class index to its definition, or a fallback
    /// <see cref="LayoutBlockType.Other"/> entry when the index is out of range.
    /// </summary>
    public RawClass Resolve(int index)
        => (uint)index < (uint)Classes.Count
            ? Classes[index]
            : new RawClass(index, $"class_{index}", LayoutBlockType.Other);

    /// <summary>The downloadable file behind this spec, as <see cref="ModelDownloadManager"/> sees it.</summary>
    public ModelAsset Asset => new(FileName, Sha256);
    /// <summary>
    /// Non-null for a bring-your-own model (<see cref="LayoutModel.Custom"/>): the absolute path the
    /// ONNX file is loaded from. Such specs are never downloaded or cached; <see cref="Sha256"/> is
    /// verified only when non-empty.
    /// </summary>
    public string? LocalPath { get; init; }

    /// <summary>Optional display-name override (custom models); see <see cref="Name"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Human-readable name reported on <see cref="LayoutResult.ModelName"/>: <see cref="DisplayName"/>
    /// when set, else the file name without extension (e.g. <c>PP-DocLayoutV3</c>).
    /// </summary>
    public string Name => DisplayName ?? Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// Static catalogue of the ONNX detectors LayoutSharp can run, plus each one's raw-class →
/// <see cref="LayoutBlockType"/> mapping.
/// </summary>
/// <remarks>
/// <para>
/// The shipped asset is IBM's <c>docling-layout-heron</c> (Apache-2.0, Hugging Face
/// <c>transformers</c> RT-DETRv2 checkpoint <c>docling-project/docling-layout-heron</c>) exported to
/// ONNX with <c>training/export_onnx.py</c> in this repository (opset 17). Graph contract: input
/// <c>pixel_values [1,3,640,640]</c> (RGB, stretch-resized, <c>1/255</c>, no mean/std); outputs
/// <c>logits [1,300,17]</c> (pre-sigmoid) and <c>pred_boxes [1,300,4]</c> (normalized
/// <c>cx, cy, w, h</c>). No NMS in the graph — see <see cref="DetrLayoutDetector"/>.
/// </para>
/// <para>
/// The label list below is copied verbatim from the checkpoint's <c>config.json</c>
/// (<c>id2label</c>). Index order is the wire contract. The raw label is preserved on every block so
/// a mis-mapping is diagnosable at a glance.
/// </para>
/// </remarks>
internal static class ModelRegistry
{
    /// <summary>
    /// Base URL where the exported ONNX assets are hosted. Override at runtime via the
    /// <c>LAYOUTSHARP_MODEL_BASE_URL</c> environment variable to use a private mirror.
    /// </summary>
    public const string DefaultBaseUrl =
        "https://huggingface.co/LayoutSharp/LayoutSharp-models/resolve/main";

    // ---- label → normalized-type mapping ----

    private static RawClass[] Map(Func<string, LayoutBlockType> typeOf, params string[] labels)
    {
        var classes = new RawClass[labels.Length];
        for (int i = 0; i < labels.Length; i++)
            classes[i] = new RawClass(i, labels[i], typeOf(labels[i]));
        return classes;
    }

    /// <summary>
    /// Normalizes a Docling layout label (DocLayNet's 11 plus Docling's 6 extensions, snake_case as
    /// in the checkpoint's <c>id2label</c>) to the library's <see cref="LayoutBlockType"/> taxonomy.
    /// </summary>
    internal static LayoutBlockType TypeOf(string label) => label switch
    {
        "title" or "doc_title" => LayoutBlockType.Title,
        "section_header" or "paragraph_title" => LayoutBlockType.SectionHeader,
        "text" or "document_index" or "code" or "abstract" or "content" or "reference"
            or "reference_content" or "algorithm" or "aside_text" or "vertical_text" => LayoutBlockType.Text,
        "list_item" => LayoutBlockType.List,
        "table" => LayoutBlockType.Table,
        "picture" or "image" or "chart" => LayoutBlockType.Figure,
        "caption" or "figure_title" or "table_title" or "chart_title" or "vision_footnote" => LayoutBlockType.Caption,
        "formula" or "formula_number" or "display_formula" or "inline_formula" => LayoutBlockType.Formula,
        "footnote" => LayoutBlockType.Footnote,
        "page_header" => LayoutBlockType.PageHeader,
        "page_footer" => LayoutBlockType.PageFooter,
        "checkbox_selected" or "checkbox_unselected" => LayoutBlockType.Checkbox,
        "form" => LayoutBlockType.Form,
        "key_value_region" => LayoutBlockType.KeyValueRegion,
        _ => LayoutBlockType.Other,
    };

    // docling-layout-heron: 17 categories (config.json id2label, verbatim order). Indices 0–10 are
    // DocLayNet's canonical order; 11–16 are Docling's DocLayNet-v2 extensions.
    private static readonly RawClass[] HeronClasses = Map(TypeOf,
        "caption", "footnote", "formula", "list_item", "page_footer", "page_header", "picture",
        "section_header", "table", "text", "title", "document_index", "code", "checkbox_selected",
        "checkbox_unselected", "form", "key_value_region");

    private static readonly LayoutModelSpec Heron = new(
        LayoutModel.DoclingLayoutHeron,
        FileName: "docling-layout-heron.onnx",
        Sha256: "7542D71B4E94A6275BEDDE0CF0966267178A9C90699BD529B52C285D398015E8",
        Family: "RT-DETRv2-R50",
        InputSize: 640,
        ImageNetNormalize: false,   // preprocessor_config.json: do_normalize false, rescale 1/255 only
        Classes: HeronClasses);

    /// <summary>All registered detectors.</summary>
    public static IReadOnlyList<LayoutModelSpec> All { get; } = new[] { Heron };

    /// <summary>
    /// Looks up the spec for a built-in <see cref="LayoutModel"/>. <see cref="LayoutModel.Custom"/>
    /// has no registry entry — build its spec with <see cref="FromCustom"/> — and throws
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static LayoutModelSpec Get(LayoutModel model) => model switch
    {
        LayoutModel.DoclingLayoutHeron => Heron,
        LayoutModel.Custom => throw new ArgumentException(
            "LayoutModel.Custom requires LayoutServiceOptions.CustomModel to describe the ONNX file to load.", nameof(model)),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown layout model."),
    };

    // ---- auxiliary (non-detector) assets ----

    /// <summary>
    /// PP-LCNet_x1_0_doc_ori (PaddleClas, Apache-2.0): 4-way document orientation classifier used by
    /// <see cref="Services.LayoutServiceOptions.CorrectOrientation"/>. Input <c>x [1,3,224,224]</c>
    /// (resize short side to 256, centre-crop 224, ImageNet mean/std); output <c>[1,4]</c> softmax
    /// over the labels <c>["0","90","180","270"]</c> = how many degrees clockwise the page content
    /// is rotated. 6,715,311 bytes. See <see cref="OnnxOrientationClassifier"/>.
    /// </summary>
    public static readonly ModelAsset DocOrientation = new(
        FileName: "PP-LCNet_x1_0_doc_ori.onnx",
        Sha256: "D85B3185075AFCA1A83157F73EAC2E52B598D72E9D47DD19CC4A2F3605E23E3F");

    /// <summary>
    /// Builds the spec for a bring-your-own model: loaded from <see cref="LayoutModelSpec.LocalPath"/>,
    /// decoded by the <see cref="DetectorKind"/> matching its <see cref="LayoutOutputContract"/>,
    /// with each label normalized through <see cref="CustomLayoutModel.TypeMap"/> then
    /// <see cref="DefaultCustomTypeOf"/>.
    /// </summary>
    public static LayoutModelSpec FromCustom(CustomLayoutModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var kind = model.OutputContract switch
        {
            LayoutOutputContract.PaddleDetection => DetectorKind.PaddleDetection,
            LayoutOutputContract.Detr => DetectorKind.Detr,
            LayoutOutputContract.YoloEndToEnd => DetectorKind.YoloEndToEnd,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model.OutputContract, "Unknown output contract."),
        };

        var map = model.TypeMap;
        var labels = model.Labels.ToArray();
        var classes = Map(
            label => map is not null && map.TryGetValue(label, out var mapped) ? mapped : DefaultCustomTypeOf(label),
            labels);

        var fullPath = Path.GetFullPath(model.Path);
        return new LayoutModelSpec(
            LayoutModel.Custom,
            FileName: Path.GetFileName(fullPath),
            Sha256: model.Sha256?.ToUpperInvariant() ?? string.Empty,
            Family: "custom",
            InputSize: model.InputSize,
            ImageNetNormalize: model.Normalization == LayoutModelNormalization.ImageNet,
            Classes: classes,
            Kind: kind)
        {
            LocalPath = fullPath,
            DisplayName = model.DisplayName,
        };
    }

    /// <summary>
    /// Default label normalization for custom models: the PP-DocLayout vocabulary
    /// (<see cref="TypeOf"/>), then a case- and
    /// separator-insensitive pass over both plus the common YOLO layout vocabularies
    /// (DocLayout-YOLO / DocStructBench: <c>plain text</c>, <c>figure_caption</c>,
    /// <c>isolate_formula</c>, …). Anything else is <see cref="LayoutBlockType.Other"/>.
    /// </summary>
    internal static LayoutBlockType DefaultCustomTypeOf(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return LayoutBlockType.Other;

        var type = TypeOf(label);
        if (type != LayoutBlockType.Other) return type;

        // Normalize "Section-header", "Plain Text", "list_item" … to one spelling.
        var key = label.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        type = TypeOf(key);
        if (type != LayoutBlockType.Other) return type;

        return key switch
        {
            "title" or "doc_title" or "document_title" => LayoutBlockType.Title,
            "section_header" or "section_title" or "heading" or "subtitle" or "sub_title" => LayoutBlockType.SectionHeader,
            "plain_text" or "paragraph" or "body" or "body_text" or "document_index" or "code" or "index" => LayoutBlockType.Text,
            "list" or "list_item" or "list_items" => LayoutBlockType.List,
            "figure" or "picture" or "figure_image" or "photo" or "graphic" => LayoutBlockType.Figure,
            "caption" or "figure_caption" or "table_caption" or "chart_caption" or "image_caption" => LayoutBlockType.Caption,
            "table_footnote" or "figure_footnote" => LayoutBlockType.Footnote,
            "equation" or "isolate_formula" or "isolated_formula" or "formula_caption" or "math" => LayoutBlockType.Formula,
            "page_header" or "running_head" => LayoutBlockType.PageHeader,
            "page_footer" => LayoutBlockType.PageFooter,
            "page_number" or "page_num" => LayoutBlockType.PageNumber,
            "seal" or "stamp" => LayoutBlockType.Seal,
            "checkbox" or "checkbox_selected" or "checkbox_unselected" or "check_box" => LayoutBlockType.Checkbox,
            "form" or "form_region" => LayoutBlockType.Form,
            "key_value_region" or "key_value" or "kv_region" => LayoutBlockType.KeyValueRegion,
            _ => LayoutBlockType.Other,
        };
    }
}
