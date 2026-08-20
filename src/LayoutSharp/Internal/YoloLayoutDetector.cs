using LayoutSharp.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LayoutSharp.Internal;

/// <summary>
/// Geometry of a letterbox resize: the source is scaled by <see cref="Scale"/> (aspect preserved) to
/// <see cref="ContentWidth"/>×<see cref="ContentHeight"/> and pasted at (<see cref="Left"/>,
/// <see cref="Top"/>) on a square canvas; <see cref="PadX"/> / <see cref="PadY"/> are the exact
/// (fractional) offsets Ultralytics uses when mapping boxes back.
/// </summary>
internal readonly record struct Letterbox(double Scale, double PadX, double PadY, int ContentWidth, int ContentHeight, int Left, int Top)
{
    /// <summary>
    /// Reproduces Ultralytics <c>LetterBox(auto=False, scaleup=True, center=True)</c> for a
    /// <paramref name="srcW"/>×<paramref name="srcH"/> image on a <paramref name="size"/>×<paramref name="size"/> canvas.
    /// </summary>
    public static Letterbox Compute(int srcW, int srcH, int size)
    {
        if (srcW <= 0 || srcH <= 0) throw new ArgumentOutOfRangeException(nameof(srcW), "Image dimensions must be positive.");
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), size, "Canvas size must be positive.");

        double scale = Math.Min(size / (double)srcW, size / (double)srcH);
        int contentW = Math.Clamp((int)Math.Round(srcW * scale), 1, size);
        int contentH = Math.Clamp((int)Math.Round(srcH * scale), 1, size);
        double padX = (size - contentW) / 2.0;
        double padY = (size - contentH) / 2.0;
        // Ultralytics: top/left = round(pad - 0.1) so an odd padding puts the extra pixel at the bottom/right.
        int left = (int)Math.Round(padX - 0.1, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(padY - 0.1, MidpointRounding.AwayFromZero);
        return new Letterbox(scale, padX, padY, contentW, contentH, Math.Max(0, left), Math.Max(0, top));
    }

    /// <summary>Maps a letterboxed-canvas x coordinate back to the source image (unclamped).</summary>
    public double ToSourceX(double x) => (x - PadX) / Scale;

    /// <summary>Maps a letterboxed-canvas y coordinate back to the source image (unclamped).</summary>
    public double ToSourceY(double y) => (y - PadY) / Scale;

    /// <summary>Maps a letterboxed-canvas box back to source pixels, clamped to the image.</summary>
    public LayoutBox ToSource(float x1, float y1, float x2, float y2, int srcW, int srcH)
        => new(
            Math.Clamp(ToSourceX(x1), 0, srcW),
            Math.Clamp(ToSourceY(y1), 0, srcH),
            Math.Clamp(ToSourceX(x2), 0, srcW),
            Math.Clamp(ToSourceY(y2), 0, srcH));
}

/// <summary>
/// Decodes the rows of an Ultralytics-style end-to-end export (in-graph NMS) into
/// <see cref="RawDetection"/>s in source-image pixel coordinates.
/// </summary>
/// <remarks>
/// <c>model.export(format="onnx", nms=True)</c> (Ultralytics 8.3+) and equivalent graphs emit
/// <c>[N, 6]</c> or <c>[1, N, 6]</c> rows of <c>[x1, y1, x2, y2, score, class_id]</c> in the
/// letterboxed input's pixel space, padded with zero rows up to a fixed N. This decoder drops
/// padding and sub-threshold rows, undoes the letterbox and clamps to the image.
/// </remarks>
internal static class YoloDecoder
{
    /// <summary>Number of floats per detection row.</summary>
    public const int RowStride = 6;

    /// <summary>
    /// Decodes <paramref name="rowCount"/> rows from <paramref name="rows"/> (flat, row-major, stride
    /// <see cref="RowStride"/>), keeping those scoring at or above <paramref name="scoreThreshold"/>,
    /// mapped from letterboxed pixels back to <paramref name="srcW"/>×<paramref name="srcH"/>.
    /// </summary>
    public static List<RawDetection> DecodeRows(
        ReadOnlySpan<float> rows,
        int rowCount,
        Letterbox letterbox,
        int srcW,
        int srcH,
        float scoreThreshold,
        LayoutModelSpec spec)
    {
        int available = rows.Length / RowStride;
        if (rowCount > available) rowCount = available;

        var detections = new List<RawDetection>(Math.Min(rowCount, 64));
        for (int r = 0; r < rowCount; r++)
        {
            int o = r * RowStride;
            float score = rows[o + 4];
            float clsF = rows[o + 5];

            if (float.IsNaN(score) || score < scoreThreshold || clsF < 0 || float.IsNaN(clsF))
                continue;

            var box = letterbox.ToSource(rows[o], rows[o + 1], rows[o + 2], rows[o + 3], srcW, srcH);
            if (box.Width < 1 || box.Height < 1) continue; // also drops the all-zero padding rows

            detections.Add(new RawDetection(box, spec.Resolve((int)clsF), Math.Clamp(score, 0f, 1f)));
        }

        return detections;
    }
}

/// <summary>
/// Runs an Ultralytics-style end-to-end ONNX session (letterboxed input, NMS in the graph,
/// <c>[x1, y1, x2, y2, score, class_id]</c> rows) and decodes its output. One instance per model;
/// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> is thread-safe, so a
/// single detector serves concurrent callers.
/// </summary>
internal sealed class YoloLayoutDetector : IDetectionSession
{
    private readonly InferenceSession _session;
    private readonly LayoutModelSpec _spec;
    private readonly string _imageInput;

    public YoloLayoutDetector(string modelPath, LayoutModelSpec spec, SessionOptions sessionOptions)
    {
        _spec = spec;
        _session = new InferenceSession(modelPath, sessionOptions);

        // Ultralytics names the image "images"; fall back to "the rank-4 input is the image".
        string? image = null;
        foreach (var (name, meta) in _session.InputMetadata)
        {
            if (name == "images" || (image is null && meta.Dimensions.Length == 4)) image = name;
        }

        _imageInput = image ?? throw new LayoutSharpException(
            $"Layout model '{spec.FileName}' has no rank-4 image input. Inputs: {string.Join(", ", _session.InputMetadata.Keys)}.");

        ValidateOutputs(_session.OutputMetadata, spec);
    }

    /// <summary>
    /// Fails fast, with a message that names the fix, when the static output shapes show a raw
    /// Ultralytics head (<c>[1, 4+C, A]</c> / <c>[1, A, 4+C]</c>) instead of end-to-end rows.
    /// </summary>
    internal static void ValidateOutputs(IReadOnlyDictionary<string, NodeMetadata> outputs, LayoutModelSpec spec)
    {
        bool anyRows = false, anyDynamic = false;
        string? rawHead = null;
        foreach (var (name, meta) in outputs)
        {
            if (!meta.IsTensor || meta.ElementType != typeof(float)) continue;
            var dims = meta.Dimensions;
            if (dims.Length is not (2 or 3)) continue;
            int last = dims[^1];
            if (last == YoloDecoder.RowStride) { anyRows = true; break; }
            if (last < 0 || dims.Any(d => d < 0)) { anyDynamic = true; continue; }
            if (dims.Length == 3 && (dims[1] == 4 + spec.ClassCount || dims[2] == 4 + spec.ClassCount)) rawHead = name;
        }

        if (anyRows || anyDynamic) return;
        if (rawHead is not null)
            throw new LayoutSharpException(
                $"Custom layout model '{spec.FileName}' output '{rawHead}' is a raw YOLO head ([1, 4+classes, anchors]) without NMS. " +
                "LayoutSharp's YoloEndToEnd contract needs the post-processed rows: re-export with NMS in the graph " +
                "(Ultralytics: model.export(format=\"onnx\", nms=True)).");
        throw new LayoutSharpException(
            $"Custom layout model '{spec.FileName}' has no [N, 6] / [1, N, 6] float output (x1, y1, x2, y2, score, class_id). " +
            $"Outputs: {string.Join(", ", outputs.Select(o => $"{o.Key} [{string.Join(",", o.Value.Dimensions)}]"))}.");
    }

    /// <inheritdoc />
    public IReadOnlyList<RawDetection> Detect(Image<Rgb24> image, float scoreThreshold)
    {
        int size = _spec.InputSize;
        var pixels = ImageProcessing.PreprocessLetterbox(image, size, _spec.ImageNetNormalize, out var letterbox);

        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(_imageInput, new DenseTensor<float>(pixels, new[] { 1, 3, size, size })),
        };

        using var results = _session.Run(inputs);

        var (rows, rowCount) = LocateOutputs(results);
        return YoloDecoder.DecodeRows(rows, rowCount, letterbox, image.Width, image.Height, scoreThreshold, _spec);
    }

    /// <summary>Finds the <c>[N, 6]</c> or <c>[1, N, 6]</c> float row tensor among the outputs.</summary>
    private static (float[] Rows, int RowCount) LocateOutputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var r in results)
        {
            if (r.ElementType != TensorElementType.Float) continue;
            var t = r.AsTensor<float>();
            var dims = t.Dimensions;
            if (dims.Length is 2 or 3 && dims[^1] == YoloDecoder.RowStride)
            {
                var rows = t.ToArray();
                return (rows, rows.Length / YoloDecoder.RowStride);
            }
        }

        throw new LayoutSharpException(
            "Layout model did not produce the expected [N, 6] / [1, N, 6] detection tensor (x1, y1, x2, y2, score, class_id). " +
            "Export with NMS in the graph (Ultralytics: nms=True) and verify the ONNX outputs.");
    }

    public void Dispose() => _session.Dispose();
}
