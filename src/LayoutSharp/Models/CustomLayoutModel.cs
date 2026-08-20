namespace LayoutSharp.Models;

/// <summary>Pixel normalization a detector expects after the 1/255 rescale to [0, 1].</summary>
public enum LayoutModelNormalization
{
    /// <summary>Rescale to [0, 1] only (PP-DocLayout_plus-L / V3, Docling heron, Ultralytics YOLO exports).</summary>
    None,

    /// <summary>
    /// Rescale to [0, 1], then subtract the ImageNet mean <c>[0.485, 0.456, 0.406]</c> and divide by
    /// the ImageNet std <c>[0.229, 0.224, 0.225]</c> per RGB channel (PP-DocLayout-M / -S and most
    /// PaddleDetection or Hugging Face exports whose preprocessing config lists mean/std).
    /// </summary>
    ImageNet,
}

/// <summary>
/// The graph contract of a bring-your-own detector, i.e. which inputs LayoutSharp feeds and how it
/// reads the outputs. Pick the one matching the framework the ONNX file was exported from.
/// </summary>
public enum LayoutOutputContract
{
    /// <summary>
    /// PaddleDetection / PaddleX export (Paddle2ONNX): inputs <c>image [1,3,S,S]</c> plus
    /// <c>scale_factor [1,2]</c> (and <c>im_shape [1,2]</c> on RT-DETR variants); output rows of
    /// <c>[class_id, score, x1, y1, x2, y2]</c> — or <c>[..., order]</c> with a seventh reading-order
    /// column, as PP-DocLayoutV3 emits — in the S×S input space, with top-k / NMS baked into the
    /// graph, plus an optional <c>int32</c> row count. Preprocessing: plain stretch to S×S.
    /// </summary>
    PaddleDetection,

    /// <summary>
    /// Hugging Face <c>transformers</c> RT-DETR / RT-DETRv2 / D-FINE export: input
    /// <c>pixel_values [1,3,S,S]</c>; outputs <c>logits [1,Q,C]</c> (pre-sigmoid) and
    /// <c>pred_boxes [1,Q,4]</c> (normalized <c>cx, cy, w, h</c>), no NMS. Preprocessing: plain
    /// stretch to S×S. The label count must equal the logits' last dimension (or be one less when
    /// the export carries a trailing no-object class).
    /// </summary>
    Detr,

    /// <summary>
    /// Ultralytics-style end-to-end export (<c>model.export(format="onnx", nms=True)</c>, or any
    /// graph with in-graph NMS): input <c>images [1,3,S,S]</c>; output rows of
    /// <c>[x1, y1, x2, y2, score, class_id]</c> shaped <c>[N,6]</c> or <c>[1,N,6]</c> in
    /// letterboxed S×S pixels. Preprocessing: letterbox (scale = min(S/w, S/h), centered on a
    /// gray-114 canvas), which LayoutSharp undoes on the way out. Raw heads (<c>[1,4+C,A]</c>)
    /// are not decoded — re-export with NMS in the graph.
    /// </summary>
    YoloEndToEnd,
}

/// <summary>
/// Describes a bring-your-own ONNX layout detector — a PP-DocLayout fine-tune exported with
/// PaddleX, a Hugging Face RT-DETR checkpoint, an Ultralytics YOLO trained on your own pages —
/// so <see cref="Services.LayoutService"/> can run it exactly like a built-in model. Assign it to
/// <see cref="Services.LayoutServiceOptions.CustomModel"/> (or call
/// <see cref="Services.LayoutServiceOptions.UseCustomModel(CustomLayoutModel)"/>); the service then
/// reports <see cref="LayoutModel.Custom"/> and loads the file straight from <see cref="Path"/>
/// without any download.
/// </summary>
/// <remarks>
/// LayoutSharp itself is MIT and every built-in model is Apache-2.0, but a custom model carries its
/// own licence: Ultralytics YOLO weights and exports are AGPL-3.0 unless you hold a commercial
/// licence, so check the terms of whatever you load here.
/// </remarks>
public sealed record CustomLayoutModel
{
    /// <summary>
    /// Absolute or relative path to the <c>.onnx</c> file. It must exist when the service is
    /// constructed; it is never copied into the model cache.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The square input side S the graph was exported for (e.g. 800, 640, 480, 1024). Must be a
    /// multiple of 32 between 64 and 4096. For PaddleX exports read <c>Preprocess → Resize →
    /// target_size</c> in <c>inference.yml</c>; for Ultralytics the <c>imgsz</c> used at export.
    /// </summary>
    public required int InputSize { get; init; }

    /// <summary>
    /// The class labels in the model's own index order (<c>inference.yml label_list</c>,
    /// <c>config.json id2label</c>, or <c>data.yaml names</c>). Non-empty, non-blank and distinct.
    /// Each raw label is normalized to a <see cref="LayoutBlockType"/> through <see cref="TypeMap"/>
    /// when it has an entry, else through the built-in PP-DocLayout / Docling / common YOLO
    /// vocabularies (case-insensitive), else <see cref="LayoutBlockType.Other"/>. The raw label is
    /// always preserved on <see cref="LayoutBlock.RawClassName"/>.
    /// </summary>
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>Pixel normalization the graph expects. Defaults to <see cref="LayoutModelNormalization.None"/>.</summary>
    public LayoutModelNormalization Normalization { get; init; } = LayoutModelNormalization.None;

    /// <summary>Which decoder reads the graph. Defaults to <see cref="LayoutOutputContract.PaddleDetection"/>.</summary>
    public LayoutOutputContract OutputContract { get; init; } = LayoutOutputContract.PaddleDetection;

    /// <summary>
    /// Optional expected SHA-256 of the file (64 hex characters, either case). When set it is verified
    /// once, when the session is created; a mismatch throws <see cref="ModelChecksumException"/> and
    /// the file is left untouched. When null the file is trusted as-is.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Optional overrides from raw label to normalized <see cref="LayoutBlockType"/>. Labels without
    /// an entry fall back to the built-in vocabularies described on <see cref="Labels"/>.
    /// </summary>
    public IReadOnlyDictionary<string, LayoutBlockType>? TypeMap { get; init; }

    /// <summary>
    /// Display name reported on <see cref="LayoutResult.ModelName"/> and in logs. Defaults to the
    /// file name without extension.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The name this model is reported under: <see cref="Name"/>, else the file name without extension.</summary>
    internal string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// Throws when the description cannot possibly be run: missing file, invalid input size, empty /
    /// duplicate labels, malformed SHA-256 or unknown enum values.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("CustomLayoutModel.Path must be set to the .onnx file to load.", nameof(Path));

        var fullPath = System.IO.Path.GetFullPath(Path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                $"Custom layout model not found at '{fullPath}'. CustomLayoutModel.Path must point to an existing ONNX file " +
                "(LayoutSharp never downloads custom models).", fullPath);

        if (InputSize is < 64 or > 4096 || InputSize % 32 != 0)
            throw new ArgumentOutOfRangeException(nameof(InputSize), InputSize,
                "CustomLayoutModel.InputSize must be a multiple of 32 between 64 and 4096 (e.g. 480, 640, 800, 1024).");

        if (Labels is null || Labels.Count == 0)
            throw new ArgumentException("CustomLayoutModel.Labels must list the model's classes in index order.", nameof(Labels));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Labels.Count; i++)
        {
            var label = Labels[i];
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException($"CustomLayoutModel.Labels[{i}] is blank; every class needs a label.", nameof(Labels));
            if (!seen.Add(label))
                throw new ArgumentException($"CustomLayoutModel.Labels contains '{label}' more than once; labels must be distinct.", nameof(Labels));
        }

        if (Sha256 is not null && !IsSha256Hex(Sha256))
            throw new ArgumentException(
                "CustomLayoutModel.Sha256 must be 64 hexadecimal characters (or null to skip verification).", nameof(Sha256));

        if (!Enum.IsDefined(Normalization))
            throw new ArgumentOutOfRangeException(nameof(Normalization), Normalization, "Unknown normalization.");
        if (!Enum.IsDefined(OutputContract))
            throw new ArgumentOutOfRangeException(nameof(OutputContract), OutputContract, "Unknown output contract.");
    }

    /// <summary>
    /// Snapshot with its own copies of <see cref="Labels"/> and <see cref="TypeMap"/>, so later
    /// mutation of the caller's collections cannot leak into a running service.
    /// </summary>
    internal CustomLayoutModel Snapshot() => this with
    {
        Labels = Labels?.ToArray() ?? Array.Empty<string>(),
        TypeMap = TypeMap is null ? null : new Dictionary<string, LayoutBlockType>(TypeMap, TypeMap is Dictionary<string, LayoutBlockType> d ? d.Comparer : StringComparer.Ordinal),
    };

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
