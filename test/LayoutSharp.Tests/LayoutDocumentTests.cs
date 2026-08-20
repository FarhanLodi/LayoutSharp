using LayoutSharp.Models;
using Xunit;

namespace LayoutSharp.Tests;

public class LayoutDocumentTests
{
    private static LayoutBlock Block(LayoutBlockType type, int order, string? text, string raw = "x", double y = 0) => new()
    {
        Type = type,
        BoundingBox = new LayoutBox(10, y, 500, y + 40),
        Confidence = 0.9f,
        ReadingOrder = order,
        RawClassId = 0,
        RawClassName = raw,
        Text = text,
    };

    private static LayoutDocument SampleDoc() => new()
    {
        Pages = new[]
        {
            new LayoutPage
            {
                PageNumber = 1,
                Width = 600,
                Height = 800,
                Blocks = new[]
                {
                    Block(LayoutBlockType.PageHeader, 0, "Running head", "header"),
                    Block(LayoutBlockType.Title, 1, "Quarterly\nReport", "doc_title", 10),
                    Block(LayoutBlockType.Figure, 2, null, "image", 80),
                    Block(LayoutBlockType.Caption, 3, "Figure 1. Revenue", "figure_title", 300),
                    Block(LayoutBlockType.SectionHeader, 4, "Summary", "paragraph_title", 320),
                    Block(LayoutBlockType.Text, 5, "Revenue grew 12%.", "text", 340),
                    Block(LayoutBlockType.List, 6, "• first\n• second", "text", 400),
                    Block(LayoutBlockType.Table, 7, null, "table", 500),
                    Block(LayoutBlockType.Formula, 8, null, "formula", 600),
                    Block(LayoutBlockType.PageNumber, 9, "7", "number", 780),
                },
            },
        },
    };

    [Fact]
    public void ToPlainText_IncludesTextBlocksInOrder_SkipsBlocksWithoutText()
    {
        var text = SampleDoc().ToPlainText().ReplaceLineEndings("\n");
        Assert.Equal("Running head\nQuarterly\nReport\nFigure 1. Revenue\nSummary\nRevenue grew 12%.\n• first\n• second\n7", text);
    }

    [Fact]
    public void ToPlainText_SeparatesPagesWithBlankLine()
    {
        var page = SampleDoc().Pages[0];
        var doc = new LayoutDocument { Pages = new[] { page, page with { PageNumber = 2 } } };
        var text = doc.ToPlainText().ReplaceLineEndings("\n");
        Assert.Contains("\n7\n\nRunning head\n", text);
    }

    [Fact]
    public void ToMarkdown_RendersHeadingsListsPlaceholders_AndOmitsFurniture()
    {
        var md = SampleDoc().ToMarkdown().ReplaceLineEndings("\n");
        var expected = string.Join("\n\n",
            "# Quarterly Report",       // title: line breaks collapsed
            "*[Figure]*",
            "*Figure 1. Revenue*",
            "## Summary",
            "Revenue grew 12%.",
            "- first\n- second",        // bullets normalized
            "*[Table]*",
            "*[Formula]*");
        Assert.Equal(expected, md);
        Assert.DoesNotContain("Running head", md);
        Assert.DoesNotContain("\n7", md);
    }

    [Fact]
    public void ToMarkdown_SeparatesPagesWithRule()
    {
        var page = SampleDoc().Pages[0];
        var doc = new LayoutDocument { Pages = new[] { page, page with { PageNumber = 2 } } };
        var md = doc.ToMarkdown().ReplaceLineEndings("\n");
        Assert.Contains("\n\n---\n\n# Quarterly Report", md);
    }

    [Fact]
    public void ToMarkdown_EmptyDocument_IsEmptyString()
    {
        Assert.Equal("", new LayoutDocument { Pages = Array.Empty<LayoutPage>() }.ToMarkdown());
        Assert.Equal("", LayoutResult.Empty.Document.ToPlainText());
    }

    [Fact]
    public void ToJson_SerializesEnumsAsStrings_AndOmitsNullText()
    {
        var json = SampleDoc().ToJson();
        Assert.Contains("\"Type\": \"Title\"", json);
        Assert.Contains("\"RawClassName\": \"doc_title\"", json);
        Assert.Contains("\"PageNumber\": 1", json);
        // The figure block's Text is null and should be omitted (WhenWritingNull).
        Assert.DoesNotContain("\"Text\": null", json);
    }

    [Fact]
    public void FromJson_RoundTrips()
    {
        var original = SampleDoc();
        var json = original.ToJson();
        var back = LayoutDocument.FromJson(json);

        Assert.NotNull(back);
        Assert.Equal(json, back!.ToJson());
        Assert.Equal(original.Pages[0].Blocks.Count, back.Pages[0].Blocks.Count);
        Assert.Equal(original.Pages[0].Blocks[1], back.Pages[0].Blocks[1]); // record value equality
        Assert.Equal(original.Pages[0].Blocks[1].BoundingBox, back.Pages[0].Blocks[1].BoundingBox);
    }
}
