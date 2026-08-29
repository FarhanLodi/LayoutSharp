using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using LayoutSharp.Models;
using LayoutSharp.Recognition;
using LayoutSharp.Services;
using Microsoft.Extensions.Logging;
using EasyOcrSharp.Structure;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

// LayoutSharp + EasyOcrSharp: layout analysis with the text of every text-bearing region filled in,
// and — optionally — table structure and formula LaTeX for the table / formula regions.
//
// Usage:  dotnet run --project samples/LayoutSharp.EasyOcrSample -- <image-path> [--lang en,de] [--tables] [--formulas] [--json | --markdown]
//
// Both libraries download their models on first use (LayoutSharp: PP-DocLayout; EasyOcrSharp: CRAFT +
// the requested language recognizers; with --tables / --formulas also the PP-StructureV3 models).

if (args.Length == 0)
{
    Console.WriteLine("Usage: LayoutSharp.EasyOcrSample <image-path> [--lang en,de] [--tables] [--formulas] [--json | --markdown]");
    return 1;
}

string imagePath = args[0];
int langIdx = Array.IndexOf(args, "--lang");
string[] languages = langIdx >= 0 && langIdx + 1 < args.Length ? args[langIdx + 1].Split(',') : new[] { "en" };
bool asJson = args.Contains("--json");
bool asMarkdown = args.Contains("--markdown");
bool withTables = args.Contains("--tables");
bool withFormulas = args.Contains("--formulas");

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(asJson || asMarkdown ? LogLevel.Warning : LogLevel.Information));

// 1. The OCR engine (caller-owned; LayoutSharp never disposes it).
await using var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());

// 2. Adapt it to LayoutSharp's ITextRecognizer (and, on request, ITableRecognizer / IFormulaRecognizer).
var recognizer = new EasyOcrRecognizer(ocr, languages);
ITableRecognizer? tableRecognizer = withTables ? new EasyOcrTableRecognizer(ocr, languages) : null;
IFormulaRecognizer? formulaRecognizer = withFormulas ? new EasyOcrFormulaRecognizer(ocr) : null;

// 3. Layout analysis with recognition. RecognitionParallelism > 1 is fine: EasyOcrService is thread-safe.
await using var layout = new LayoutService(recognizer, loggerFactory.CreateLogger<LayoutService>(), tableRecognizer, formulaRecognizer);
var result = await layout.AnalyzeAsync(imagePath, new LayoutAnalysisOptions { RecognitionParallelism = 2 });

if (asJson) { Console.WriteLine(result.Document.ToJson()); return 0; }
if (asMarkdown) { Console.WriteLine(result.Document.ToMarkdown()); return 0; }

var page = result.Document.Pages[0];
Console.WriteLine();
Console.WriteLine($"{page.Blocks.Count} blocks in {result.Duration.TotalMilliseconds:F0} ms (text recognized: {result.TextRecognized}, tables: {result.TablesRecognized}, formulas: {result.FormulasRecognized})");
Console.WriteLine();
foreach (var block in page.Blocks)
{
    Console.WriteLine($"#{block.ReadingOrder,-2} {block.Type,-14} {block.Confidence,5:P0}  ({block.RawClassName})");
    if (block.Text is not null)
        Console.WriteLine($"      {block.Text.ReplaceLineEndings(" ")}");
    if (block.Table is { } table)
    {
        Console.WriteLine($"      table {table.RowCount}x{table.ColumnCount}, {table.Cells.Count} cells{(table.HasSpans ? " (merged cells)" : "")}");
        foreach (var row in table.ToGrid().Take(5))
            Console.WriteLine($"      | {string.Join(" | ", row)} |");
    }
    if (block.Latex is not null)
        Console.WriteLine($"      $$ {block.Latex} $$");
}
return 0;

/// <summary>Bridges EasyOcrSharp to LayoutSharp: OCR one cropped region, return its text.</summary>
sealed class EasyOcrRecognizer(IEasyOcrService ocr, IReadOnlyList<string> languages) : ITextRecognizer
{
    public async Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default)
    {
        var ocrResult = await ocr.ExtractTextFromImage(crop, languages, options: null, cancellationToken);
        return ocrResult.FullText;
    }
}

/// <summary>
/// Table structure via EasyOcrSharp's PP-StructureV3 pipeline (SLANet_plus + OCR of the cells).
/// EasyOcrSharp 2.3.2 has no per-crop table API, so this runs its whole-page
/// <c>AnalyzeDocumentAsync</c> on the (white-padded) table crop and takes the largest table it finds
/// — correct, but it re-runs the inner layout detector and text OCR for every table (~0.5–3 s per
/// region on CPU). A per-crop RecognizeTable/RecognizeFormula API in EasyOcrSharp would make this
/// bridge one line and 2–3x faster.
/// </summary>
sealed class EasyOcrTableRecognizer(IEasyOcrService ocr, IReadOnlyList<string> languages) : ITableRecognizer
{
    private static readonly DocumentAnalysisOptions TablesOnly = new()
    {
        RecognizeTables = true,
        RecognizeFormulas = false,
        RecognizeSeals = false,
        DocumentOrientation = false,
        DocumentUnwarp = false,
        TableModel = DocumentTableModel.SlanetPlus,   // SlaNeXt = 3 extra models, better on clearly wired/wireless tables
    };

    public async Task<TableStructure?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default)
    {
        using var padded = Pad(crop, 24);   // white margin so the inner layout detector sees a whole table region
        StructureResult result = await ocr.AnalyzeDocumentAsync(padded, TablesOnly with { Languages = languages }, cancellationToken);

        TableStructure? best = null;
        foreach (StructureBlock block in result.Blocks)
        {
            if (block.Type != StructureBlockType.Table) continue;
            var table = TableStructure.FromHtml(block.TableHtml);
            if (table is not null && (best is null || table.Cells.Count > best.Cells.Count))
                best = table;
        }
        return best;   // cell boxes are not reported by TableHtml, so nothing to offset
    }

    internal static Image<Rgb24> Pad(Image<Rgb24> crop, int margin)
        => crop.Clone(c => c.Pad(crop.Width + 2 * margin, crop.Height + 2 * margin, Color.White));
}

/// <summary>
/// Formula LaTeX via EasyOcrSharp (its LaTeX-OCR recognizer behind PP-StructureV3). Same
/// whole-page-on-a-crop caveat as <see cref="EasyOcrTableRecognizer"/>.
/// </summary>
sealed class EasyOcrFormulaRecognizer(IEasyOcrService ocr) : IFormulaRecognizer
{
    private static readonly DocumentAnalysisOptions FormulasOnly = new()
    {
        RecognizeTables = false,
        RecognizeFormulas = true,
        RecognizeSeals = false,
        DocumentOrientation = false,
        DocumentUnwarp = false,
    };

    public async Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default)
    {
        using var padded = EasyOcrTableRecognizer.Pad(crop, 24);
        StructureResult result = await ocr.AnalyzeDocumentAsync(padded, FormulasOnly, cancellationToken);

        string? best = null;
        foreach (StructureBlock block in result.Blocks)
        {
            if (block.Type != StructureBlockType.Formula || string.IsNullOrWhiteSpace(block.Latex)) continue;
            if (best is null || block.Latex.Length > best.Length) best = block.Latex;
        }
        return best;
    }
}

// With dependency injection instead:
//
//   services.AddEasyOcrSharp();                                  // IEasyOcrService
//   services.AddSingleton<ITextRecognizer>(sp =>
//       new EasyOcrRecognizer(sp.GetRequiredService<IEasyOcrService>(), new[] { "en" }));
//   services.AddSingleton<ITableRecognizer>(sp =>
//       new EasyOcrTableRecognizer(sp.GetRequiredService<IEasyOcrService>(), new[] { "en" }));
//   services.AddSingleton<IFormulaRecognizer>(sp =>
//       new EasyOcrFormulaRecognizer(sp.GetRequiredService<IEasyOcrService>()));
//   services.AddLayoutSharp();                                   // ILayoutService picks up all three recognizers
//
// The table and formula recognizers above call into EasyOcrSharp's PP-StructureV3 pipeline per crop,
// so LayoutSharp stays in charge of finding the regions and ordering them, and EasyOcrSharp does the
// recognition it is best at.
