using LayoutSharp;
using LayoutSharp.Models;
using LayoutSharp.Services;
using Microsoft.Extensions.Logging;

// LayoutSharp demo: analyze a document image into a typed, reading-ordered block graph.
//
// Usage:  dotnet run --project test/LayoutSharp.Demo -- <image-path>
//                    [--threshold 0.5] [--json | --markdown] [--gpu] [--warmup-only]
//
// The model (Docling heron, Apache-2.0) is downloaded and checksum-verified on first use into
// %LocalAppData%/LayoutSharp/models (override with LAYOUTSHARP_CACHE). This demo runs layout-only;
// to fill in block text, pass an ITextRecognizer to LayoutService (see samples/ for an EasyOcrSharp bridge).

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Usage: LayoutSharp.Demo <image-path> [--threshold 0.5] [--json | --markdown] [--gpu] [--warmup-only]");
    return 1;
}

string imagePath = args[0];
bool asJson = args.Contains("--json");
bool asMarkdown = args.Contains("--markdown");
bool useGpu = args.Contains("--gpu");
bool warmupOnly = args.Contains("--warmup-only");
var model = LayoutModel.DoclingLayoutHeron;
float threshold = float.TryParse(ArgValue("--threshold"), System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0.5f;

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(asJson || asMarkdown ? LogLevel.Warning : LogLevel.Information));

await using var layout = new LayoutService(
    new LayoutServiceOptions { Model = model, UseGpu = useGpu },
    recognizer: null,
    logger: loggerFactory.CreateLogger<LayoutService>());

try
{
    if (warmupOnly)
    {
        await layout.WarmUpAsync();
        Console.WriteLine($"Model {model} is ready.");
        return 0;
    }

    var result = await layout.AnalyzeAsync(imagePath, new LayoutAnalysisOptions { ConfidenceThreshold = threshold });
    var page = result.Document.Pages[0];

    if (asJson)
    {
        Console.WriteLine(result.Document.ToJson());
        return 0;
    }
    if (asMarkdown)
    {
        Console.WriteLine(result.Document.ToMarkdown());
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"Analyzed {imagePath} ({page.Width}×{page.Height}) with {result.Model} on {(result.UsedGpu ? "GPU" : "CPU")}: " +
                      $"{page.Blocks.Count} blocks in {result.Duration.TotalMilliseconds:F0} ms");
    Console.WriteLine();

    foreach (var block in page.Blocks)
    {
        var box = block.BoundingBox;
        Console.WriteLine($"#{block.ReadingOrder,-2} {block.Type,-14} {block.Confidence,5:P0}  " +
                          $"[{box.MinX:F0},{box.MinY:F0} {box.Width:F0}×{box.Height:F0}]  ({block.RawClassName})");
        if (!string.IsNullOrWhiteSpace(block.Text))
        {
            var preview = block.Text.ReplaceLineEndings(" ");
            if (preview.Length > 100) preview = preview[..100] + "…";
            Console.WriteLine($"      \"{preview}\"");
        }
    }

    return 0;
}
catch (LayoutSharpException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"LayoutSharp error ({ex.GetType().Name}): {ex.Message}");
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"  → {ex.InnerException.Message}");
    return 2;
}

string? ArgValue(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
}
