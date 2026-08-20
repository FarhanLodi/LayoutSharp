namespace LayoutSharp.Models;

/// <summary>
/// An axis-aligned bounding box in original-image pixel coordinates.
/// </summary>
public readonly record struct LayoutBox(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>An empty box at the origin.</summary>
    public static LayoutBox Empty { get; } = new(0, 0, 0, 0);

    /// <summary>Width of the box (never negative).</summary>
    public double Width => Math.Max(0, MaxX - MinX);

    /// <summary>Height of the box (never negative).</summary>
    public double Height => Math.Max(0, MaxY - MinY);

    /// <summary>Area of the box.</summary>
    public double Area => Width * Height;

    /// <summary>Horizontal center.</summary>
    public double CenterX => MinX + (Width / 2.0);

    /// <summary>Vertical center.</summary>
    public double CenterY => MinY + (Height / 2.0);

    /// <summary>True when the box has no area.</summary>
    public bool IsEmpty => Width <= 0 && Height <= 0;

    /// <summary>Area of the overlap with another box; 0 when the boxes do not overlap.</summary>
    public double IntersectionArea(LayoutBox other)
    {
        double ix = Math.Max(0, Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX));
        double iy = Math.Max(0, Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY));
        return ix * iy;
    }

    /// <summary>
    /// Intersection-over-union with another box, in [0, 1]. 0 when the boxes do not overlap.
    /// </summary>
    public double IntersectionOverUnion(LayoutBox other)
    {
        double inter = IntersectionArea(other);
        double union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>
    /// Resolves the box to an integer pixel rectangle (x, y, width, height) clamped to the
    /// given image bounds. Width/height are 0 when the box is empty after clamping.
    /// </summary>
    public (int X, int Y, int Width, int Height) ToPixelRect(int imageWidth, int imageHeight)
    {
        int x = (int)Math.Round(Math.Clamp(MinX, 0, imageWidth));
        int y = (int)Math.Round(Math.Clamp(MinY, 0, imageHeight));
        int w = (int)Math.Round(Math.Clamp(MaxX, 0, imageWidth)) - x;
        int h = (int)Math.Round(Math.Clamp(MaxY, 0, imageHeight)) - y;
        return (x, y, Math.Max(0, w), Math.Max(0, h));
    }
}
