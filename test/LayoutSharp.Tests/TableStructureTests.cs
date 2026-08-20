using LayoutSharp.Models;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// The dependency-free HTML table parser and the TableStructure rendering helpers
/// (grid expansion, HTML, Markdown, CSV, offsetting).
/// </summary>
public class TableStructureTests
{
    private static TableCell Cell(int row, int col, string? text, int rowSpan = 1, int colSpan = 1, bool header = false, LayoutBox? box = null)
        => new() { Row = row, Column = col, RowSpan = rowSpan, ColumnSpan = colSpan, IsHeader = header, Text = text, BoundingBox = box };

    private static string N(string s) => s.ReplaceLineEndings("\n");

    // ---- FromHtml ----

    [Fact]
    public void FromHtml_SimpleTable_ProducesOriginCells()
    {
        var t = TableStructure.FromHtml("<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>");

        Assert.NotNull(t);
        Assert.Equal(2, t!.RowCount);
        Assert.Equal(2, t.ColumnCount);
        Assert.False(t.HasSpans);
        Assert.False(t.IsEmpty);
        Assert.Equal(new[] { Cell(0, 0, "a"), Cell(0, 1, "b"), Cell(1, 0, "c"), Cell(1, 1, "d") }, t.Cells);
    }

    [Fact]
    public void FromHtml_PreservesSpans_AndGridExpandsThem()
    {
        // A spans two rows; B, C, D flow around it. E spans two columns.
        var html = "<table><tr><td rowspan=\"2\">A</td><td>B</td></tr><tr><td>C</td></tr><tr><td colspan=\"2\">E</td></tr></table>";
        var t = TableStructure.FromHtml(html)!;

        Assert.Equal(3, t.RowCount);
        Assert.Equal(2, t.ColumnCount);
        Assert.True(t.HasSpans);
        Assert.Equal(new[] { Cell(0, 0, "A", rowSpan: 2), Cell(0, 1, "B"), Cell(1, 1, "C"), Cell(2, 0, "E", colSpan: 2) }, t.Cells);

        var grid = t.ToGrid();
        Assert.Equal(new[] { "A", "B" }, grid[0]);
        Assert.Equal(new[] { "A", "C" }, grid[1]);   // rowspan carried down, C pushed right
        Assert.Equal(new[] { "E", "E" }, grid[2]);   // colspan repeated
    }

    [Fact]
    public void FromHtml_HeaderCells_AreFlagged()
    {
        var t = TableStructure.FromHtml("<table><thead><tr><th>Name</th><th>Qty</th></tr></thead><tbody><tr><td>x</td><td>1</td></tr></tbody></table>")!;
        Assert.True(t.Cells[0].IsHeader);
        Assert.True(t.Cells[1].IsHeader);
        Assert.False(t.Cells[2].IsHeader);
        Assert.Equal("Name", t.Cells[0].Text);
    }

    [Fact]
    public void FromHtml_IsForgiving_MixedCaseUnclosedTagsEntitiesBreaks()
    {
        var html = "<HTML><body><TABLE><TR><TD>a &amp; b<br>c</TD><td>&nbsp;x&#33;<TR><td>&lt;y&gt;</table><table><tr><td>second</td></tr></table>";
        var t = TableStructure.FromHtml(html)!;

        Assert.Equal(2, t.RowCount);
        Assert.Equal(2, t.ColumnCount);
        Assert.Equal("a & b c", t.Cells[0].Text);   // entity decoded, <br> -> space
        Assert.Equal("x!", t.Cells[1].Text);        // &nbsp; treated as whitespace, numeric entity decoded
        Assert.Equal("<y>", t.Cells[2].Text);
        Assert.Equal(3, t.Cells.Count);             // only the first <table>
        Assert.Equal(html, t.Html);                 // markup kept verbatim
    }

    [Fact]
    public void FromHtml_KeepsTextOfUnknownTags_AndQuotedAngleBrackets()
    {
        var t = TableStructure.FromHtml("<table><tr><td><b>bold</b> <span title=\"a > b\">t</span></td></tr></table>")!;
        Assert.Equal("bold t", t.Cells[0].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<p>no table here</p>")]
    [InlineData("<table></table>")]
    [InlineData("<table><tr></tr></table>")]
    public void FromHtml_NothingRecoverable_ReturnsNull(string? html)
        => Assert.Null(TableStructure.FromHtml(html));

    [Fact]
    public void FromHtml_HostileSpans_AreClamped()
    {
        var t = TableStructure.FromHtml("<table><tr><td colspan=\"1000000\" rowspan='99999'>x</td></tr></table>")!;
        Assert.Equal(1, t.RowCount);                 // rowspan clamped to the table
        Assert.Equal(1, t.Cells[0].RowSpan);
        Assert.Equal(512, t.ColumnCount);            // colspan clamped to MaxSpan
        Assert.Equal(512, t.Cells[0].ColumnSpan);
        Assert.Equal(512, t.ToGrid()[0].Count);
    }

    [Fact]
    public void FromHtml_UnquotedAndBadSpans_FallBackToOne()
    {
        var t = TableStructure.FromHtml("<table><tr><td colspan=2>a</td><td rowspan=abc>b</td><td colspan=\"0\">c</td></tr></table>")!;
        Assert.Equal(2, t.Cells[0].ColumnSpan);
        Assert.Equal(1, t.Cells[1].RowSpan);
        Assert.Equal(1, t.Cells[2].ColumnSpan);
        Assert.Equal(4, t.ColumnCount);
    }

    [Fact]
    public void FromHtml_UnterminatedTag_IsLiteralText()
    {
        var t = TableStructure.FromHtml("<table><tr><td>a <b")!;
        Assert.Equal("a <b", t.Cells[0].Text);
    }

    // ---- ToHtml / round-trip ----

    [Fact]
    public void ToHtml_RoundTripsThroughFromHtml_WithSpansAndHeaders()
    {
        var original = new TableStructure
        {
            RowCount = 2,
            ColumnCount = 3,
            Cells = new[]
            {
                Cell(0, 0, "H1 & <b>", header: true), Cell(0, 1, "H2", colSpan: 2, header: true),
                Cell(1, 0, "a", rowSpan: 1), Cell(1, 1, null), Cell(1, 2, "c"),
            },
        };

        var html = original.ToHtml();
        Assert.Equal(N("<table>\n<tr><th>H1 &amp; &lt;b&gt;</th><th colspan=\"2\">H2</th></tr>\n<tr><td>a</td><td></td><td>c</td></tr>\n</table>"), N(html));

        var back = TableStructure.FromHtml(html)!;
        Assert.Equal(original.RowCount, back.RowCount);
        Assert.Equal(original.ColumnCount, back.ColumnCount);
        // Null text comes back as "" (HTML cannot express the difference); everything else is equal.
        Assert.Equal(original.Cells.Select(c => c with { Text = c.Text ?? "" }), back.Cells);
    }

    [Fact]
    public void ToHtml_EmptyTable_IsBareTableElement()
        => Assert.Equal(N("<table>\n</table>"), N(TableStructure.Empty.ToHtml()));

    // ---- ToGrid robustness ----

    [Fact]
    public void ToGrid_PadsMissingCells_GrowsForOutOfRangeCells_FirstWinsOnOverlap()
    {
        var t = new TableStructure
        {
            RowCount = 1,
            ColumnCount = 1,
            Cells = new[] { Cell(0, 0, "a", colSpan: 2), Cell(0, 1, "b"), Cell(2, 0, "z"), Cell(-1, 0, "neg") },
        };
        var grid = t.ToGrid();
        Assert.Equal(3, grid.Count);                        // grown to cover row 2
        Assert.Equal(new[] { "a", "a" }, grid[0]);          // overlap: first wins
        Assert.Equal(new[] { "", "" }, grid[1]);            // padded
        Assert.Equal(new[] { "z", "" }, grid[2]);
    }

    [Fact]
    public void Empty_IsEmpty_AndRendersToNothing()
    {
        Assert.True(TableStructure.Empty.IsEmpty);
        Assert.Empty(TableStructure.Empty.ToGrid());
        Assert.Equal("", TableStructure.Empty.ToMarkdown());
        Assert.Equal("", TableStructure.Empty.ToCsv());
        Assert.True(new TableStructure { RowCount = 2, ColumnCount = 2, Cells = Array.Empty<TableCell>() }.IsEmpty);
    }

    // ---- ToMarkdown ----

    [Fact]
    public void ToMarkdown_NoSpans_IsPipeTable_WithEscapedPipesAndCollapsedNewlines()
    {
        var t = TableStructure.FromHtml("<table><tr><th>Name</th><th>Value</th></tr><tr><td>a|b</td><td>line1<br>line2</td></tr><tr><td></td><td>x</td></tr></table>")!;
        var md = N(t.ToMarkdown());
        Assert.Equal("| Name | Value |\n| --- | --- |\n| a\\|b | line1 line2 |\n|  | x |", md);
    }

    [Fact]
    public void ToMarkdown_WithSpans_FallsBackToHtml()
    {
        var t = TableStructure.FromHtml("<table><tr><td colspan=\"2\">wide</td></tr><tr><td>a</td><td>b</td></tr></table>")!;
        var md = t.ToMarkdown();
        Assert.StartsWith("<table>", md);
        Assert.Contains("<td colspan=\"2\">wide</td>", md);
        Assert.Equal(t.ToHtml(), md);
    }

    [Fact]
    public void ToMarkdown_SingleRow_IsHeaderPlusSeparator()
    {
        var t = TableStructure.FromHtml("<table><tr><td>only</td></tr></table>")!;
        Assert.Equal("| only |\n| --- |", N(t.ToMarkdown()));
    }

    // ---- ToCsv ----

    [Fact]
    public void ToCsv_QuotesWhenNeeded_AndExpandsSpans()
    {
        var t = new TableStructure
        {
            RowCount = 2,
            ColumnCount = 2,
            Cells = new[] { Cell(0, 0, "plain"), Cell(0, 1, "has,comma"), Cell(1, 0, "say \"hi\"", colSpan: 2) },
        };
        Assert.Equal("plain,\"has,comma\"\r\n\"say \"\"hi\"\"\",\"say \"\"hi\"\"\"\r\n", t.ToCsv());
        Assert.Equal("plain\thas,comma\r\n\"say \"\"hi\"\"\"\t\"say \"\"hi\"\"\"\r\n", t.ToCsv('\t'));
    }

    // ---- Offset ----

    [Fact]
    public void Offset_ShiftsCellBoxes_LeavesBoxlessCellsAndHtml()
    {
        var t = new TableStructure
        {
            RowCount = 1,
            ColumnCount = 2,
            Html = "<table><tr><td>a</td><td>b</td></tr></table>",
            Cells = new[] { Cell(0, 0, "a", box: new LayoutBox(1, 2, 3, 4)), Cell(0, 1, "b") },
        };

        var moved = t.Offset(10, 20);
        Assert.Equal(new LayoutBox(11, 22, 13, 24), moved.Cells[0].BoundingBox);
        Assert.Null(moved.Cells[1].BoundingBox);
        Assert.Equal(t.Html, moved.Html);
        Assert.Equal(new LayoutBox(1, 2, 3, 4), t.Cells[0].BoundingBox);   // original untouched
        Assert.Same(t, t.Offset(0, 0));
    }
}
