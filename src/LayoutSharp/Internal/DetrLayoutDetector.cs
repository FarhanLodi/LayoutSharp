using LayoutSharp.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace LayoutSharp.Internal;

/// <summary>
/// Decodes the raw head of a DETR-family detector (RT-DETR, RT-DETRv2, D-FINE as exported from
/// Hugging Face <c>transformers</c>) into <see cref="RawDetection"/>s in source-image pixel
/// coordinates.
/// </summary>
/// <remarks>
/// The graph emits <c>logits [1, Q, C]</c> (pre-sigmoid, one score per class per query) and
/// <c>pred_boxes [1, Q, 4]</c> (<c>cx, cy, w, h</c> normalized to the input, i.e. in [0, 1]).
/// DETR heads are NMS-free: each of the Q object queries is one candidate. This decoder keeps, per
/// query, its best class when the sigmoid score clears the threshold — after
/// <see cref="Services.LayoutService.Deduplicate"/> that is equivalent to the reference top-k
/// post-processing (a query's runner-up class can only ever lose the IoU tie-break to its own best class).
/// </remarks>
internal static class DetrDecoder
{
    /// <summary>Number of floats per box.</summary>
    public const int BoxStride = 4;

    /// <summary>
    /// Decodes <paramref name="queryCount"/> queries from <paramref name="logits"/> (flat, row-major,
    /// stride <paramref name="classCount"/>) and <paramref name="boxes"/> (flat, stride
    /// <see cref="BoxStride"/>), keeping those whose best-class sigmoid score is at or above
    /// <paramref name="scoreThreshold"/>. Boxes are mapped from normalized <c>cx, cy, w, h</c> to
    /// <paramref name="srcW"/>×<paramref name="srcH"/> pixel corners and clamped to the image.
    /// </summary>
    public static List<RawDetection> Decode(
        ReadOnlySpan<float> logits,
        ReadOnlySpan<float> boxes,
        int queryCount,
        int classCount,
        int srcW,
        int srcH,
        float scoreThreshold,
        LayoutModelSpec spec)
    {
        if (classCount <= 0) return new List<RawDetection>();

        int available = Math.Min(logits.Length / classCount, boxes.Length / BoxStride);
        if (queryCount > available) queryCount = available;

        // Compare in logit space and apply the sigmoid once per survivor.
        float logitThreshold = LogitOf(scoreThreshold);

        var detections = new List<RawDetection>(Math.Min(queryCount, 64));
        for (int q = 0; q < queryCount; q++)
        {
            int lo = q * classCount;
            int best = 0;
            float bestLogit = logits[lo];
            for (int c = 1; c < classCount; c++)
            {
                float l = logits[lo + c];
                if (l > bestLogit) { bestLogit = l; best = c; }
            }

            if (float.IsNaN(bestLogit) || bestLogit < logitThreshold) continue;

            int bo = q * BoxStride;
            float cx = boxes[bo], cy = boxes[bo + 1], w = boxes[bo + 2], h = boxes[bo + 3];
            if (float.IsNaN(cx) || float.IsNaN(cy) || float.IsNaN(w) || float.IsNaN(h)) continue;

            double minX = Math.Clamp((cx - w / 2f) * srcW, 0, srcW);
            double minY = Math.Clamp((cy - h / 2f) * srcH, 0, srcH);
            double maxX = Math.Clamp((cx + w / 2f) * srcW, 0, srcW);
            double maxY = Math.Clamp((cy + h / 2f) * srcH, 0, srcH);

            var box = new LayoutBox(minX, minY, maxX, maxY);
            if (box.Width < 1 || box.Height < 1) continue;

            detections.Add(new RawDetection(box, spec.Resolve(best), Sigmoid(bestLogit)));
        }

        return detections;
    }

    /// <summary>Numerically safe sigmoid clamped to [0, 1].</summary>
    internal static float Sigmoid(float x)
    {
        if (x >= 0)
        {
            float e = MathF.Exp(-x);
            return 1f / (1f + e);
        }
        else
        {
            float e = MathF.Exp(x);
            return e / (1f + e);
        }
    }

    /// <summary>Inverse sigmoid; probabilities at or below 0 / at or above 1 map to ∓∞ so every / no query passes.</summary>
    internal static float LogitOf(float p)
    {
        if (p <= 0f) return float.NegativeInfinity;
        if (p >= 1f) return float.PositiveInfinity;
        return MathF.Log(p / (1f - p));
    }
}

/// <summary>
/// Runs a DETR-family ONNX session (raw <c>logits</c> + <c>pred_boxes</c> head, no NMS) and decodes
/// its output. One instance per model; <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
/// is thread-safe, so a single detector serves concurrent callers.
/// </summary>
internal sealed class DetrLayoutDetector : IDetectionSession
{
    private readonly InferenceSession _session;
    private readonly LayoutModelSpec _spec;
    private readonly string _imageInput;

    public DetrLayoutDetector(string modelPath, LayoutModelSpec spec, SessionOptions sessionOptions)
    {
        _spec = spec;
        _session = new InferenceSession(modelPath, sessionOptions);

        // transformers exports name the image "pixel_values"; fall back to "the rank-4 input is the image".
        string? image = null;
        foreach (var (name, meta) in _session.InputMetadata)
        {
            if (name == "pixel_values" || (image is null && meta.Dimensions.Length == 4)) image = name;
        }

        _imageInput = image ?? throw new LayoutSharpException(
            $"Layout model '{spec.FileName}' has no rank-4 image input. Inputs: {string.Join(", ", _session.InputMetadata.Keys)}.");
    }

    /// <inheritdoc />
    public IReadOnlyList<RawDetection> Detect(Image<Rgb24> image, float scoreThreshold)
    {
        int size = _spec.InputSize;
        var pixels = ImageProcessing.Preprocess(image, size, _spec.ImageNetNormalize, _spec.Interpolation);

        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(_imageInput, new DenseTensor<float>(pixels, new[] { 1, 3, size, size })),
        };

        using var results = _session.Run(inputs);

        var (logits, boxes, queries, classes) = LocateOutputs(results, _spec.ClassCount);
        if (_spec.LocalPath is not null && classes != _spec.ClassCount && classes != _spec.ClassCount + 1)
        {
            // Built-in specs are verified against their graph; a custom label list is the user's
            // claim, so check it against the logits axis (C, or C+1 with a trailing no-object class).
            throw new LayoutSharpException(
                $"Custom layout model '{_spec.FileName}' emits logits with {classes} classes but CustomLayoutModel.Labels " +
                $"lists {_spec.ClassCount}. Supply the labels in the model's id2label order (one per logits column).");
        }
        return DetrDecoder.Decode(logits, boxes, queries, classes, image.Width, image.Height, scoreThreshold, _spec);
    }

    /// <summary>
    /// Finds the <c>logits [1, Q, C]</c> and <c>pred_boxes [1, Q, 4]</c> tensors among the outputs,
    /// by name where the transformers conventions hold and by shape otherwise.
    /// </summary>
    private static (float[] Logits, float[] Boxes, int Queries, int Classes) LocateOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int expectedClasses)
    {
        float[]? logits = null, boxes = null;
        int queries = 0, classes = 0;

        foreach (var r in results)
        {
            if (r.ElementType != TensorElementType.Float) continue;
            var t = r.AsTensor<float>();
            if (t.Dimensions.Length != 3) continue;

            int q = t.Dimensions[1], last = t.Dimensions[2];
            if (r.Name == "pred_boxes" || (boxes is null && last == DetrDecoder.BoxStride && r.Name != "logits"))
            {
                boxes = t.ToArray();
                queries = queries == 0 ? q : Math.Min(queries, q);
            }
            else if (r.Name == "logits" || (logits is null && (last == expectedClasses || last > DetrDecoder.BoxStride)))
            {
                logits = t.ToArray();
                classes = last;
                queries = queries == 0 ? q : Math.Min(queries, q);
            }
        }

        if (logits is null || boxes is null)
            throw new LayoutSharpException(
                "Layout model did not produce the expected DETR outputs (logits [1,Q,C] and pred_boxes [1,Q,4]). " +
                "Verify the exported ONNX graph's outputs.");

        return (logits, boxes, queries, classes);
    }

    public void Dispose() => _session.Dispose();
}
