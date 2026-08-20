using LayoutSharp.Internal;
using LayoutSharp.Models;
using Xunit;

namespace LayoutSharp.Tests;

public class XyCutOrdererTests
{
    // Helper: a labeled box so assertions read in terms of intended reading order.
    private static (string Label, LayoutBox Box) B(string label, double x, double y, double w, double h)
        => (label, new LayoutBox(x, y, x + w, y + h));

    private static List<string> Order(params (string Label, LayoutBox Box)[] items)
        => XyCutOrderer.Order(items, i => i.Box).Select(i => i.Label).ToList();

    private static List<string> OrderTol(double tol, params (string Label, LayoutBox Box)[] items)
        => XyCutOrderer.Order(items, i => i.Box, tol).Select(i => i.Label).ToList();

    [Fact]
    public void SingleColumn_OrdersTopToBottom()
    {
        var order = Order(
            B("c", 0, 200, 100, 40),
            B("a", 0, 0, 100, 40),
            B("b", 0, 100, 100, 40));

        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public void TwoColumns_ReadsLeftColumnFullyThenRight()
    {
        // Left column x∈[0,100], right column x∈[200,300]; each has two stacked blocks.
        var order = Order(
            B("R1", 200, 0, 100, 40),
            B("L2", 0, 100, 100, 40),
            B("R2", 200, 100, 100, 40),
            B("L1", 0, 0, 100, 40));

        Assert.Equal(new[] { "L1", "L2", "R1", "R2" }, order);
    }

    [Fact]
    public void FullWidthHeaderOverTwoColumns_HeaderFirstThenColumns()
    {
        // A header spanning both columns bridges the vertical gap, forcing a horizontal cut first.
        var order = Order(
            B("L1", 0, 100, 100, 40),
            B("R1", 200, 100, 100, 40),
            B("Header", 0, 0, 300, 40),
            B("L2", 0, 160, 100, 40),
            B("R2", 200, 160, 100, 40));

        Assert.Equal(new[] { "Header", "L1", "L2", "R1", "R2" }, order);
    }

    [Fact]
    public void TwoColumnsWithStaggeredParagraphs_StaysColumnMajor()
    {
        // Paragraph breaks do not line up across columns, so no full-width gap exists.
        var order = Order(
            B("R1", 200, 0, 100, 70),
            B("L1", 0, 0, 100, 40),
            B("L2", 0, 50, 100, 40),
            B("R2", 200, 80, 100, 40),
            B("L3", 0, 100, 100, 40));

        Assert.Equal(new[] { "L1", "L2", "L3", "R1", "R2" }, order);
    }

    [Fact]
    public void WidestGapWins_FormRowsAreReadRowMajor()
    {
        // Two rows of two fields. The columns are separated by only 10px, the rows by 100px:
        // Nagy's rule cuts the wider (horizontal) gap first, giving row-major order.
        var order = Order(
            B("A", 0, 0, 100, 20),
            B("B", 110, 0, 100, 20),
            B("C", 0, 120, 100, 20),
            B("D", 110, 120, 100, 20));

        Assert.Equal(new[] { "A", "B", "C", "D" }, order);
    }

    [Fact]
    public void WidestGapWins_WideGutterIsReadColumnMajor()
    {
        // Same grid, but the gutter (200px) is wider than the row gap (20px): column-major.
        var order = Order(
            B("A", 0, 0, 100, 20),
            B("B", 300, 0, 100, 20),
            B("C", 0, 40, 100, 20),
            B("D", 300, 40, 100, 20));

        Assert.Equal(new[] { "A", "C", "B", "D" }, order);
    }

    [Fact]
    public void OverlapTolerance_AllowsColumnSplit_WhenBoxesOverlapSlightly()
    {
        // Two columns with staggered paragraphs (no full-width horizontal gap), where the right
        // column's boxes overlap the left column by 3px, as detectors routinely produce.
        var items = new[]
        {
            B("L1", 0, 0, 100, 40),
            B("L2", 0, 50, 100, 40),
            B("L3", 0, 100, 100, 40),
            B("R1", 97, 0, 100, 70),
            B("R2", 97, 80, 100, 40),
        };

        // Strict cut: no gap on either axis → atomic top-to-bottom sort interleaves the columns.
        Assert.Equal(new[] { "L1", "R1", "L2", "R2", "L3" }, OrderTol(0, items));
        // With a 5px tolerance the 3px overlap still counts as a gutter → column-major.
        Assert.Equal(new[] { "L1", "L2", "L3", "R1", "R2" }, OrderTol(5, items));
    }

    [Fact]
    public void OverlapTolerance_DoesNotForceColumnsAcrossAWideRowGap()
    {
        // Two rows (mirrors the bottom of a scanned form). The bottom-left label barely overlaps
        // the top-right field on X (4px) and a vertical side note barely overlaps that field's end
        // (2px), so both column "gutters" exist only thanks to the tolerance; the horizontal gap
        // (85px) is far wider, so rows win and the order is row-major.
        var order = OrderTol(5,
            B("A", 0, 0, 90, 10),
            B("B", 200, 0, 300, 15),
            B("C", 40, 120, 164, 10),   // ends at 204, overlapping B's start (200) by 4px
            B("D", 498, 100, 20, 100)); // starts at 498, overlapping B's end (500) by 2px

        Assert.Equal(new[] { "A", "B", "C", "D" }, order);
    }

    [Fact]
    public void SingleBlock_ReturnsItself()
    {
        var order = Order(B("only", 10, 10, 50, 50));
        Assert.Equal(new[] { "only" }, order);
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        var order = XyCutOrderer.Order(Array.Empty<(string, LayoutBox)>(), i => i.Item2);
        Assert.Empty(order);
    }

    [Fact]
    public void PreservesAllItems()
    {
        var items = new[]
        {
            B("a", 0, 0, 100, 40),
            B("b", 0, 50, 100, 40),
            B("c", 200, 0, 100, 40),
        };
        var order = XyCutOrderer.Order(items, i => i.Box);
        Assert.Equal(3, order.Count);
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, order.Select(i => i.Label).ToHashSet());
    }

    [Fact]
    public void NestedBoxes_StayTogether_AndSortTopLeftFirst()
    {
        // A caption inside a figure: no cut possible; atomic sort by top then left.
        var order = Order(
            B("Caption", 10, 370, 200, 20),
            B("Figure", 0, 0, 400, 400));
        Assert.Equal(new[] { "Figure", "Caption" }, order);
    }
}
