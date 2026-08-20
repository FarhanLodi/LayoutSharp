<div align="center">

<img src="https://raw.githubusercontent.com/FarhanLodi/LayoutSharp/main/src/LayoutSharp/Assets/icon.png" alt="LayoutSharp" width="120" height="120">

# LayoutSharp

### Document layout analysis for .NET — Docling region detection and reading order, running natively on ONNX Runtime. **No Python. No OCR lock-in.**

[![NuGet](https://img.shields.io/nuget/v/LayoutSharp.svg?label=NuGet&color=004880&logo=nuget)](https://www.nuget.org/packages/LayoutSharp)
[![Downloads](https://img.shields.io/nuget/dt/LayoutSharp.svg?label=Downloads&color=success)](https://www.nuget.org/packages/LayoutSharp)
[![CI](https://github.com/FarhanLodi/LayoutSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/FarhanLodi/LayoutSharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/FarhanLodi/LayoutSharp/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg?logo=dotnet)](https://dotnet.microsoft.com/)
[![AOT ready](https://img.shields.io/badge/Native%20AOT-ready-6f42c1.svg)](#-production--operations)

**[Quick start](#-quick-start)** · **[Multi-page](#multi-page-documents)** · **[Text recognition](#-text-recognition-bring-your-own-ocr)** · **[Models](#-models)** · **[Output](#-output--integration)** · **[Production](#-production--operations)**

</div>

---

LayoutSharp turns a page image into a **typed, reading-ordered block graph**: it runs IBM Docling's
**`docling-layout-heron`** detector (RT-DETRv2, exported to ONNX, executed through
`Microsoft.ML.OnnxRuntime`) to find and classify every region on the page — title, section header,
text, list item, table, figure, caption, formula, footnote, header, footer, checkbox, form,
key-value region — then orders them the way a human reads. Multi-page documents, page orientation
and skew correction, and pluggable table / formula recognition are built in. Everything runs
locally in a small managed package: no Python, no PyTorch, and **nothing ever leaves the machine**.

```csharp
await using var layout = new LayoutService();
var result = await layout.AnalyzeAsync("page.png");
foreach (var block in result.Document.Pages[0].Blocks)
    Console.WriteLine($"#{block.ReadingOrder} {block.Type} {block.Confidence:P0} {block.BoundingBox}");
```

## ✨ Highlights

| | |
|---|---|
| 🧱 **16 region types** | Title, section header, text, list, table, figure, caption, formula, footnote, page header, page footer, page number, seal, checkbox, form, key-value region — normalized from the detector's 17 raw labels, with the raw label preserved on every block |
| 📖 **Reading order** | Recursive XY-cut with the widest-gap rule, tolerant of touching boxes; page headers first, footers and page numbers last — or the model's own order when a detector emits one |
| 📄 **Multi-page** | `AnalyzePagesAsync` for rasterized page sequences and `AnalyzeAllFramesAsync` for multi-page TIFF / animated GIF / WebP — one document, pages 1..N, optionally analyzed in parallel |
| 🧭 **Page correction** | Opt-in 0/90/180/270 orientation classification and small-angle deskew, both applied *before* detection, with `MapToSource` to map boxes back to your image |
| 🔌 **Bring your own OCR** | One-method `ITextRecognizer` fills in block text — plug in [EasyOcrSharp](https://github.com/FarhanLodi/EasyOcrSharp), Tesseract, a cloud API, or nothing at all |
| 🧮 **Tables & formulas** | Optional `ITableRecognizer` → `TableStructure` (grid / HTML / Markdown / CSV) and `IFormulaRecognizer` → LaTeX, filled in on the blocks LayoutSharp located |
| 🎚️ **One model, or yours** | `DoclingLayoutHeron` (Apache-2.0) out of the box, or point `CustomLayoutModel` at any PaddleDetection / DETR / YOLO-end-to-end ONNX export |
| 📦 **Tiny package** | Four small dependencies; models download on demand, SHA-256 verified fail-closed, and cache locally — nothing is bundled |
| 🔒 **Verified & private** | HTTPS-only downloads with retries, offline mode for air-gapped hosts, decompression-bomb and page-count guards on input |
| 🧩 **Flexible input** | File / `Stream` / `byte[]` / `ReadOnlyMemory<byte>` / `Image<Rgb24>` / page sequences |
| 📤 **Structured output** | `Document → Page → Block` records with **JSON** (source-generated, round-trips), **plain text** and **Markdown** export |
| ⚡ **Fast** | ~0.5 s per page on a laptop CPU (warm), optional CUDA, thread-safe singleton, concurrent page and region recognition |
| 🛠️ **Modern .NET** | .NET 10, `IsAotCompatible`, DI-ready (`AddLayoutSharp()`), typed exceptions, `ILogger` progress |
| ⚖️ **Permissive end-to-end** | MIT library, Apache-2.0 model — no AGPL YOLO weights |

## 🆚 Why LayoutSharp?

|  | **LayoutSharp** | EasyOcrSharp `AnalyzeDocumentAsync` | Python Docling / PaddleX / LayoutParser | Cloud Document AI |
|---|:---:|:---:|:---:|:---:|
| **Runtime** | Pure .NET + ONNX | Pure .NET + ONNX | Python + PyTorch/Paddle | Remote HTTP service |
| **Scope** | Layout, reading order, page correction; OCR / tables / formulas pluggable | Full PP-StructureV3: layout, OCR, **tables → HTML**, formulas, seals | Layout (+ OCR add-ons) | Everything, per page |
| **Footprint** | 🟢 4 small deps, no bundled models | 🟡 OCR + PDF + barcode stack | 🔴 pip + framework | n/a |
| **Choose your OCR** | 🟢 any `ITextRecognizer` | 🟡 built-in engines | 🟡 built-in | 🔴 fixed |
| **Privacy** | 🟢 100% offline | 🟢 100% offline | 🟢 offline | 🔴 data leaves the machine |
| **Model license** | 🟢 Apache-2.0 | 🟢 Apache-2.0 | 🟡 varies (YOLO variants AGPL) | n/a |
| **Native AOT / trimming** | 🟢 yes | 🟢 yes | n/a | n/a |
| **Cost** | 🟢 free (MIT) | 🟢 free (MIT) | 🟢 free | 🔴 per-page |

> **Rule of thumb:** need boxes, classes and reading order with the OCR engine of *your* choice (or none) — LayoutSharp.
> Need tables recovered as HTML, formulas as LaTeX and OCR in one call — EasyOcrSharp's `AnalyzeDocumentAsync`.
> They compose: LayoutSharp's [EasyOcrSharp bridge](#-text-recognition-bring-your-own-ocr) is ten lines, and the
> same bridge fills in [tables and formulas](#tables--formulas).

---

## 📥 Installation

```bash
dotnet add package LayoutSharp
```

The detector (~172 MB) is downloaded on first use, SHA-256 verified, and cached under
`%LocalAppData%/LayoutSharp/models` (override with `LAYOUTSHARP_CACHE`). The optional orientation
classifier adds 6.7 MB, and only when you enable it. For NVIDIA GPU acceleration add
`Microsoft.ML.OnnxRuntime.Gpu` to your application and set `UseGpu = true` (see
[GPU](#gpu--execution-providers)).

## 🚀 Quick start

```csharp
using LayoutSharp.Services;

await using var layout = new LayoutService();          // docling-layout-heron on CPU, layout-only

var result = await layout.AnalyzeAsync("page.png");
var page = result.Document.Pages[0];

Console.WriteLine($"{page.Blocks.Count} blocks in {result.Duration.TotalMilliseconds:F0} ms");
foreach (var block in page.Blocks)
    Console.WriteLine($"#{block.ReadingOrder,-2} {block.Type,-14} {block.Confidence,5:P0}  {block.BoundingBox}  ({block.RawClassName})");

Console.WriteLine(result.Document.ToJson());           // the whole structured document
```

On a 762×1000 scanned form (`dotnet run --project test/LayoutSharp.Demo -- test/assets/structure_sample.png`).
The reported duration is the **first** call in a fresh process — it includes loading the 172 MB model
and creating the ONNX session; subsequent pages take ~0.5 s (see [Performance](#performance)), and
`WarmUpAsync()` moves that cost off the first request:

```
Analyzed test/assets/structure_sample.png (762×1000) with DoclingLayoutHeron on CPU: 42 blocks in 1567 ms

#0  Figure          95 %  [23,34 67×65]  (picture)
#1  SectionHeader   84 %  [144,47 397×17]  (section_header)
#2  SectionHeader   68 %  [122,78 442×14]  (section_header)
#3  Text            74 %  [124,94 436×15]  (text)
#4  Figure          97 %  [589,33 75×67]  (picture)
#5  SectionHeader   70 %  [228,120 229×14]  (section_header)
#6  Form            96 %  [11,139 673×750]  (form)
#7  Text            82 %  [497,150 130×13]  (text)
#8  Text            83 %  [12,166 210×14]  (text)
…
#26 List            84 %  [33,503 327×14]  (list_item)
#27 List            78 %  [34,531 223×14]  (list_item)
…
#32 Checkbox        91 %  [411,645 69×16]  (checkbox_unselected)
#33 Checkbox        92 %  [306,645 40×17]  (checkbox_unselected)
#34 Checkbox        91 %  [363,646 33×16]  (checkbox_unselected)
…
#40 PageFooter      63 %  [263,877 152×24]  (page_footer)
#41 PageFooter      69 %  [634,774 20×113]  (page_footer)
```

## 📦 The result model

```csharp
public sealed record LayoutResult
{
    public LayoutDocument Document { get; }        // Pages → Blocks
    public TimeSpan Duration { get; }              // detection + recognition, total for the call
    public LayoutModel Model { get; }              // which detector ran
    public string ModelName { get; }               // its human-readable name
    public bool UsedGpu { get; }                   // what actually ran, not what was requested
    public bool TextRecognized { get; }            // a text recognizer was configured and enabled
    public bool TablesRecognized { get; }          // … a table recognizer …
    public bool FormulasRecognized { get; }        // … a formula recognizer …
    public ReadingOrderSource ReadingOrderUsed { get; }   // Model or XyCut
}

public sealed record LayoutPage
{
    int PageNumber; int Width; int Height; IReadOnlyList<LayoutBlock> Blocks;
    int Rotation; double SkewAngle;                // page correction that was applied …
    int SourceWidth; int SourceHeight; bool IsCorrected;
    LayoutBox MapToSource(LayoutBox box);          // … and how to undo it
}

public sealed record LayoutBlock
{
    public LayoutBlockType Type { get; }           // normalized category
    public LayoutBox BoundingBox { get; }          // MinX/MinY/MaxX/MaxY + Width/Height/Center/Area/IoU
    public float Confidence { get; }               // 0..1
    public int ReadingOrder { get; }               // 0-based position within the page
    public int RawClassId { get; }                 // detector's class index …
    public string RawClassName { get; }            // … and label, e.g. "section_header", "code", "form"
    public string? Text { get; }                   // recognized text, or null (figure/table, or no recognizer)
    public TableStructure? Table { get; }          // recovered table grid, when a table recognizer ran
    public string? Latex { get; }                  // recognized formula, when a formula recognizer ran
}
```

`LayoutDocument` offers `ToJson()`, `FromJson()`, `ToPlainText()` and `ToMarkdown()`.

<br>

# 🧭 Core

### Input sources

```csharp
await layout.AnalyzeAsync("page.png");                    // file
await layout.AnalyzeAsync(stream);                        // Stream (non-seekable streams are buffered)
await layout.AnalyzeAsync(bytes);                         // byte[] or ReadOnlyMemory<byte>
await layout.AnalyzeAsync(image);                         // SixLabors Image<Rgb24> — caller keeps ownership
```

All overloads run the same pipeline and produce identical results for the same pixels. Any format
ImageSharp decodes (PNG, JPEG, BMP, TIFF, WebP, GIF, …) is accepted; RGBA and grayscale are converted.
Multi-frame files are analyzed **first frame only** by these overloads — see below.

### Multi-page documents

One call, one `LayoutDocument`, pages numbered `1..N`:

```csharp
// Multi-page TIFF, animated GIF/WebP — every frame becomes a page
var result = await layout.AnalyzeAllFramesAsync("scan.tiff");
Console.WriteLine(result.Document.Pages.Count);      // 12
Console.WriteLine(result.Document.ToMarkdown());     // pages separated by ---

// Already-rasterized pages (PDF renderer, camera roll, database blobs…)
var result = await layout.AnalyzePagesAsync(pages);  // IEnumerable<Image<Rgb24>>, caller keeps ownership
```

`AnalyzeAllFramesAsync` has the same five overloads as `AnalyzeAsync` (path, `Stream`, `byte[]`,
`ReadOnlyMemory<byte>`, `Image<Rgb24>`).

| | |
|---|---|
| Page numbers | `LayoutPage.PageNumber` = 1..N in file / sequence order |
| Reading order | restarts at `0` on every page (`ReadingOrder` is per page) |
| `Duration` | total for the whole call; per-page timings are logged at `Debug` level |
| Exports | `ToJson()` / `FromJson()` keep every page; `ToPlainText()` separates pages with a blank line, `ToMarkdown()` with a `---` rule |
| Guard | more than `LayoutServiceOptions.MaxPages` (default 500) pages throws `TooManyPagesException` — multi-frame files are rejected from their header, before decoding |
| Guard | `MaxImagePixels` applies to every page |

**Pages in parallel.** `PageParallelism` analyzes several pages at once (the ONNX session is
thread-safe); results stay in page order either way:

```csharp
var result = await layout.AnalyzeAllFramesAsync("scan.tiff",
    new LayoutAnalysisOptions { PageParallelism = 4 });
```

On CPU one inference already uses several cores, so 2–4 is the useful range (and pair it with
`LayoutServiceOptions.IntraOpThreads` — see [Performance](#performance)); a GPU session or an
OCR-bound run benefits more. With a recognizer configured, `PageParallelism × RecognitionParallelism`
calls can be in flight, so the recognizer must be thread-safe.

**Streaming (the default).** With `PageParallelism = 1` pages are pulled from the sequence lazily and
strictly in order — the next page is requested only after the previous one is finished — so a
rasterizer can `yield return` one page at a time and dispose it as the iterator advances, keeping a
single page in memory no matter how long the document is. With `PageParallelism > 1` several pages
are pulled ahead, so keep them alive until the call completes.

> **Frames must share one size.** ImageSharp decodes all frames of a file at once and requires them
> to have identical dimensions; a multi-page TIFF with differently sized pages throws
> `NotSupportedException`. Rasterize such documents page by page and use `AnalyzePagesAsync`.

### PDFs

LayoutSharp deliberately ships no PDF dependency — a PDF rasterizer means PDFium (plus SkiaSharp for
some wrappers) native binaries per platform, which would dwarf the library. Bring any rasterizer and
stream its pages into `AnalyzePagesAsync`:

```csharp
// dotnet add package PDFtoImage    (MIT — PDFium + SkiaSharp)
using PDFtoImage;
using SkiaSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// One page at a time: each page is disposed as soon as the next is pulled
// (valid because PageParallelism defaults to 1 — see "Streaming" above).
static IEnumerable<Image<Rgb24>> RasterizePdf(string path, int dpi = 200)
{
    byte[] pdf = File.ReadAllBytes(path);
    foreach (var bitmap in Conversion.ToImages(pdf, options: new RenderOptions(Dpi: dpi)))
    {
        using (bitmap)
        using (var rgba = bitmap.Copy(SKColorType.Rgba8888))
        using (var rgbaImage = Image.LoadPixelData<Rgba32>(rgba.GetPixelSpan(), rgba.Width, rgba.Height))
        using (var page = rgbaImage.CloneAs<Rgb24>())
        {
            yield return page;
        }
    }
}

await using var layout = new LayoutService();
var result = await layout.AnalyzePagesAsync(RasterizePdf("report.pdf"));

Console.WriteLine($"{result.Document.Pages.Count} pages, " +
                  $"{result.Document.Pages.Sum(p => p.Blocks.Count)} blocks");
File.WriteAllText("report.md", result.Document.ToMarkdown());
```

200 dpi is a good default for layout analysis (a Letter page becomes ~1700×2200); 150 dpi is enough
for clean digital PDFs, 300 dpi helps on dense scans. PDFium is **not thread-safe** — keep
rasterization single-threaded (`PageParallelism > 1` only fans out the *analysis*, which pulls pages
one at a time, so the recipe above stays correct).

<details>
<summary>Docnet.Core instead of PDFtoImage</summary>

```csharp
// dotnet add package Docnet.Core   (MIT — PDFium, no SkiaSharp; raw BGRA, no re-encode)
using Docnet.Core;
using Docnet.Core.Models;

static IEnumerable<Image<Rgb24>> RasterizePdf(string path, int dpi = 200)
{
    // DocLib.Instance is a process-wide singleton — do not dispose it per call.
    using var doc = DocLib.Instance.GetDocReader(File.ReadAllBytes(path), new PageDimensions(dpi / 72d));
    for (int i = 0; i < doc.GetPageCount(); i++)
    {
        using var reader = doc.GetPageReader(i);
        using var bgra = Image.LoadPixelData<Bgra32>(reader.GetImage(), reader.GetPageWidth(), reader.GetPageHeight());
        using var page = bgra.CloneAs<Rgb24>();
        yield return page;
    }
}
```

</details>

### Analysis options

```csharp
var result = await layout.AnalyzeAsync("page.png", new LayoutAnalysisOptions
{
    ConfidenceThreshold = 0.5f,          // drop detections below this score
    RecognizeText = true,                // run the ITextRecognizer, if one was supplied
    RecognizeTables = true,              // … the ITableRecognizer …
    RecognizeFormulas = true,            // … the IFormulaRecognizer …
    RecognitionParallelism = 1,          // > 1 fans regions out to a thread-safe recognizer
    PageParallelism = 1,                 // > 1 analyzes several pages at once (multi-page calls)
    DuplicateIouThreshold = 0.6f,        // a DETR head has no NMS: keep the better of two overlaps
    SuppressNestedDuplicates = true,     // drop a box ≥ 90% inside a better one of the same type
    MinFigureAreaFraction = 0.001,       // drop figure specks (bullets, icons); 0 keeps every figure
    ReadingOrderSource = ReadingOrderSource.Auto,  // model order when available, else XY-cut
    ReadingOrderOverlapTolerance = 0.005,// boxes overlapping ≤ 0.5% of the page still count as separated
    PinPageFurniture = true,             // headers first, footers/page numbers last
    Deskew = false,                      // straighten small tilts before detection
    DeskewMaxAngle = 15,                 // deskew search window, ±degrees
});
```

The shipped detector scores its confident regions 0.85–0.98, so the 0.5 default keeps the structure
while dropping speculative queries. Lower it to `0.3` for faint scans (more regions, more noise);
raise it to `0.7` when you only want the confident structure.

### Page correction (orientation & deskew)

Scans arrive sideways and crooked. Both corrections are **opt-in and off by default**, and both run
*before* detection, so region boxes, reading order and OCR crops all come from the corrected page.

```csharp
await using var layout = new LayoutService(new LayoutServiceOptions
{
    CorrectOrientation = true,              // 0/90/180/270 via PP-LCNet doc-ori (+6.7 MB, ~5 ms/page)
    OrientationConfidenceThreshold = 0.6f,  // below this the page is left alone
});

var result = await layout.AnalyzeAsync("scan.png", new LayoutAnalysisOptions
{
    Deskew = true,          // straighten small tilts (pure ImageSharp, ~20–100 ms/page)
    DeskewMaxAngle = 15,    // search window, ±degrees
});

var page = result.Document.Pages[0];
Console.WriteLine($"{page.Rotation}° turn, {page.SkewAngle:F1}° skew corrected");
```

**Orientation** classifies the page with PaddleClas' `PP-LCNet_x1_0_doc_ori` (Apache-2.0, 6.7 MB)
and rotates it upright when the winning class beats the threshold. The model is downloaded and
SHA-256 verified into the same cache as the detector on first use, honours `LAYOUTSHARP_CACHE`,
`LAYOUTSHARP_MODEL_BASE_URL`, `LAYOUTSHARP_OFFLINE` and `LayoutServiceOptions.Offline`, and is
pre-seeded by `WarmUpAsync()` along with the detector. Nothing is downloaded unless
`CorrectOrientation` is set or `ClassifyOrientationAsync` is called. You can also run the classifier
on its own, without enabling correction:

```csharp
var (rotation, confidence) = await layout.ClassifyOrientationAsync(image);   // on LayoutService
// rotation ∈ {0, 90, 180, 270} = how many degrees CLOCKWISE the content appears to be turned
```

**Deskew** estimates the tilt with a projection-profile search (downscale → Otsu → maximize the
sharpness of the horizontal ink profile over ±`DeskewMaxAngle`, 0.5° then 0.1° steps) and rotates the
page straight only when the estimate is reliable — |angle| ≥ 0.5° with a clear sharpness gain — so
already-straight pages are never touched. It is ImageSharp-only: no native dependency, no model.
The same routine is public for callers who want to reproduce the corrected image:

```csharp
using LayoutSharp.Preprocessing;

var estimate = PageDeskew.Estimate(image);          // Angle, Confidence, IsReliable
using var straight = PageDeskew.Rotate(image, -estimate.Angle);
```

Deskew runs *after* orientation, so a page that is both sideways and crooked needs both flags. It
cannot distinguish 0° from 180°, which is exactly why the orientation stage exists.

#### Coordinate frame

> **When a correction was applied, `page.Width`/`page.Height` and every `BoundingBox` are in the
> corrected frame — the image the detector actually saw — not in the frame of the image you passed in.**

Deskew *expands* the canvas (a 762×1000 page at 5° becomes 934×1131, corners filled white) and a
quarter turn swaps the axes, so overlaying raw boxes on the original image would be wrong.
`LayoutPage` reports the transform and inverts it for you:

| Member | Meaning |
|---|---|
| `Rotation` | 0/90/180/270 — how many degrees clockwise the **input** was turned; the page was rotated back by `(360 - Rotation) % 360` |
| `SkewAngle` | measured tilt of the input in degrees, positive = clockwise; the page was rotated by `-SkewAngle` |
| `SourceWidth` / `SourceHeight` | pixel size of the image you passed in |
| `IsCorrected` | `Rotation != 0 \|\| SkewAngle != 0` |
| `MapToSource(x, y)` / `MapToSource(box)` | corrected frame → your image |

```csharp
foreach (var block in page.Blocks)
{
    var onScreen = page.MapToSource(block.BoundingBox);   // draw this on the original
}
```

`MapToSource` is exact for 0/90/180/270. For a deskewed page it returns the axis-aligned rectangle
enclosing the four mapped corners, which is slightly larger than the visual region — an unavoidable
consequence of `LayoutBox` being axis-aligned in a rotated frame. When nothing was corrected it is
the identity, so it is always safe to call.

### Block types

| `LayoutBlockType` | Raw detector labels | Text-bearing | Notes |
|---|---|:---:|---|
| `Title` | `title` | ✅ | Document title |
| `SectionHeader` | `section_header` | ✅ | Section / sub-section heading |
| `Text` | `text`, `document_index`, `code` | ✅ | Body text (raw label tells them apart) |
| `List` | `list_item` | ✅ | One list item per block |
| `Table` | `table` | — | Located; parsed by an `ITableRecognizer` if you supply one |
| `Figure` | `picture` | — | Kept as a region |
| `Caption` | `caption` | ✅ | Figure / table caption |
| `Formula` | `formula` | — | Located; LaTeX by an `IFormulaRecognizer` if you supply one |
| `Footnote` | `footnote` | ✅ | |
| `PageHeader` | `page_header` | ✅ | Pinned first in reading order |
| `PageFooter` | `page_footer` | ✅ | Pinned last |
| `PageNumber` | — | ✅ | Reserved: heron folds page numbers into header/footer |
| `Seal` | — | — | Reserved: not emitted by the shipped detector |
| `Checkbox` | `checkbox_selected`, `checkbox_unselected` | — | The raw label carries the state |
| `Form` | `form` | — | Container; its fields are reported as their own blocks |
| `KeyValueRegion` | `key_value_region` | ✅ | Label : value pairs on invoices and forms |
| `Other` | anything unmapped | — | Not produced by the shipped model |

`type.IsTextBearing()` says whether a block is sent to the recognizer; `type.IsPageFurniture()`
identifies running heads, footers and page numbers. The raw label is always on
`block.RawClassName`, so `checkbox_selected` vs `checkbox_unselected`, or `code` vs plain `text`,
stays available even though both normalize to one type. Custom models map their own labels through
the same taxonomy (plus the common PP-DocLayout and YOLO vocabularies), so `PageNumber` and `Seal`
are produced when a detector emits them.

### Reading order

Blocks come back sorted by `ReadingOrder`. By default (`ReadingOrderSource.Auto`) the order is
computed with **recursive XY-cut**: the page is split along whitespace corridors — the wider gap wins
when both a vertical (column) and a horizontal (row) cut are possible — until every leaf is one
region; columns are read fully before moving right. A small tolerance lets boxes that touch or
overlap by a few pixels still count as separated (detectors routinely emit those), and page furniture
is pinned (headers first, footers/page numbers last).

If a custom detector emits its own reading order (a PaddleDetection export with seven-column
"ordered" rows, such as PP-DocLayoutV3), `Auto` uses it instead; force either strategy with
`ReadingOrderSource.Model` / `ReadingOrderSource.XyCut`, and check
`LayoutResult.ReadingOrderUsed` for what actually ran.

XY-cut handles single-column pages, multi-column bodies, full-width headers over columns, and forms.
It assumes an **upright, axis-aligned page** — enable [page correction](#page-correction-orientation--deskew)
for photographed or rotated scans, or columns whose boxes overlap will be read row by row.

<br>

# 🔌 Text recognition (bring your own OCR)

LayoutSharp ships no OCR engine. Implement one method and hand it to the service; every text-bearing
block is cropped at source resolution, recognized, and its `Text` filled in.

```csharp
public interface ITextRecognizer
{
    Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken cancellationToken = default);
}
```

**EasyOcrSharp** (the complete bridge — also in [`samples/LayoutSharp.EasyOcrSample`](samples/LayoutSharp.EasyOcrSample)):

```csharp
using EasyOcrSharp.Services;
using LayoutSharp.Recognition;

sealed class EasyOcrRecognizer(IEasyOcrService ocr, IReadOnlyList<string> languages) : ITextRecognizer
{
    public async Task<string?> RecognizeAsync(Image<Rgb24> crop, CancellationToken ct = default)
        => (await ocr.ExtractTextFromImage(crop, languages, options: null, ct)).FullText;
}

await using var ocr = new EasyOcrService();
await using var layout = new LayoutService(new EasyOcrRecognizer(ocr, ["en"]));

var result = await layout.AnalyzeAsync("page.png",
    new LayoutAnalysisOptions { RecognitionParallelism = 2 });   // EasyOcrService is thread-safe

Console.WriteLine(result.Document.ToMarkdown());
```

**Any other engine** — a delegate is enough:

```csharp
var recognizer = TextRecognizer.FromDelegate(async (crop, ct) =>
{
    using var ms = new MemoryStream();
    await crop.SaveAsPngAsync(ms, ct);
    return await myVisionClient.ReadTextAsync(ms.ToArray(), ct);   // Tesseract, Azure, Google, …
});
await using var layout = new LayoutService(recognizer);
```

Notes: the crop is owned by LayoutSharp and disposed after the call — don't keep it. Return `null` or
`""` for nothing legible. Set `RecognizeText = false` per call to skip recognition without dropping
the recognizer. Only text-bearing types are recognized; figures, tables, formulas, checkboxes, form
containers and seals keep `Text = null`.

### Tables & formulas

Two more optional plugs, filled in on the blocks LayoutSharp already located:

```csharp
public interface ITableRecognizer   { Task<TableStructure?> RecognizeAsync(Image<Rgb24> crop, CancellationToken ct = default); }
public interface IFormulaRecognizer { Task<string?>         RecognizeAsync(Image<Rgb24> crop, CancellationToken ct = default); }
```

```csharp
// Any engine that returns table HTML — TableStructure.FromHtml parses colspan/rowspan for you
var tables = TableRecognizer.FromHtml((crop, ct) => myTableEngine.ToHtmlAsync(crop, ct));
var formulas = FormulaRecognizer.FromDelegate((crop, ct) => myFormulaEngine.ToLatexAsync(crop, ct));

await using var layout = new LayoutService(
    recognizer: myTextRecognizer, logger: null,
    tableRecognizer: tables, formulaRecognizer: formulas);

var result = await layout.AnalyzeAsync("page.png");

foreach (var block in result.Document.Pages[0].Blocks)
{
    if (block.Table is { } table)
    {
        Console.WriteLine(table.ToMarkdown());          // also ToHtml(), ToCsv(), ToGrid()
        Console.WriteLine($"{table.RowCount}×{table.ColumnCount}");
    }
    if (block.Latex is { } latex) Console.WriteLine($"$${latex}$$");
}
```

`TableStructure` keeps merged cells as single cells with `RowSpan` / `ColumnSpan`, preserves the
recognizer's original `Html`, and exposes `ToGrid()` (expanded strings), `ToHtml()`, `ToMarkdown()`,
`ToCsv()` and `Offset(dx, dy)`. Cell boxes returned in crop coordinates are shifted into page space
for you. Recognized tables and formulas are rendered into `ToMarkdown()` — pipe tables (HTML when
cells are merged) and `$$…$$` blocks — instead of `*[Table]*` / `*[Formula]*` placeholders.

Set `RecognizeTables = false` / `RecognizeFormulas = false` per call to skip them; table, formula and
text calls share `RecognitionParallelism`. The EasyOcrSharp sample wires all three:
`dotnet run --project samples/LayoutSharp.EasyOcrSample -- page.png --lang en --tables --formulas --markdown`.

<br>

# 🎚️ Models

| `LayoutModel` | Backbone | Input | Classes | Download | License |
|---|---|:---:|:---:|:---:|:---:|
| `DoclingLayoutHeron` **(default)** | RT-DETRv2 / ResNet-50 | 640×640 | 17 | ~172 MB | Apache-2.0 |
| `Custom` | yours | yours | yours | — (local file) | yours |

```csharp
await using var layout = new LayoutService();   // DoclingLayoutHeron — nothing to configure
```

The shipped detector is IBM Docling's [`docling-layout-heron`](https://huggingface.co/docling-project/docling-layout-heron)
(**Apache-2.0**), an RT-DETRv2 with a ResNet-50 backbone trained on ~150 k pages (DocLayNet,
DocLayNet-v2, WordScape). It emits the 11 DocLayNet classes plus Docling's form extensions
(document index, code, checkbox selected/unselected, form, key-value region) — the strongest
DocLayNet-taxonomy detector available under a permissive license. It is exported to ONNX with
`training/export_onnx.py` in this repository (opset 17, input `pixel_values [1,3,640,640]`, outputs
`logits` + `pred_boxes`) and hosted on Hugging Face; the download is SHA-256 verified fail-closed.

LayoutSharp is MIT and deliberately uses only permissively-licensed models — the higher-scoring
Ultralytics-YOLO DocLayNet detectors are AGPL/GPL and are not an option for an MIT package.

### Bring your own model

Point `CustomLayoutModel` at any local ONNX detector — a fine-tune of the shipped model, a PaddleX
export, a YOLO trained on your own pages. It is loaded straight from disk: never downloaded, never
cached, and `Offline` / `ModelCachePath` do not apply.

```csharp
using LayoutSharp.Models;

await using var layout = new LayoutService(new LayoutServiceOptions().UseCustomModel(new CustomLayoutModel
{
    Path = "models/my-detector.onnx",
    InputSize = 800,                                          // square side the graph was exported for
    Labels = ["text", "title", "figure", "table"],            // in the model's own index order
    OutputContract = LayoutOutputContract.PaddleDetection,    // or Detr, or YoloEndToEnd
    Normalization = LayoutModelNormalization.None,            // or ImageNet
    Sha256 = null,                                            // optional: verified once at session creation
    TypeMap = new Dictionary<string, LayoutBlockType>         // optional: override label → type
    {
        ["figure"] = LayoutBlockType.Figure,
    },
    Name = "my-detector",                                     // reported on LayoutResult.ModelName
}));
```

There is a shorthand for the common case:

```csharp
var options = new LayoutServiceOptions()
    .UseCustomModel("models/my-detector.onnx", 640, labels, LayoutOutputContract.Detr);
```

| `LayoutOutputContract` | Graph | Preprocessing | Outputs |
|---|---|---|---|
| `PaddleDetection` | PaddleX / Paddle2ONNX export | stretch to S×S | rows `[class_id, score, x1, y1, x2, y2]` (optionally a 7th reading-order column) in input pixels, NMS in-graph |
| `Detr` | HF `transformers` RT-DETR / RT-DETRv2 / D-FINE | stretch to S×S | `logits [1,Q,C]` (pre-sigmoid) + `pred_boxes [1,Q,4]` normalized `cx,cy,w,h`, no NMS |
| `YoloEndToEnd` | Ultralytics export **with NMS in the graph** | letterbox onto gray-114 canvas | rows `[x1, y1, x2, y2, score, class_id]` in letterboxed pixels |

**Export recipes.** For a Hugging Face RT-DETR checkpoint or a fine-tune of the shipped model use
`python training/export_onnx.py --model <hub-id-or-dir> --output my-detector.onnx` (writes a JSON
sidecar with the labels, input size, preprocessing and SHA-256 — everything `CustomLayoutModel`
needs), and pick `OutputContract = Detr`. For PaddleX, export with Paddle2ONNX and read
`Preprocess → Resize → target_size` and `label_list` from `inference.yml`
(`OutputContract = PaddleDetection`; `Normalization = ImageNet` when the config lists mean/std).
For Ultralytics, export **with NMS baked in** — `model.export(format="onnx", nms=True)` — and use the
`imgsz` and `data.yaml names` you trained with; raw YOLO heads (`[1, 4+C, A]`) are not decoded.

> ⚖️ **Licensing is yours to check.** LayoutSharp is MIT and every built-in model is Apache-2.0, but a
> custom model carries its own terms. DocLayout-YOLO and YOLOv10-DocLayNet weights (and anything else
> derived from Ultralytics) are **AGPL-3.0** unless you hold a commercial license — loading them
> through `CustomLayoutModel` makes their licensing your responsibility, not the library's.

<br>

# 📤 Output & integration

### JSON, plain text, Markdown

```csharp
string json = result.Document.ToJson();                  // indented, enums as strings, nulls omitted
LayoutDocument? back = LayoutDocument.FromJson(json);    // round-trips

string text = result.Document.ToPlainText();             // recognized text, reading order, blank line between pages
string md   = result.Document.ToMarkdown();              // # Title, ## SectionHeader, *caption*, - list, tables, $$…$$
```

```jsonc
{
  "Pages": [{
    "PageNumber": 1, "Width": 762, "Height": 1000,
    "Blocks": [{
      "Type": "SectionHeader",
      "BoundingBox": { "MinX": 144, "MinY": 47, "MaxX": 541, "MaxY": 64, "Width": 397, "Height": 17, … },
      "Confidence": 0.84, "ReadingOrder": 1,
      "RawClassId": 7, "RawClassName": "section_header",
      "Text": "QUALITY IMPROVEMENT SUGGESTION"          // present only when a recognizer ran
    }, …]
  }]
}
```

Serialization is **source-generated** — no reflection, safe under trimming and Native AOT.

### Working with blocks

```csharp
var page = result.Document.Pages[0];

var body = page.Blocks.Where(b => !b.Type.IsPageFurniture());               // drop running heads/footers
var tables = page.Blocks.Where(b => b.Type == LayoutBlockType.Table);
foreach (var t in tables)
{
    var (x, y, w, h) = t.BoundingBox.ToPixelRect(page.Width, page.Height); // integer crop rect, clamped
    using var crop = image.Clone(c => c.Crop(new Rectangle(x, y, w, h)));   // hand to your table parser
}

var ticked = page.Blocks.Where(b => b.RawClassName == "checkbox_selected"); // finer than the normalized type
```

<br>

# 📊 Production & operations

### Dependency injection

```csharp
services.AddSingleton<ITextRecognizer, MyRecognizer>();   // optional — omit for layout-only
services.AddLayoutSharp(o =>
{
    o.Model = LayoutModel.DoclingLayoutHeron;
    o.ModelCachePath = "/var/cache/layoutsharp";
    o.UseGpu = false;
    o.MaxImagePixels = 50_000_000;
    o.MaxPages = 500;
});

services.AddLayoutSharpTableRecognizer<MyTableRecognizer>();      // optional
services.AddLayoutSharpFormulaRecognizer<MyFormulaRecognizer>();  // optional
```

`ILayoutService` is registered as a **singleton** (one ONNX session, thread-safe, reused) and picks up
any registered `ITextRecognizer`, `ITableRecognizer` and `IFormulaRecognizer` — in either
registration order. `AddLayoutSharp<TRecognizer>()` registers the text recognizer and the service in
one call. Warm it up at startup so the first request doesn't pay the cold-start cost:

```csharp
await app.Services.GetRequiredService<ILayoutService>().WarmUpAsync();
```

### GPU & execution providers

```csharp
new LayoutService(new LayoutServiceOptions { UseGpu = true });
```

Reference `Microsoft.ML.OnnxRuntime.Gpu` in your **application** (with the matching CUDA/cuDNN
runtime). LayoutSharp asks for the CUDA execution provider; if it cannot be loaded it logs a warning,
runs on CPU, and `LayoutResult.UsedGpu` tells you what actually happened.

### Hardening & resource limits

| Guard | Default | What it does |
|---|---|---|
| `MaxImagePixels` | 100 MP | Rejects oversized inputs **before decoding** (`ImageTooLargeException`) — decompression-bomb / pixel-flood protection |
| `MaxPages` | 500 | Bounds a multi-page call (`TooManyPagesException`); multi-frame files are rejected from their header, before pixels are decoded |
| SHA-256 verification | always | Every download is checked against a pinned digest and **discarded on mismatch** (`ModelChecksumException`) |
| HTTPS-only | always | Plain-HTTP mirrors are refused (loopback allowed for local mirrors) |
| Retries | 3, back-off | Transient failures retried; definitive 4xx fail fast (`ModelDownloadException`) |
| Atomic cache writes | always | `.part` file then rename — a killed download never leaves a half model |
| Cancellation | everywhere | Every call takes a `CancellationToken`, including downloads |

The service is safe for concurrent `AnalyzeAsync` calls; ONNX Runtime sessions are thread-safe and
recognition fans out only as far as `RecognitionParallelism` (and `PageParallelism`) allow.

### Resilient & offline model downloads

```csharp
// Build / bake step (connected):
await using (var l = new LayoutService(new LayoutServiceOptions { ModelCachePath = "/models" }))
    await l.WarmUpAsync();                          // downloads + verifies docling-layout-heron

// Production (air-gapped):
new LayoutService(new LayoutServiceOptions { ModelCachePath = "/models", Offline = true });
```

`WarmUpAsync()` also pre-seeds the orientation model when `CorrectOrientation` is enabled.

| Environment variable | Effect |
|---|---|
| `LAYOUTSHARP_CACHE` | Model cache directory (default `%LocalAppData%/LayoutSharp/models`) |
| `LAYOUTSHARP_MODEL_BASE_URL` | Download from a private HTTPS mirror hosting the same files |
| `LAYOUTSHARP_OFFLINE=1` | Never download; missing model → `OfflineModelMissingException` |

### Errors

Everything LayoutSharp raises derives from `LayoutSharpException`, so one catch covers the library:

| Exception | When |
|---|---|
| `ModelDownloadException` | Download failed after retries (`Url` property) |
| `ModelChecksumException` | Downloaded file (or a custom model with a pinned `Sha256`) failed verification |
| `OfflineModelMissingException` | Offline mode and the model is not cached (`ExpectedPath`) |
| `ImageTooLargeException` | Input exceeds `MaxImagePixels` |
| `TooManyPagesException` | A multi-page call exceeds `MaxPages` |
| `LayoutInferenceException` | ONNX session could not be created or inference failed |

Argument problems throw the usual `ArgumentException` family; a disposed service throws
`ObjectDisposedException`.

### Performance

Measured on an **AMD Ryzen 5 4600H** (12 logical cores, CPU only, ONNX Runtime 1.29), warm session,
762×1000 page:

| Stage | Cost | Notes |
|---|---|---|
| Analysis, warm | **~500–550 ms / page** | end-to-end `AnalyzeAsync` |
| ├─ inference | ~490–530 ms | ~96% of the total — the model is the workload |
| ├─ preprocessing | 8–16 ms | resize + tensor fill |
| └─ decode of detections | < 2 ms | |
| PNG decode | 19–89 ms | depends on page size |
| Session creation | ~0.9–1.3 s | once per service |
| First analysis after that | ~875 ms | cold kernels |

`WarmUpAsync()` moves both one-off costs off the first request. The detector's cost is fixed per page
(the image is stretched to 640×640), so a 12 MP scan costs about the same as a 1 MP one apart from
decode time. Recognition time is entirely your recognizer's; use `RecognitionParallelism` with a
thread-safe one.

Sessions are created with `GraphOptimizationLevel.ORT_ENABLE_ALL` and `ExecutionMode.ORT_SEQUENTIAL`,
which measured ~17% faster than ORT's defaults here. When you analyze several pages concurrently
(`PageParallelism > 1`, or several requests at once), cap the intra-op pool so the sessions stop
fighting over cores:

```csharp
new LayoutServiceOptions { IntraOpThreads = 4 }   // null (default) = all cores, best for one page at a time
```

> **What we tried: int8 quantization — rejected.** Dynamic int8 was ~2× *slower* than fp32
> (1002 ms vs 498 ms) because ORT's `ConvInteger` kernels are slow on this CPU, and static int8 (QDQ)
> merely matched fp32 (504 ms vs 522 ms) while destroying accuracy — 127 detections became 0 on a
> fixture. The model ships as fp32.

<br>

# 📚 Reference

### How model downloads work

1. `LayoutService` resolves the model spec (file name, URL, SHA-256, input size, labels) from its
   internal registry — or, for a custom model, from your `CustomLayoutModel`.
2. On the first `AnalyzeAsync` / `WarmUpAsync`, the cache directory is checked; a present file is used as-is.
3. Otherwise the file is fetched over HTTPS to `<name>.part`, hashed, compared to the pinned digest,
   and renamed into place. Progress is reported through `ILogger` every 2 s.
4. The ONNX session is created (CPU, or CUDA if requested and available) and reused for the life of
   the service.

Only one download runs at a time per process; concurrent callers wait for it. Custom models skip
steps 1–3 entirely and are loaded from their path.

### Limitations

- **PDFs need a rasterizer.** LayoutSharp analyzes images, not PDFs; render pages with PDFtoImage or
  Docnet.Core (both MIT) and hand them to `AnalyzePagesAsync` — see [PDFs](#pdfs). Multi-page TIFFs
  and animated GIF/WebP are handled directly by `AnalyzeAllFramesAsync`, as long as every frame has
  the same size.
- **Fixed detector resolution.** The page is stretched to 640×640, so extremely dense pages (a
  photographed newspaper spread, a full-page dense table) can lose small regions or merge neighbours.
  Crop and analyze the region of interest when a page is that busy.
- **Page correction is opt-in and heuristic.** Orientation is a 4-way classifier on a 224×224 centre
  crop: very elongated pages (receipts) and text-free pages can be misread, and a wrong 90°/180° turn
  is expensive downstream — hence the confidence gate. Deskew assumes horizontal text lines; pages
  dominated by diagrams, vertical CJK text or tables tilted differently from the text can produce a
  confidently wrong angle. Both are off by default.
- **Tables and formulas are located, not parsed — unless you plug something in.** Supply an
  `ITableRecognizer` / `IFormulaRecognizer` (EasyOcrSharp's `AnalyzeDocumentAsync` is bridged in the
  sample), or feed the crops to a parser of your choice.
- **Reading order assumes an upright, axis-aligned page.** Correct rotation and skew first, or
  columns whose boxes overlap will be read row by row.

### Building & testing

```bash
git clone https://github.com/FarhanLodi/LayoutSharp.git
cd LayoutSharp
dotnet build LayoutSharp.slnx -c Release

# Fast unit tests only (no models, no network):
dotnet test test/LayoutSharp.Tests -c Release --filter "Category!=Integration"

# Model-backed integration tests over every image in test/assets (downloads the model on first run):
dotnet test test/LayoutSharp.Tests -c Release --filter "Category=Integration"

# Console demo:
dotnet run --project test/LayoutSharp.Demo -- page.png [--threshold 0.5] [--json|--markdown] [--gpu] [--warmup-only]

# EasyOcrSharp bridge sample (layout + real OCR, tables and formulas):
dotnet run --project samples/LayoutSharp.EasyOcrSample -- page.png --lang en --tables --formulas --markdown
```

CI (GitHub Actions) builds and runs the unit tests on Linux, Windows and macOS for every push and PR,
runs the integration suite on Linux with a cached model directory, and validates the NuGet package and
a Native AOT publish. See [CHANGELOG.md](https://github.com/FarhanLodi/LayoutSharp/blob/main/CHANGELOG.md)
for release history.

<br>

## 🤝 Contributing

**Contributions are welcome!** Reading-order improvements, new detector variants, recognizer bridges,
documentation, and tests are all appreciated.

- 🐛 **Found a bug?** Open an [issue](https://github.com/FarhanLodi/LayoutSharp/issues) with a
  minimal repro (the page image plus the code and options you used).
- 💡 **Have an idea or feature request?** Open an issue to discuss it first, then send a PR.
- 🔧 **Sending a PR?** Branch from `main`, keep changes focused, and make sure `dotnet build -c Release`
  and the unit tests (`dotnet test --filter "Category!=Integration"`) pass.

If you're working on something larger, or want to collaborate on a feature, feel free to reach out
before starting so we can align on the approach.

## 💖 Support

If LayoutSharp saves you time, consider supporting development:

- 💳 **PayPal** — [paypal.me/FarhanLodi](https://paypal.me/FarhanLodi)
- 📱 **UPI (India)** — `farhanlodi5@oksbi`
- 🏦 **Bank transfer (USD)** — details below

<details>
<summary><b>USD bank transfer details (Wise)</b></summary>

<br>

USD account details for Farhan Lodi on Wise. Sending from a bank in the US? Use these details for a
domestic transfer. Sending from anywhere else? Make an international SWIFT transfer.

| Field | Value |
|---|---|
| Name | Farhan Lodi |
| Account type | Deposit |
| Routing number (wire and ACH) | `084009519` |
| Account number | `420927686563885` |
| SWIFT/BIC | `TRWIUS35XXX` |
| Bank address | Wise US Inc, 108 W 13th St, Wilmington, DE, 19801, United States |

Use the routing and account numbers when sending from the US, and the SWIFT/BIC when sending from
outside the US.

</details>

📧 Need more details, a different payment method, or have a question? Email
[farhanlodi31@gmail.com](mailto:farhanlodi31@gmail.com). All options are also listed in
[Donation.md](https://github.com/FarhanLodi/LayoutSharp/blob/main/Donation.md).

## 📬 Contact

For work inquiries, collaboration, feature requests, or any questions, reach out to:

**Farhan Lodi** — [farhanlodi31@gmail.com](mailto:farhanlodi31@gmail.com)

## 📄 License

MIT — see [LICENSE](https://github.com/FarhanLodi/LayoutSharp/blob/main/LICENSE). The
`docling-layout-heron` detector and the `PP-LCNet_x1_0_doc_ori` orientation classifier are
Apache-2.0 and retain their own licenses; a model you load through `CustomLayoutModel` carries
whatever license it came with.

## 🙏 Acknowledgments

- [IBM Docling](https://github.com/docling-project/docling) — `docling-layout-heron`, the shipped detector
- [DocLayNet](https://github.com/DS4SD/DocLayNet) — the taxonomy and training data behind it
- [PaddleOCR / PaddleX](https://github.com/PaddlePaddle/PaddleOCR) — the PP-LCNet orientation classifier and the PaddleDetection decoding contract
- [ONNX Runtime](https://onnxruntime.ai/) — neural network execution
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — image I/O, resizing and rotation
- [EasyOcrSharp](https://github.com/FarhanLodi/EasyOcrSharp) — the sample OCR bridge, and the sibling project this one was carved out of

<div align="center">
<br>

**[⬆ Back to top](#layoutsharp)**

<sub>Built with ❤️ for the .NET community · Layout analysis, zero Python, your choice of OCR</sub>

</div>
