using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Recognition;
using LayoutSharp.Services;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// Routing of table / formula regions to ITableRecognizer / IFormulaRecognizer inside the pipeline
/// (options, shared parallelism, cell-box remapping, null/empty handling, DI wiring), over a
/// scripted detector so no model is needed.
/// </summary>
public class TableFormulaServiceTests
{
    private static readonly LayoutModelSpec Spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

    private static RawDetection Det(string label, double x, double y, double w, double h, float score = 0.9f)
        => new(new LayoutBox(x, y, x + w, y + h), Spec.Classes.First(c => c.Name == label), score);

    private sealed class ScriptedDetector : ILayoutDetector
    {
        public List<RawDetection> Detections { get; } = new();
        public LayoutModel Model => LayoutModel.DoclingLayoutHeron;
        public bool IsGpu => false;
        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<RawDetection>> DetectAsync(Image<Rgb24> image, float scoreThreshold, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RawDetection>>(Detections.Where(d => d.Score >= scoreThreshold).ToList());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Counts calls and peak concurrency; shared by the three fakes below.</summary>
    private class Probe
    {
        public int Calls;
        public int MaxConcurrency;
        private int _inFlight;

        protected async Task<(int W, int H)> EnterAsync(Image<Rgb24> crop, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            do { seen = MaxConcurrency; } while (now > seen && Interlocked.CompareExchange(ref MaxConcurrency, now, seen) != seen);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref _inFlight);
            return (crop.Width, crop.Height);
        }
    }

    private sealed class SizeText : Probe, ITextRecognizer
    {
        public async Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken ct = default)
        { var (w, h) = await EnterAsync(crop, ct); return $"{w}x{h}"; }
    }

    /// <summary>Returns a 1x1 table whose cell text is the crop size and whose cell box is (0,0,10,10) in crop space.</summary>
    private sealed class SizeTable : Probe, ITableRecognizer
    {
        public async Task<TableStructure?> RecognizeAsync(Image<Rgb24> crop, CancellationToken ct = default)
        {
            var (w, h) = await EnterAsync(crop, ct);
            return new TableStructure
            {
                RowCount = 1,
                ColumnCount = 1,
                Cells = new[] { new TableCell { Row = 0, Column = 0, Text = $"{w}x{h}", BoundingBox = new LayoutBox(0, 0, 10, 10) } },
            };
        }
    }

    private sealed class SizeFormula : Probe, IFormulaRecognizer
    {
        public async Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken ct = default)
        { var (w, h) = await EnterAsync(crop, ct); return $"$${w}x{h}$$"; }   // delimiters must be stripped
    }

    private static LayoutService Create(ScriptedDetector det, ITextRecognizer? text = null, ITableRecognizer? table = null, IFormulaRecognizer? formula = null)
        => new(det, null, text, logger: null, tableRecognizer: table, formulaRecognizer: formula);

    private static ScriptedDetector MixedPage()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("title", 0, 0, 100, 20));           // text-bearing -> 100x20
        det.Detections.Add(Det("picture", 0, 30, 60, 60));               // figure -> nothing
        det.Detections.Add(Det("table", 20, 100, 80, 40));             // table -> 80x40, offset (20,100)
        det.Detections.Add(Det("formula", 0, 150, 50, 30));            // formula -> 50x30
        det.Detections.Add(Det("text", 0, 190, 100, 10));              // text-bearing -> 100x10
        return det;
    }

    [Fact]
    public async Task Analyze_RoutesTextTableFormula_ToTheirRecognizers()
    {
        var det = MixedPage();
        var text = new SizeText(); var table = new SizeTable(); var formula = new SizeFormula();
        await using var svc = Create(det, text, table, formula);
        using var img = new Image<Rgb24>(200, 200);

        var result = await svc.AnalyzeAsync(img);
        var blocks = result.Document.Pages[0].Blocks;

        Assert.True(result.TextRecognized);
        Assert.True(result.TablesRecognized);
        Assert.True(result.FormulasRecognized);
        Assert.Equal(2, text.Calls);
        Assert.Equal(1, table.Calls);
        Assert.Equal(1, formula.Calls);

        var title = blocks.Single(b => b.Type == LayoutBlockType.Title);
        Assert.Equal("100x20", title.Text);
        Assert.Null(title.Table);
        Assert.Null(title.Latex);

        var tbl = blocks.Single(b => b.Type == LayoutBlockType.Table);
        Assert.Null(tbl.Text);
        Assert.NotNull(tbl.Table);
        Assert.Equal("80x40", tbl.Table!.Cells[0].Text);
        // Cell box came back in crop space (0,0,10,10) and must now be in page space.
        Assert.Equal(new LayoutBox(20, 100, 30, 110), tbl.Table.Cells[0].BoundingBox);

        var f = blocks.Single(b => b.Type == LayoutBlockType.Formula);
        Assert.Null(f.Text);
        Assert.Null(f.Table);
        Assert.Equal("50x30", f.Latex);   // $$ stripped

        var fig = blocks.Single(b => b.Type == LayoutBlockType.Figure);
        Assert.Null(fig.Text); Assert.Null(fig.Table); Assert.Null(fig.Latex);
    }

    [Fact]
    public async Task Analyze_OptionsOff_SkipRecognizers_AndFlagsAreFalse()
    {
        var det = MixedPage();
        var text = new SizeText(); var table = new SizeTable(); var formula = new SizeFormula();
        await using var svc = Create(det, text, table, formula);
        using var img = new Image<Rgb24>(200, 200);

        var result = await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognizeTables = false, RecognizeFormulas = false });

        Assert.Equal(2, text.Calls);
        Assert.Equal(0, table.Calls);
        Assert.Equal(0, formula.Calls);
        Assert.True(result.TextRecognized);
        Assert.False(result.TablesRecognized);
        Assert.False(result.FormulasRecognized);
        Assert.All(result.Document.Pages[0].Blocks, b => { Assert.Null(b.Table); Assert.Null(b.Latex); });

        // Everything off -> no recognition pass at all.
        var none = await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognizeText = false, RecognizeTables = false, RecognizeFormulas = false });
        Assert.Equal(2, text.Calls);
        Assert.False(none.TextRecognized || none.TablesRecognized || none.FormulasRecognized);
    }

    [Fact]
    public async Task Analyze_WithoutTableOrFormulaRecognizer_LeavesBlocksUntouched()
    {
        var det = MixedPage();
        var text = new SizeText();
        await using var svc = Create(det, text);
        Assert.True(svc.HasRecognizer);
        Assert.False(svc.HasTableRecognizer);
        Assert.False(svc.HasFormulaRecognizer);
        using var img = new Image<Rgb24>(200, 200);

        var result = await svc.AnalyzeAsync(img);

        Assert.False(result.TablesRecognized);
        Assert.False(result.FormulasRecognized);
        Assert.All(result.Document.Pages[0].Blocks, b => { Assert.Null(b.Table); Assert.Null(b.Latex); });
    }

    [Fact]
    public async Task Analyze_TableOrFormulaOnly_WorksWithoutTextRecognizer()
    {
        var det = MixedPage();
        var table = new SizeTable();
        await using var svc = Create(det, table: table);
        Assert.False(svc.HasRecognizer);
        Assert.True(svc.HasTableRecognizer);
        using var img = new Image<Rgb24>(200, 200);

        var result = await svc.AnalyzeAsync(img);

        Assert.False(result.TextRecognized);
        Assert.True(result.TablesRecognized);
        Assert.Equal(1, table.Calls);
        Assert.NotNull(result.Document.Pages[0].Blocks.Single(b => b.Type == LayoutBlockType.Table).Table);
        Assert.All(result.Document.Pages[0].Blocks, b => Assert.Null(b.Text));
    }

    [Fact]
    public async Task Analyze_RecognitionParallelism_IsSharedAcrossKinds()
    {
        var det = new ScriptedDetector();
        for (int i = 0; i < 2; i++) det.Detections.Add(Det("text", 0, i * 30, 100 + i, 20));
        for (int i = 0; i < 2; i++) det.Detections.Add(Det("table", 0, 60 + i * 30, 100 + i, 20));
        for (int i = 0; i < 2; i++) det.Detections.Add(Det("formula", 0, 120 + i * 30, 100 + i, 20));
        var text = new SizeText(); var table = new SizeTable(); var formula = new SizeFormula();
        await using var svc = Create(det, text, table, formula);
        using var img = new Image<Rgb24>(200, 200);

        var page = (await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognitionParallelism = 3 })).Document.Pages[0];

        Assert.Equal(2, text.Calls); Assert.Equal(2, table.Calls); Assert.Equal(2, formula.Calls);
        // With 6 regions and parallelism 3 at least one recognizer sees overlap; the work list is shared.
        Assert.True(text.MaxConcurrency + table.MaxConcurrency + formula.MaxConcurrency > 3,
            $"expected concurrent recognition across kinds, saw {text.MaxConcurrency}/{table.MaxConcurrency}/{formula.MaxConcurrency}");
        // Each block still gets its own crop, in reading order.
        for (int i = 0; i < 6; i++)
        {
            var b = page.Blocks[i];
            var expected = $"{100 + (i % 2)}x20";
            var got = b.Text ?? b.Table?.Cells[0].Text ?? b.Latex;
            Assert.Equal(expected, got);
        }

        // Sequential: never more than one in flight anywhere.
        var t2 = new SizeText(); var tb2 = new SizeTable(); var f2 = new SizeFormula();
        await using var seq = Create(det, t2, tb2, f2);
        await seq.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognitionParallelism = 1 });
        Assert.Equal(1, t2.MaxConcurrency); Assert.Equal(1, tb2.MaxConcurrency); Assert.Equal(1, f2.MaxConcurrency);
    }

    [Fact]
    public async Task Analyze_NullOrEmptyResults_BecomeNull_AndTinyRegionsAreSkipped()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("table", 0, 0, 100, 40));
        det.Detections.Add(Det("formula", 0, 50, 100, 20));
        det.Detections.Add(Det("table", 0, 80, 1.2, 1.4));   // < 2 px -> not sent
        int tableCalls = 0, formulaCalls = 0;
        var table = TableRecognizer.FromDelegate((_, _) => { tableCalls++; return Task.FromResult<TableStructure?>(TableStructure.Empty); });
        var formula = FormulaRecognizer.FromDelegate((_, _) => { formulaCalls++; return Task.FromResult<string?>("  $$  $$ "); });
        await using var svc = Create(det, table: table, formula: formula);
        using var img = new Image<Rgb24>(200, 200);

        var blocks = (await svc.AnalyzeAsync(img)).Document.Pages[0].Blocks;

        Assert.Equal(1, tableCalls);
        Assert.Equal(1, formulaCalls);
        Assert.All(blocks, b => { Assert.Null(b.Table); Assert.Null(b.Latex); });

        // null from the recognizer is null on the block, too.
        var nullTable = TableRecognizer.FromDelegate((_, _) => Task.FromResult<TableStructure?>(null));
        var nullFormula = FormulaRecognizer.FromDelegate((_, _) => Task.FromResult<string?>(null));
        await using var svc2 = Create(det, table: nullTable, formula: nullFormula);
        Assert.All((await svc2.AnalyzeAsync(img)).Document.Pages[0].Blocks, b => { Assert.Null(b.Table); Assert.Null(b.Latex); });
    }

    [Fact]
    public async Task Analyze_RecognizerExceptions_Propagate_AndCancellationIsHonoured()
    {
        var det = new ScriptedDetector();
        det.Detections.Add(Det("table", 0, 0, 100, 40));
        det.Detections.Add(Det("formula", 0, 50, 100, 20));
        using var img = new Image<Rgb24>(200, 200);

        var boom = TableRecognizer.FromDelegate((_, _) => throw new InvalidOperationException("boom"));
        await using (var svc = Create(det, table: boom))
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AnalyzeAsync(img));

        // Same through the parallel path (two eligible regions, parallelism 2).
        var boomFormula = FormulaRecognizer.FromDelegate((_, _) => throw new InvalidOperationException("boom"));
        await using (var svc = Create(det, table: boom, formula: boomFormula))
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AnalyzeAsync(img, new LayoutAnalysisOptions { RecognitionParallelism = 2 }));

        var slow = FormulaRecognizer.FromDelegate(async (_, ct) => { await Task.Delay(5000, ct); return "x"; });
        using var cts = new CancellationTokenSource(50);
        await using (var svc = Create(det, formula: slow))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.AnalyzeAsync(img, null, cts.Token));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("E=mc^2", "E=mc^2")]
    [InlineData("  $E=mc^2$ ", "E=mc^2")]
    [InlineData("$$ E=mc^2 $$", "E=mc^2")]
    [InlineData("\\[a\\]", "a")]
    [InlineData("\\(a\\)", "a")]
    [InlineData("$", "$")]
    [InlineData("$$", null)]
    public void NormalizeLatex_TrimsAndStripsOneDelimiterPair(string? input, string? expected)
        => Assert.Equal(expected, LayoutService.NormalizeLatex(input));

    // ---- helpers ----

    [Fact]
    public async Task RecognizerHelpers_WrapDelegates_AndRejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => TableRecognizer.FromDelegate(null!));
        Assert.Throws<ArgumentNullException>(() => TableRecognizer.FromHtml(null!));
        Assert.Throws<ArgumentNullException>(() => FormulaRecognizer.FromDelegate(null!));

        using var crop = new Image<Rgb24>(4, 4);
        var fromHtml = TableRecognizer.FromHtml((_, _) => Task.FromResult<string?>("<table><tr><td>x</td><td>y</td></tr></table>"));
        var t = await fromHtml.RecognizeAsync(crop);
        Assert.NotNull(t);
        Assert.Equal(2, t!.ColumnCount);
        Assert.Equal("x", t.Cells[0].Text);
        Assert.Null(await TableRecognizer.FromHtml((_, _) => Task.FromResult<string?>(null)).RecognizeAsync(crop));

        var f = FormulaRecognizer.FromDelegate((c, _) => Task.FromResult<string?>($"{c.Width}"));
        Assert.Equal("4", await f.RecognizeAsync(crop));
    }

    // ---- DI ----

    [Fact]
    public void AddLayoutSharp_PicksUpTableAndFormulaRecognizers_InEitherOrder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITableRecognizer>(new SizeTable());
        services.AddLayoutSharp();
        services.AddLayoutSharpFormulaRecognizer<SizeFormula>();   // after AddLayoutSharp is fine: resolved lazily
        using var provider = services.BuildServiceProvider();

        var svc = (LayoutService)provider.GetRequiredService<ILayoutService>();
        Assert.False(svc.HasRecognizer);
        Assert.True(svc.HasTableRecognizer);
        Assert.True(svc.HasFormulaRecognizer);
        Assert.IsType<SizeFormula>(provider.GetRequiredService<IFormulaRecognizer>());
    }

    [Fact]
    public void AddLayoutSharpTableRecognizer_IsTryAdd_AndAloneYieldsNoOtherRecognizers()
    {
        var services = new ServiceCollection();
        services.AddLayoutSharpTableRecognizer<SizeTable>();
        services.AddLayoutSharpTableRecognizer<SizeTable>();       // idempotent
        services.AddLayoutSharp<SizeText>();
        using var provider = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(ITableRecognizer));
        var svc = (LayoutService)provider.GetRequiredService<ILayoutService>();
        Assert.True(svc.HasRecognizer);
        Assert.True(svc.HasTableRecognizer);
        Assert.False(svc.HasFormulaRecognizer);
    }

    [Fact]
    public void AddLayoutSharp_WithoutTableOrFormulaRecognizer_HasNone()
    {
        var services = new ServiceCollection();
        services.AddLayoutSharp();
        using var provider = services.BuildServiceProvider();
        var svc = (LayoutService)provider.GetRequiredService<ILayoutService>();
        Assert.False(svc.HasTableRecognizer);
        Assert.False(svc.HasFormulaRecognizer);
    }

    [Fact]
    public void PublicConstructors_AcceptTrailingRecognizers()
    {
        var table = new SizeTable(); var formula = new SizeFormula();
        using var a = new LayoutService(null, null, table, formula);
        Assert.False(a.HasRecognizer); Assert.True(a.HasTableRecognizer); Assert.True(a.HasFormulaRecognizer);
        using var b = new LayoutService(new LayoutServiceOptions { Model = LayoutModel.DoclingLayoutHeron }, formulaRecognizer: formula);
        Assert.False(b.HasTableRecognizer); Assert.True(b.HasFormulaRecognizer);
    }
}
