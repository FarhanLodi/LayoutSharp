using LayoutSharp.Models;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>Markdown / plain-text / JSON export of blocks that carry a recognized table or formula.</summary>
public class TableFormulaDocumentTests
{
    private static LayoutBlock Block(LayoutBlockType type, int order, string? text = null, TableStructure? table = null, string? latex = null) => new()
    {
        Type = type,
        BoundingBox = new LayoutBox(10, order * 50, 500, order * 50 + 40),
        Confidence = 0.9f,
        ReadingOrder = order,
        RawClassId = 0,
        RawClassName = type.ToString().ToLowerInvariant(),
        Text = text,
        Table = table,
        Latex = latex,
    };

    private static LayoutDocument Doc(params LayoutBlock[] blocks) => new()
    {
        Pages = new[] { new LayoutPage { PageNumber = 1, Width = 600, Height = 800, Blocks = blocks } },
    };

    private static string N(string s) => s.ReplaceLineEndings("\n");

    private static readonly TableStructure Simple = TableStructure.FromHtml(
        "<table><tr><th>Name</th><th>Qty</th></tr><tr><td>Bolt|M6</td><td>12</td></tr></table>")!;

    private static readonly TableStructure Spanned = TableStructure.FromHtml(
        "<html><body><table><tr><td colspan=\"2\">Totals</td></tr><tr><td>a</td><td>b</td></tr></table></body></html>")!;

    [Fact]
    public void ToMarkdown_RectangularTable_IsPipeTable_WithFirstRowAsHeader()
    {
        var md = N(Doc(Block(LayoutBlockType.Table, 0, table: Simple)).ToMarkdown());
        Assert.Equal("| Name | Qty |\n| --- | --- |\n| Bolt\\|M6 | 12 |", md);
    }

    [Fact]
    public void ToMarkdown_SpannedTable_RendersCanonicalHtml_NotTheVerbatimWrapper()
    {
        var md = Doc(Block(LayoutBlockType.Table, 0, table: Spanned)).ToMarkdown();
        Assert.StartsWith("<table>", md);
        Assert.Contains("<td colspan=\"2\">Totals</td>", md);
        Assert.DoesNotContain("<html>", md);   // ToHtml() from cells, not the raw Html passthrough
    }

    [Fact]
    public void ToMarkdown_Formula_IsDisplayMathBlock()
    {
        var md = N(Doc(Block(LayoutBlockType.Formula, 0, latex: "E = mc^2")).ToMarkdown());
        Assert.Equal("$$\nE = mc^2\n$$", md);
    }

    [Fact]
    public void ToMarkdown_UnrecognizedTableAndFormula_KeepPlaceholders_OrText()
    {
        var md = N(Doc(
            Block(LayoutBlockType.Table, 0),
            Block(LayoutBlockType.Formula, 1),
            Block(LayoutBlockType.Table, 2, text: "ocr'd table text"),
            Block(LayoutBlockType.Table, 3, table: TableStructure.Empty)).ToMarkdown());
        Assert.Equal("*[Table]*\n\n*[Formula]*\n\nocr'd table text\n\n*[Table]*", md);
    }

    [Fact]
    public void ToMarkdown_TableWinsOverText_LatexWinsOverText()
    {
        var md = N(Doc(
            Block(LayoutBlockType.Table, 0, text: "ignored", table: Simple),
            Block(LayoutBlockType.Formula, 1, text: "ignored", latex: "x")).ToMarkdown());
        Assert.DoesNotContain("ignored", md);
        Assert.Contains("| Name | Qty |", md);
        Assert.Contains("$$\nx\n$$", md);
    }

    [Fact]
    public void ToPlainText_EmitsTabJoinedRows_AndLatex()
    {
        var text = N(Doc(
            Block(LayoutBlockType.Text, 0, text: "Intro"),
            Block(LayoutBlockType.Table, 1, table: Spanned),
            Block(LayoutBlockType.Formula, 2, latex: "  a+b  "),
            Block(LayoutBlockType.Table, 3),                    // nothing recognized -> skipped
            Block(LayoutBlockType.Text, 4, text: "Outro")).ToPlainText());
        Assert.Equal("Intro\nTotals\tTotals\na\tb\na+b\nOutro", text);
    }

    [Fact]
    public void ToJson_OmitsNullTableAndLatex_AndDoesNotSerializeComputedProperties()
    {
        var json = Doc(Block(LayoutBlockType.Text, 0, text: "t"), Block(LayoutBlockType.Table, 1, table: Simple)).ToJson();
        Assert.DoesNotContain("\"Table\": null", json);
        Assert.DoesNotContain("\"Latex\": null", json);
        Assert.Contains("\"RowCount\": 2", json);
        Assert.Contains("\"IsHeader\": true", json);
        Assert.DoesNotContain("\"HasSpans\"", json);   // [JsonIgnore]d computed property (IsEmpty likewise; LayoutBox has its own IsEmpty)
    }

    [Fact]
    public void FromJson_RoundTripsTableWithCellBoxesSpansHtml_AndLatex()
    {
        var table = new TableStructure
        {
            RowCount = 2,
            ColumnCount = 2,
            Html = "<table><tr><td colspan=\"2\">T</td></tr><tr><td>a</td><td>b</td></tr></table>",
            Cells = new TableCell[]
            {
                new() { Row = 0, Column = 0, ColumnSpan = 2, IsHeader = true, Text = "T", BoundingBox = new LayoutBox(1.5, 2, 30, 10) },
                new() { Row = 1, Column = 0, Text = "a" },
                new() { Row = 1, Column = 1, Text = null, BoundingBox = new LayoutBox(15, 10, 30, 20) },
            },
        };
        var original = Doc(Block(LayoutBlockType.Table, 0, table: table), Block(LayoutBlockType.Formula, 1, latex: "\\frac{a}{b}"));

        var json = original.ToJson();
        var back = LayoutDocument.FromJson(json);

        Assert.NotNull(back);
        Assert.Equal(json, back!.ToJson());
        var t = back.Pages[0].Blocks[0].Table!;
        Assert.Equal(table.RowCount, t.RowCount);
        Assert.Equal(table.ColumnCount, t.ColumnCount);
        Assert.Equal(table.Html, t.Html);
        Assert.Equal(table.Cells, t.Cells);   // TableCell is a value-equal record (LayoutBox? included)
        Assert.Equal("\\frac{a}{b}", back.Pages[0].Blocks[1].Latex);
        Assert.Equal(table.ToMarkdown(), t.ToMarkdown());
    }
}
