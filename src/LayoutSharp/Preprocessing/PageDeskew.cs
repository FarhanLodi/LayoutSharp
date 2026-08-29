using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace LayoutSharp.Preprocessing;

/// <summary>
/// Result of <see cref="PageDeskew.Estimate"/>: the measured tilt of the page content and how much
/// evidence supports it.
/// </summary>
/// <param name="Angle">
/// Skew of the content in degrees, positive = tilted clockwise. Rotating the page by
/// <c>-Angle</c> (see <see cref="PageDeskew.Rotate"/>) straightens it. 0 when nothing was measured.
/// </param>
/// <param name="Confidence">
/// Relative gain of the row-projection sharpness at <see cref="Angle"/> over the un-rotated page
/// (0 = no evidence of skew; straight pages measure ≈ 0–0.3, clearly skewed pages ≥ 0.5, often
/// far higher). Not bounded above.
/// </param>
/// <param name="IsReliable">
/// True when <c>|Angle|</c> reached the caller's <c>minAngle</c> and <see cref="Confidence"/> the
/// caller's <c>minConfidence</c>; the pipeline only corrects reliable estimates.
/// </param>
public readonly record struct SkewEstimate(double Angle, double Confidence, bool IsReliable);

/// <summary>
/// Small-angle skew estimation and correction for scanned pages, implemented with EasyImageSharp only
/// (no native dependencies). The estimator downsamples the page, binarises it (Otsu), and finds the
/// rotation at which the horizontal projection profile of the ink is sharpest — i.e. at which text
/// lines are horizontal — over ±<c>maxAngle</c> in 0.5° then 0.1° steps.
/// </summary>
/// <remarks>
/// <para>
/// This is the routine <see cref="Services.LayoutService"/> runs when
/// <see cref="Models.LayoutAnalysisOptions.Deskew"/> is enabled; it is public so callers can reproduce
/// the exact corrected image from <see cref="Models.LayoutPage.SkewAngle"/> or deskew pages themselves.
/// </para>
/// <para>
/// Limitations: the projection profile cannot tell 0° from 180° (run orientation correction first),
/// the window is ±<c>maxAngle</c> (default 15°), and pages dominated by non-text content
/// (diagrams, vertical CJK text, tables tilted differently from the text) may yield a wrong maximum.
/// The reliability gate leaves straight pages untouched.
/// </para>
/// </remarks>
public static class PageDeskew
{
    /// <summary>Default search window, in degrees, on either side of 0.</summary>
    public const double DefaultMaxAngle = 15.0;

    /// <summary>Default minimum |angle| for an estimate to count as reliable.</summary>
    public const double DefaultMinAngle = 0.5;

    /// <summary>Default minimum <see cref="SkewEstimate.Confidence"/> for an estimate to count as reliable.</summary>
    public const double DefaultMinConfidence = 0.5;

    private const int WorkingSize = 800;      // longest side of the analysis image
    private const int MaxInkSamples = 150_000; // ink pixels kept (deterministic stride)
    private const int MinInkSamples = 500;     // below this the page is treated as blank
    private const double CoarseStep = 0.5;
    private const double FineStep = 0.1;

    /// <summary>
    /// Estimates the skew of <paramref name="image"/>. The image is not modified.
    /// </summary>
    /// <param name="image">The page (any size; analysed at ≤ 800 px on the longest side).</param>
    /// <param name="maxAngle">Search window ±degrees, in (0, 45].</param>
    /// <param name="minAngle">Estimates with |angle| below this are reported but flagged unreliable (≥ 0).</param>
    /// <param name="minConfidence">Estimates with a sharpness gain below this are flagged unreliable (≥ 0).</param>
    public static SkewEstimate Estimate(
        Image<Rgb24> image,
        double maxAngle = DefaultMaxAngle,
        double minAngle = DefaultMinAngle,
        double minConfidence = DefaultMinConfidence)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!(maxAngle > 0) || maxAngle > 45 || double.IsNaN(maxAngle))
            throw new ArgumentOutOfRangeException(nameof(maxAngle), maxAngle, "maxAngle must be in (0, 45].");
        if (minAngle < 0 || double.IsNaN(minAngle))
            throw new ArgumentOutOfRangeException(nameof(minAngle), minAngle, "minAngle must be non-negative.");
        if (minConfidence < 0 || double.IsNaN(minConfidence))
            throw new ArgumentOutOfRangeException(nameof(minConfidence), minConfidence, "minConfidence must be non-negative.");

        // 1. Downscale (box filter) and convert to 8-bit luma.
        double scale = Math.Min(1.0, (double)WorkingSize / Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)Math.Round(image.Width * scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var gray = new byte[w * h];
        var hist = new int[256];
        if (w == image.Width && h == image.Height)
        {
            ReadLuma(image, gray, hist, w, h);
        }
        else
        {
            using var small = image.Clone(c => c.Resize(new ResizeOptions
            {
                Size = new Size(w, h),
                Sampler = KnownResamplers.Box,
                Mode = ResizeMode.Stretch,
            }));
            ReadLuma(small, gray, hist, w, h);
        }

        // 2. Otsu threshold; ink = darker class.
        int threshold = OtsuThreshold(hist, (long)w * h);

        // 3. Collect ink coordinates with a deterministic stride so at most ~MaxInkSamples are kept.
        int inkCount = 0;
        for (int i = 0; i < gray.Length; i++) if (gray[i] < threshold) inkCount++;
        if (inkCount < MinInkSamples) return new SkewEstimate(0, 0, false);

        int stride = Math.Max(1, inkCount / MaxInkSamples);
        int n = inkCount / stride;
        var xs = new float[n];
        var ys = new float[n];
        for (int y = 0, seen = 0, k = 0; y < h && k < n; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w && k < n; x++)
            {
                if (gray[rowBase + x] >= threshold) continue;
                if (seen++ % stride == 0) { xs[k] = x; ys[k] = y; k++; }
            }
        }

        // 4. Sharpness of the row-projection profile as a function of rotation angle.
        int binCount = (int)Math.Sqrt((double)w * w + (double)h * h) + 3;
        var bins = new int[binCount];

        double Score(double degrees)
        {
            double th = degrees * Math.PI / 180.0;
            float sn = (float)Math.Sin(th), cs = (float)Math.Cos(th);
            // Row coordinate after rotating the frame by `degrees`; shift by the min over the corners.
            float minRow = Math.Min(Math.Min(0f, w * sn), Math.Min(h * cs, w * sn + h * cs));
            Array.Clear(bins);
            for (int i = 0; i < n; i++)
            {
                int b = (int)(xs[i] * sn + ys[i] * cs - minRow);
                if ((uint)b < (uint)binCount) bins[b]++;
            }
            double sum = 0;
            for (int i = 1; i < binCount; i++)
            {
                double d = bins[i] - bins[i - 1];
                sum += d * d;
            }
            return sum;
        }

        double bestAngle = 0, bestScore = double.NegativeInfinity;
        for (double a = -maxAngle; a <= maxAngle + 1e-9; a += CoarseStep)
            Consider(a);
        double centre = bestAngle;
        for (double a = centre - CoarseStep; a <= centre + CoarseStep + 1e-9; a += FineStep)
            Consider(a);

        void Consider(double a)
        {
            double s = Score(a);
            if (s > bestScore) { bestScore = s; bestAngle = a; }
        }

        double straight = Score(0);
        double confidence = straight > 0 ? Math.Max(0, (bestScore - straight) / straight) : 0;
        // Score() rotates the coordinate frame by +bestAngle; the content itself is tilted the other way.
        double angle = -Math.Round(bestAngle, 3);
        if (angle == 0) angle = 0; // normalise -0.0
        bool reliable = Math.Abs(angle) >= minAngle && confidence >= minConfidence;
        return new SkewEstimate(angle, confidence, reliable);
    }

    /// <summary>
    /// Returns a new image rotated by <paramref name="degrees"/> (positive = clockwise) about its
    /// centre with bicubic resampling. The canvas grows to hold the whole rotated page and the
    /// uncovered corners are filled with white. The input is not modified; the caller disposes the result.
    /// To straighten a page whose <see cref="SkewEstimate.Angle"/> is <c>a</c>, call <c>Rotate(page, -a)</c>.
    /// </summary>
    public static Image<Rgb24> Rotate(Image<Rgb24> image, double degrees)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!double.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "degrees must be a finite number.");
        if (degrees == 0) return image.Clone();

        // EasyImageSharp fills the exposed corners of an Rgb24 rotation with black. Rotating the
        // colour-inverted page turns that black into white after inverting back, and because
        // inversion is affine the bicubic interpolation of the content is unaffected. This avoids the
        // Rgba32 round-trip (two extra 4 B/px copies) that BackgroundColor() would need.
        return image.Clone(c => c
            .Invert()
            .Rotate((float)degrees, KnownResamplers.Bicubic)
            .Invert());
    }

    private static void ReadLuma(Image<Rgb24> img, byte[] gray, int[] hist, int w, int h)
    {
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    var p = row[x];
                    // ITU-R BT.601 luma, integer form.
                    byte v = (byte)((299 * p.R + 587 * p.G + 114 * p.B + 500) / 1000);
                    gray[rowBase + x] = v;
                    hist[v]++;
                }
            }
        });
    }

    private static int OtsuThreshold(int[] hist, long total)
    {
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];

        double sumB = 0, wB = 0, best = -1;
        int threshold = 0;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            double wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t; }
        }
        // hist bin `threshold` belongs to the background class; ink is strictly darker.
        return threshold + 1;
    }
}
