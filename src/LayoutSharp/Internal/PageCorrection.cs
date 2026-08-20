using LayoutSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LayoutSharp.Internal;

/// <summary>
/// Geometry shared by the page-correction stage of <see cref="Services.LayoutService"/> and
/// <see cref="LayoutPage.MapToSource(double, double)"/>: how a source page is made upright, and how
/// corrected-frame coordinates map back to the caller's image.
/// </summary>
/// <remarks>
/// Frames: <b>source</b> (the caller's image, <c>W0×H0</c>) → <b>upright</b> (after the 90-degree
/// step, <c>W1×H1</c>; swapped when the rotation is 90/270) → <b>corrected</b> (after deskew, the
/// canvas grows to <c>W2×H2</c> = <see cref="LayoutPage.Width"/>×<see cref="LayoutPage.Height"/>).
/// Deskew rotates about the upright centre and centres the result on the corrected canvas, which is
/// exactly what ImageSharp's expanding <c>Rotate</c> does.
/// </remarks>
internal static class PageCorrection
{
    /// <summary>
    /// Returns a clone of <paramref name="image"/> rotated so that content reported as
    /// <paramref name="rotation"/> degrees clockwise (0/90/180/270) becomes upright, i.e. rotated
    /// <c>(360 - rotation) % 360</c> degrees clockwise. Rotation 0 returns a plain clone.
    /// </summary>
    public static Image<Rgb24> Upright(Image<Rgb24> image, int rotation)
    {
        ArgumentNullException.ThrowIfNull(image);
        var mode = rotation switch
        {
            0 => RotateMode.None,
            90 => RotateMode.Rotate270,
            180 => RotateMode.Rotate180,
            270 => RotateMode.Rotate90,
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Rotation must be 0, 90, 180 or 270."),
        };
        return mode == RotateMode.None ? image.Clone() : image.Clone(c => c.Rotate(mode));
    }

    /// <summary>Size of the page after the 90-degree step but before deskew.</summary>
    public static (int Width, int Height) UprightSize(int sourceWidth, int sourceHeight, int rotation)
        => rotation is 90 or 270 ? (sourceHeight, sourceWidth) : (sourceWidth, sourceHeight);

    /// <summary>
    /// Maps a point in the corrected frame (<paramref name="width"/>×<paramref name="height"/>) back
    /// to the source frame (<paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>): inverse
    /// deskew about the upright centre, then the inverse 90-degree step. Continuous coordinates;
    /// exact for pure 90-degree rotations.
    /// </summary>
    public static (double X, double Y) MapToSource(
        double x, double y,
        int rotation, double skewAngle,
        int sourceWidth, int sourceHeight,
        int width, int height)
    {
        var (w1, h1) = UprightSize(sourceWidth, sourceHeight, rotation);

        // 1. Undo the deskew: the page was rotated by -skewAngle (clockwise-positive) about the upright
        //    centre and centred on the larger corrected canvas; rotate back by +skewAngle.
        double x1 = x, y1 = y;
        if (skewAngle != 0)
        {
            double s = skewAngle * Math.PI / 180.0;
            double cos = Math.Cos(s), sin = Math.Sin(s);
            double dx = x - width / 2.0, dy = y - height / 2.0;
            x1 = cos * dx - sin * dy + w1 / 2.0;
            y1 = sin * dx + cos * dy + h1 / 2.0;
        }

        // 2. Undo the 90-degree step (rotation = clockwise rotation the source content had; the page
        //    was rotated the other way to make it upright).
        return rotation switch
        {
            90 => (sourceWidth - y1, x1),                 // page was rotated 90° counter-clockwise
            180 => (sourceWidth - x1, sourceHeight - y1),
            270 => (y1, sourceHeight - x1),               // page was rotated 90° clockwise
            _ => (x1, y1),
        };
    }

    /// <summary>
    /// Maps a corrected-frame box to the axis-aligned bounds of its four corners in the source frame.
    /// Exact for pure 90-degree rotations; an enclosing rectangle when the page was deskewed.
    /// </summary>
    public static LayoutBox MapToSource(
        LayoutBox box,
        int rotation, double skewAngle,
        int sourceWidth, int sourceHeight,
        int width, int height)
    {
        Span<(double X, double Y)> corners = stackalloc (double, double)[4];
        corners[0] = MapToSource(box.MinX, box.MinY, rotation, skewAngle, sourceWidth, sourceHeight, width, height);
        corners[1] = MapToSource(box.MaxX, box.MinY, rotation, skewAngle, sourceWidth, sourceHeight, width, height);
        corners[2] = MapToSource(box.MaxX, box.MaxY, rotation, skewAngle, sourceWidth, sourceHeight, width, height);
        corners[3] = MapToSource(box.MinX, box.MaxY, rotation, skewAngle, sourceWidth, sourceHeight, width, height);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (cx, cy) in corners)
        {
            if (cx < minX) minX = cx;
            if (cx > maxX) maxX = cx;
            if (cy < minY) minY = cy;
            if (cy > maxY) maxY = cy;
        }
        return new LayoutBox(minX, minY, maxX, maxY);
    }
}

/// <summary>
/// The outcome of the page-correction stage: the image the rest of the pipeline works on (the
/// caller's image itself when nothing was applied) plus what was applied. Disposing releases the
/// working image only when it was created by the stage.
/// </summary>
internal sealed class CorrectedPage : IDisposable
{
    public CorrectedPage(Image<Rgb24> image, bool owned, int rotation, double skewAngle, int sourceWidth, int sourceHeight)
    {
        Image = image;
        Owned = owned;
        Rotation = rotation;
        SkewAngle = skewAngle;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <summary>The image detection, ordering and recognition run on.</summary>
    public Image<Rgb24> Image { get; }

    /// <summary>True when <see cref="Image"/> was created by the correction stage and must be disposed with this object.</summary>
    public bool Owned { get; }

    /// <summary>Clockwise rotation (0/90/180/270) the source content had; 0 when none was applied.</summary>
    public int Rotation { get; }

    /// <summary>Skew (clockwise-positive degrees) that was corrected; 0 when none was applied.</summary>
    public double SkewAngle { get; }

    /// <summary>Width of the image the caller supplied, before any correction.</summary>
    public int SourceWidth { get; }

    /// <summary>Height of the image the caller supplied, before any correction.</summary>
    public int SourceHeight { get; }

    public void Dispose()
    {
        if (Owned) Image.Dispose();
    }
}
