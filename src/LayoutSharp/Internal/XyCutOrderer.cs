using LayoutSharp.Models;

namespace LayoutSharp.Internal;

/// <summary>
/// Recursive XY-cut reading-order sorter. Repeatedly splits a set of regions along whitespace
/// gaps: a vertical cut separates columns (read left-to-right, each column fully before the next), a
/// horizontal cut peels off stacked bands (top-to-bottom), until each leaf is a single region. When
/// both axes offer a cut, the wider gap wins (Nagy's original rule), which is what tells a
/// two-column body (wide gutter, no full-width gap) apart from a form's rows (wide horizontal
/// gaps, columns that merely happen to line up). Recovers the human reading order for the common
/// cases (single column, multi-column, full-width headers/footers over a multi-column body) without
/// any learned model.
/// </summary>
internal static class XyCutOrderer
{
    /// <summary>
    /// Returns the input items in reading order. The returned list preserves the caller's objects;
    /// only their order changes. Operates on arbitrary payloads via a box selector.
    /// </summary>
    /// <param name="items">Regions to order.</param>
    /// <param name="boxOf">Selects each item's bounding box.</param>
    /// <param name="overlapTolerance">
    /// Two regions whose projections on an axis overlap by less than this many pixels are still
    /// considered separated by a gap. Real detectors emit boxes that touch or overlap by a few
    /// pixels; without a tolerance a single such pair would prevent a column or band split.
    /// </param>
    public static List<T> Order<T>(IReadOnlyList<T> items, Func<T, LayoutBox> boxOf, double overlapTolerance = 0)
    {
        var ordered = new List<T>(items.Count);
        Cut(items.ToList(), boxOf, Math.Max(0, overlapTolerance), ordered);
        return ordered;
    }

    private static void Cut<T>(List<T> group, Func<T, LayoutBox> boxOf, double tol, List<T> output)
    {
        if (group.Count <= 1)
        {
            output.AddRange(group);
            return;
        }

        var (columns, columnGap) = SplitByGaps(group, boxOf, tol, horizontalAxis: true);
        var (bands, bandGap) = SplitByGaps(group, boxOf, tol, horizontalAxis: false);
        bool canCutColumns = columns.Count > 1;
        bool canCutBands = bands.Count > 1;

        if (canCutColumns && (!canCutBands || columnGap >= bandGap))
        {
            // A vertical whitespace corridor splits the group into columns (and it is at least as
            // wide as any horizontal gap). Reading order is column-major: descend each column fully
            // before moving right.
            foreach (var column in columns) // already ordered left-to-right
                Cut(column, boxOf, tol, output);
            return;
        }

        if (canCutBands)
        {
            // Peel only the TOPMOST band and recurse the remainder as a whole: splitting every band at
            // once would slice a two-column body into rows before its columns are recognized (e.g.
            // when a full-width header bridges the columns). Recursing the intact remainder lets the
            // column cut fire on the body below the header.
            Cut(bands[0], boxOf, tol, output);

            var rest = new List<T>();
            for (int i = 1; i < bands.Count; i++)
                rest.AddRange(bands[i]);
            Cut(rest, boxOf, tol, output);
            return;
        }

        // Atomic cluster: no clean separation on either axis. Emit top-to-bottom, then left-to-right.
        group.Sort((a, b) =>
        {
            var ba = boxOf(a);
            var bb = boxOf(b);
            int cmp = ba.MinY.CompareTo(bb.MinY);
            return cmp != 0 ? cmp : ba.MinX.CompareTo(bb.MinX);
        });
        output.AddRange(group);
    }

    /// <summary>
    /// Clusters the group along one axis by whitespace gaps. With <paramref name="horizontalAxis"/>
    /// true it clusters on X (producing left-to-right columns); false clusters on Y (top-to-bottom
    /// bands). Returns the clusters in axis order plus the widest gap between consecutive clusters;
    /// a single cluster means no gap was found (gap reported as negative infinity).
    /// </summary>
    private static (List<List<T>> Clusters, double WidestGap) SplitByGaps<T>(
        List<T> group, Func<T, LayoutBox> boxOf, double tol, bool horizontalAxis)
    {
        // (start, end, item) intervals on the chosen axis.
        var intervals = group
            .Select(item =>
            {
                var b = boxOf(item);
                return horizontalAxis ? (Start: b.MinX, End: b.MaxX, Item: item)
                                      : (Start: b.MinY, End: b.MaxY, Item: item);
            })
            .OrderBy(t => t.Start)
            .ThenBy(t => t.End)
            .ToList();

        var clusters = new List<List<T>>();
        var current = new List<T> { intervals[0].Item };
        double runningMaxEnd = intervals[0].End;
        double widestGap = double.NegativeInfinity;

        for (int i = 1; i < intervals.Count; i++)
        {
            double gap = intervals[i].Start - runningMaxEnd; // negative when the intervals overlap
            // A gap, or an overlap shallower than the tolerance, separates clusters.
            if (gap > -tol)
            {
                clusters.Add(current);
                current = new List<T>();
                if (gap > widestGap) widestGap = gap;
            }
            current.Add(intervals[i].Item);
            if (intervals[i].End > runningMaxEnd) runningMaxEnd = intervals[i].End;
        }
        clusters.Add(current);

        return (clusters, widestGap);
    }
}
