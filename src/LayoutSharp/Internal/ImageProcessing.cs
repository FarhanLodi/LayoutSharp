using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace LayoutSharp.Internal;

/// <summary>
/// Which resampler the input resize uses. Each training framework pins its own, and using the wrong
/// one shifts every edge in the tensor by a fraction of a pixel — enough to move box coordinates and
/// nudge borderline scores across the confidence threshold.
/// </summary>
internal enum ResizeInterpolation
{
    /// <summary>
    /// Bilinear. Hugging Face <c>RTDetrImageProcessor</c> (<c>resample: BILINEAR</c>) and
    /// Ultralytics <c>LetterBox</c> (<c>cv2.INTER_LINEAR</c>).
    /// </summary>
    Bilinear,

    /// <summary>
    /// Bicubic. PaddleDetection / PaddleX exports declare <c>interp: 2</c> in their
    /// <c>inference.yml</c>, which is <c>cv2.INTER_CUBIC</c>.
    /// </summary>
    Bicubic,
}

/// <summary>
/// Image preprocessing for the layout detector, reproducing the pipeline each model was trained
/// with: resize to <c>S×S</c> without keeping the aspect ratio → rescale <c>1/255</c> → (optional)
/// ImageNet mean/std → NCHW.
/// </summary>
/// <remarks>
/// The two shipped detectors differ only in the resampler and the mean/std step, both driven by the
/// spec. PP-DocLayoutV3's <c>inference.yml</c> is
/// <c>Resize(target_size: [800, 800], keep_ratio: false, interp: 2)</c> →
/// <c>NormalizeImage(norm_type: none, is_scale default true)</c> → <c>Permute</c>, i.e. bicubic
/// stretch and a plain <c>1/255</c>. Heron's <c>preprocessor_config.json</c> is the Hugging Face
/// RT-DETR equivalent with <c>do_normalize: false</c> and bilinear resampling.
/// </remarks>
internal static class ImageProcessing
{
    // ImageNet statistics, applied only when a model spec asks for them (RT-DETR ones do not).
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>
    /// Resizes <paramref name="source"/> to <paramref name="size"/>×<paramref name="size"/> (plain
    /// stretch, no letterbox) with the resampler <paramref name="interpolation"/> selects, rescales
    /// pixels to [0, 1], optionally applies ImageNet mean/std normalization, and packs the result
    /// into an NCHW <c>float</c> tensor in RGB channel order.
    /// </summary>
    /// <remarks>
    /// Because the resize is a stretch, boxes the model emits in its S×S input space map back to the
    /// source by scaling x by <c>srcW/S</c> and y by <c>srcH/S</c>; no padding offset to undo.
    /// </remarks>
    public static float[] Preprocess(
        Image<Rgb24> source,
        int size,
        bool imageNetNormalize,
        ResizeInterpolation interpolation = ResizeInterpolation.Bilinear)
    {
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(size, size),
            Sampler = SamplerFor(interpolation),
            Mode = ResizeMode.Stretch,
        }));

        var tensor = new float[3 * size * size];
        int hw = size * size;

        // Fold the two normalizations into one scale/offset per channel: v = p * scale[c] + offset[c].
        Span<float> scale = stackalloc float[3];
        Span<float> offset = stackalloc float[3];
        for (int c = 0; c < 3; c++)
        {
            if (imageNetNormalize)
            {
                scale[c] = 1f / (255f * Std[c]);
                offset[c] = -Mean[c] / Std[c];
            }
            else
            {
                scale[c] = 1f / 255f;
                offset[c] = 0f;
            }
        }

        float sr = scale[0], sg = scale[1], sb = scale[2];
        float offR = offset[0], offG = offset[1], offB = offset[2];

        resized.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < size; y++)
            {
                var row = rows.GetRowSpan(y);
                int baseIdx = y * size;
                for (int x = 0; x < size; x++)
                {
                    var px = row[x];
                    tensor[baseIdx + x] = px.R * sr + offR;
                    tensor[hw + baseIdx + x] = px.G * sg + offG;
                    tensor[2 * hw + baseIdx + x] = px.B * sb + offB;
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Maps an interpolation mode to the resampler that matches it. EasyImageSharp's
    /// <c>Bicubic</c> is a Catmull-Rom cubic, the closest available match to OpenCV's
    /// <c>INTER_CUBIC</c> (which uses a = -0.75); they agree to well under a grey level on document
    /// imagery, far closer than bilinear does.
    /// </summary>
    private static IResampler SamplerFor(ResizeInterpolation interpolation) => interpolation switch
    {
        ResizeInterpolation.Bilinear => KnownResamplers.Triangle,
        ResizeInterpolation.Bicubic => KnownResamplers.Bicubic,
        _ => throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, "Unknown interpolation."),
    };

    /// <summary>
    /// PaddleClas-style classification preprocessing: resize (bilinear) so the short side equals
    /// <paramref name="shortSide"/> preserving aspect ratio, take the centred
    /// <paramref name="crop"/>×<paramref name="crop"/> window, rescale to [0, 1], apply ImageNet
    /// mean/std, and pack into an NCHW <c>float</c> tensor in RGB channel order. Used by the
    /// document-orientation classifier (256 → 224); a plain stretch mis-classifies, so the crop is
    /// not optional.
    /// </summary>
    public static float[] PreprocessCenterCrop(Image<Rgb24> source, int shortSide = 256, int crop = 224)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (shortSide < crop) throw new ArgumentOutOfRangeException(nameof(shortSide), shortSide, "shortSide must be at least the crop size.");

        int w = source.Width, h = source.Height;
        int nw, nh;
        if (w <= h)
        {
            nw = shortSide;
            nh = Math.Max(shortSide, (int)Math.Round((double)h * shortSide / w));
        }
        else
        {
            nh = shortSide;
            nw = Math.Max(shortSide, (int)Math.Round((double)w * shortSide / h));
        }

        var window = new Rectangle((nw - crop) / 2, (nh - crop) / 2, crop, crop);
        using var cropped = source.Clone(ctx => ctx
            .Resize(new ResizeOptions { Size = new Size(nw, nh), Sampler = KnownResamplers.Triangle, Mode = ResizeMode.Stretch })
            .Crop(window));

        var tensor = new float[3 * crop * crop];
        int hw = crop * crop;
        float sr = 1f / (255f * Std[0]), sg = 1f / (255f * Std[1]), sb = 1f / (255f * Std[2]);
        float offR = -Mean[0] / Std[0], offG = -Mean[1] / Std[1], offB = -Mean[2] / Std[2];

        cropped.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < crop; y++)
            {
                var row = rows.GetRowSpan(y);
                int baseIdx = y * crop;
                for (int x = 0; x < crop; x++)
                {
                    var px = row[x];
                    tensor[baseIdx + x] = px.R * sr + offR;
                    tensor[hw + baseIdx + x] = px.G * sg + offG;
                    tensor[2 * hw + baseIdx + x] = px.B * sb + offB;
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Letterbox preprocessing for Ultralytics-style detectors: scales <paramref name="source"/> by
    /// <c>min(size/w, size/h)</c> (bilinear, aspect preserved), centres it on a <paramref name="size"/>×
    /// <paramref name="size"/> canvas filled with gray 114, then rescales / normalizes and packs the
    /// result exactly like <see cref="Preprocess"/>. <paramref name="letterbox"/> receives the scale
    /// and padding needed to map boxes back to the source (see <see cref="Letterbox.ToSource"/>).
    /// </summary>
    public static float[] PreprocessLetterbox(
        Image<Rgb24> source,
        int size,
        bool imageNetNormalize,
        out Letterbox letterbox,
        ResizeInterpolation interpolation = ResizeInterpolation.Bilinear)
    {
        letterbox = Letterbox.Compute(source.Width, source.Height, size);
        var lb = letterbox;

        using var canvas = new Image<Rgb24>(size, size, new Rgb24(114, 114, 114));
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(lb.ContentWidth, lb.ContentHeight),
            Sampler = SamplerFor(interpolation), // Ultralytics LetterBox: cv2.INTER_LINEAR
            Mode = ResizeMode.Stretch,
        }));
        canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(lb.Left, lb.Top), 1f));

        // The canvas already has the model's input size, so the stretch inside Preprocess is an
        // identity resample; only the rescale / normalize / pack step does real work.
        return Preprocess(canvas, size, imageNetNormalize, interpolation);
    }
}
