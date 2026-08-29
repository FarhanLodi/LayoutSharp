using LayoutSharp.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace LayoutSharp.Internal;

/// <summary>
/// Decodes the post-processed detection rows emitted by a Paddle2ONNX-exported PaddleDetection
/// model into <see cref="RawDetection"/>s in source-image pixel coordinates.
/// </summary>
/// <remarks>
/// PaddleDetection exports bake the post-processing into the graph. Feeding
/// <c>scale_factor = (1, 1)</c> and <c>im_shape = (S, S)</c> makes the graph report boxes in its own
/// S×S input space (rather than trying to undo an external scale itself), which this decoder then
/// maps back to the source with a plain stretch factor. Rows are <c>[class_id, score, x1, y1, x2, y2]</c>.
/// The RT-DETR variant always emits its 300 top-k rows (a query can appear more than once with
/// different classes, so callers must de-duplicate); the PicoDet variants emit NMS survivors and
/// may return a single <c>class_id = -1</c> row when nothing was found.
/// PP-DocLayoutV3 ("ordered object detection" in PaddleX terms) widens the rows to seven columns,
/// the seventh being a per-region reading-order key (ascending = read first; PaddleX sorts by it
/// with <c>argsort</c>), which is surfaced as <see cref="RawDetection.OrderHint"/>. V3 also emits an
/// <c>[M,200,200] int32</c> page-space mask per row that LayoutSharp neither fetches nor decodes.
/// </remarks>
internal static class PaddleDetectionDecoder
{
    /// <summary>Number of floats per detection row.</summary>
    public const int RowStride = 6;

    /// <summary>Number of floats per detection row when a trailing reading-order column is present.</summary>
    public const int OrderedRowStride = 7;

    /// <summary>
    /// Decodes <paramref name="rowCount"/> rows from <paramref name="rows"/> (flat, row-major, stride
    /// <paramref name="rowStride"/>: <see cref="RowStride"/> or <see cref="OrderedRowStride"/>),
    /// keeping those scoring at or above <paramref name="scoreThreshold"/>.
    /// Boxes are scaled from the model's <paramref name="inputSize"/>×<paramref name="inputSize"/>
    /// space to <paramref name="srcW"/>×<paramref name="srcH"/> and clamped to the image. With a
    /// seven-column stride the seventh value becomes <see cref="RawDetection.OrderHint"/>.
    /// </summary>
    public static List<RawDetection> Decode(
        ReadOnlySpan<float> rows,
        int rowCount,
        int inputSize,
        int srcW,
        int srcH,
        float scoreThreshold,
        LayoutModelSpec spec,
        int rowStride = RowStride)
    {
        if (rowStride < RowStride)
            throw new ArgumentOutOfRangeException(nameof(rowStride), rowStride, "Detection rows need at least six columns.");

        int available = rows.Length / rowStride;
        if (rowCount > available) rowCount = available;

        double sx = (double)srcW / inputSize;
        double sy = (double)srcH / inputSize;
        bool ordered = rowStride >= OrderedRowStride;

        var detections = new List<RawDetection>(Math.Min(rowCount, 64));
        for (int r = 0; r < rowCount; r++)
        {
            int o = r * rowStride;
            float clsF = rows[o];
            float score = rows[o + 1];

            if (clsF < 0 || float.IsNaN(clsF) || float.IsNaN(score) || score < scoreThreshold)
                continue;

            double minX = Math.Clamp(rows[o + 2] * sx, 0, srcW);
            double minY = Math.Clamp(rows[o + 3] * sy, 0, srcH);
            double maxX = Math.Clamp(rows[o + 4] * sx, 0, srcW);
            double maxY = Math.Clamp(rows[o + 5] * sy, 0, srcH);

            var box = new LayoutBox(minX, minY, maxX, maxY);
            if (box.Width < 1 || box.Height < 1) continue;

            int orderHint = -1;
            if (ordered)
            {
                float key = rows[o + 6];
                if (!float.IsNaN(key) && key >= 0) orderHint = (int)key;
            }

            detections.Add(new RawDetection(box, spec.Resolve((int)clsF), Math.Clamp(score, 0f, 1f), orderHint));
        }

        return detections;
    }
}

/// <summary>
/// Runs a PP-DocLayout ONNX session (RT-DETR or PicoDet export) and decodes its output. One
/// instance per model; <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> is
/// thread-safe, so a single detector serves concurrent callers.
/// </summary>
internal sealed class PaddleLayoutDetector : IDetectionSession
{
    private readonly InferenceSession _session;
    private readonly LayoutModelSpec _spec;
    private readonly string _imageInput;
    private readonly string? _scaleFactorInput;
    private readonly string? _imShapeInput;
    private readonly string[]? _outputNames;

    public PaddleLayoutDetector(string modelPath, LayoutModelSpec spec, SessionOptions sessionOptions)
    {
        _spec = spec;
        _session = new InferenceSession(modelPath, sessionOptions);

        // Bind inputs by name where the export uses the PaddleDetection conventions, falling back to
        // "the rank-4 input is the image" so a re-export with different names still loads.
        string? image = null;
        foreach (var (name, meta) in _session.InputMetadata)
        {
            if (name == "image" || (image is null && meta.Dimensions.Length == 4)) image = name;
            else if (name == "scale_factor") _scaleFactorInput = name;
            else if (name == "im_shape") _imShapeInput = name;
        }

        _imageInput = image ?? throw new LayoutSharpException(
            $"Layout model '{spec.FileName}' has no rank-4 image input. Inputs: {string.Join(", ", _session.InputMetadata.Keys)}.");

        _outputNames = SelectOutputs(_session.OutputMetadata);
    }

    /// <summary>
    /// Picks the outputs worth fetching — the rank-2 float detection rows and the rank-1 int32 row
    /// count — so that extra heads (PP-DocLayoutV3's <c>[M,200,200]</c> mask tensor, ~48 MB per call)
    /// are neither computed nor copied. Returns null (fetch everything) when the rows cannot be
    /// identified from the static metadata.
    /// </summary>
    internal static string[]? SelectOutputs(IReadOnlyDictionary<string, NodeMetadata> outputs)
    {
        string? rows = null, count = null;
        foreach (var (name, meta) in outputs)
        {
            if (!meta.IsTensor) continue;
            var dims = meta.Dimensions;
            if (rows is null && meta.ElementType == typeof(float) && dims.Length == 2
                && (dims[1] == PaddleDetectionDecoder.RowStride || dims[1] == PaddleDetectionDecoder.OrderedRowStride))
                rows = name;
            else if (count is null && meta.ElementType == typeof(int) && dims.Length == 1)
                count = name;
        }

        if (rows is null) return null;
        return count is null ? new[] { rows } : new[] { rows, count };
    }

    /// <inheritdoc />
    public IReadOnlyList<RawDetection> Detect(Image<Rgb24> image, float scoreThreshold)
    {
        int size = _spec.InputSize;
        var pixels = ImageProcessing.Preprocess(image, size, _spec.ImageNetNormalize, _spec.Interpolation);

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_imageInput, new DenseTensor<float>(pixels, new[] { 1, 3, size, size })),
        };
        if (_scaleFactorInput is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_scaleFactorInput, new DenseTensor<float>(new float[] { 1f, 1f }, new[] { 1, 2 })));
        if (_imShapeInput is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_imShapeInput, new DenseTensor<float>(new float[] { size, size }, new[] { 1, 2 })));

        using var results = _outputNames is null ? _session.Run(inputs) : _session.Run(inputs, _outputNames);

        var (rows, rowCount, stride) = LocateOutputs(results);
        return PaddleDetectionDecoder.Decode(rows, rowCount, size, image.Width, image.Height, scoreThreshold, _spec, stride);
    }

    /// <summary>
    /// Finds the detection-row tensor (rank 2, last dim 6 or 7, float) and the optional per-image
    /// count (rank 1, int32) among the outputs, tolerating exporter-specific output names. Any other
    /// tensor (such as a rank-3 int32 mask head) is ignored.
    /// </summary>
    private static (float[] Rows, int RowCount, int RowStride) LocateOutputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        float[]? rows = null;
        int stride = PaddleDetectionDecoder.RowStride;
        int? count = null;

        foreach (var r in results)
        {
            if (r.ElementType == TensorElementType.Float)
            {
                var t = r.AsTensor<float>();
                if (t.Dimensions.Length == 2
                    && (t.Dimensions[1] == PaddleDetectionDecoder.RowStride || t.Dimensions[1] == PaddleDetectionDecoder.OrderedRowStride))
                {
                    rows = t.ToArray();
                    stride = t.Dimensions[1];
                }
            }
            else if (r.ElementType == TensorElementType.Int32)
            {
                var t = r.AsTensor<int>();
                if (t.Dimensions.Length == 1 && t.Length >= 1)
                    count = t.GetValue(0);
            }
        }

        if (rows is null)
            throw new LayoutSharpException(
                "Layout model did not produce the expected [N, 6] or [N, 7] detection tensor " +
                "(class_id, score, x1, y1, x2, y2[, order]). Verify the exported ONNX graph's outputs.");

        int available = rows.Length / stride;
        return (rows, count is > 0 ? Math.Min(count.Value, available) : available, stride);
    }

    public void Dispose() => _session.Dispose();
}
