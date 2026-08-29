using LayoutSharp.Models;
using LayoutSharp.Recognition;
using LayoutSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace LayoutSharp.Tests;

/// <summary>
/// Table / formula recognizer routing on the real PP-DocLayout_plus-L detector: every detected table
/// block on the mosaic fixture is cropped at source resolution and handed to the table recognizer,
/// and the result (with page-space cell boxes) lands on the block. Excluded from CI like the other
/// model-backed tests.
/// </summary>
[Trait("Category", "Integration")]
public class TableFormulaIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public TableFormulaIntegrationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task TableRecognizer_ReceivesOneCropPerTableBlock_AndResultsLandOnTheBlocks()
    {
        var crops = new List<(int W, int H)>();
        var tableRecognizer = TableRecognizer.FromDelegate((crop, _) =>
        {
            lock (crops) crops.Add((crop.Width, crop.Height));
            return Task.FromResult<TableStructure?>(new TableStructure
            {
                RowCount = 1,
                ColumnCount = 1,
                Cells = new[] { new TableCell { Row = 0, Column = 0, Text = $"{crop.Width}x{crop.Height}", BoundingBox = new LayoutBox(0, 0, crop.Width, crop.Height) } },
            });
        });
        int formulaCalls = 0;
        var formulaRecognizer = FormulaRecognizer.FromDelegate((_, _) => { Interlocked.Increment(ref formulaCalls); return Task.FromResult<string?>("x"); });

        await using var svc = new LayoutService(
            new LayoutServiceOptions { Model = LayoutModel.DoclingLayoutHeron },
            recognizer: null, logger: null, tableRecognizer, formulaRecognizer);

        var path = IntegrationTests.AssetPath("Multiple_Images.png");
        var result = await svc.AnalyzeAsync(path, new LayoutAnalysisOptions { RecognitionParallelism = 2 });
        var page = result.Document.Pages[0];
        var blocks = page.Blocks;

        Assert.False(result.TextRecognized);
        Assert.True(result.TablesRecognized);
        Assert.True(result.FormulasRecognized);

        var tables = blocks.Where(b => b.Type == LayoutBlockType.Table).ToList();
        _out.WriteLine($"{blocks.Count} blocks, {tables.Count} tables, {formulaCalls} formula calls in {result.Duration.TotalMilliseconds:F0} ms");
        Assert.NotEmpty(tables);
        Assert.Equal(tables.Count, crops.Count);

        foreach (var b in tables)
        {
            var (x, y, w, h) = b.BoundingBox.ToPixelRect(page.Width, page.Height);
            Assert.NotNull(b.Table);
            Assert.Equal($"{w}x{h}", b.Table!.Cells[0].Text);                       // crop at source resolution
            Assert.Equal(new LayoutBox(x, y, x + w, y + h), b.Table.Cells[0].BoundingBox); // remapped to page space
            Assert.Null(b.Text);
            Assert.Null(b.Latex);
        }
        Assert.All(blocks.Where(b => b.Type != LayoutBlockType.Table), b => Assert.Null(b.Table));
        Assert.Equal(blocks.Count(b => b.Type == LayoutBlockType.Formula), formulaCalls);
        Assert.All(blocks.Where(b => b.Type == LayoutBlockType.Formula), b => Assert.Equal("x", b.Latex));

        // Exports carry the tables and JSON round-trips losslessly.
        var md = result.Document.ToMarkdown();
        Assert.Contains("| --- |", md);
        var json = result.Document.ToJson();
        var back = LayoutDocument.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(json, back!.ToJson());
    }
}
