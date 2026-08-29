# Changelog

All notable changes to LayoutSharp are documented here.

## 1.0.0

First release.

### Layout analysis
- **Region detection** with two built-in detectors, both Apache-2.0, selected with
  `LayoutServiceOptions.Model`:
  - **`LayoutModel.PPDocLayoutV3`** (Baidu PaddleX) — **the default**. RT-DETR-L, 800×800 input,
    25 categories, ~124 MB. It is the only one of the two that detects **seals** (stamps and red
    chops on contracts, invoices and certificates), charts as distinct from images, vertical CJK
    text and page numbers, and it degrades far better on rotated and skewed pages. Its export also
    carries a per-region reading-order key, so `ReadingOrderSource.Model` works without falling back
    to XY-cut. Preprocessing reproduces the model's own `inference.yml` exactly —
    `Resize(target_size: [800, 800], keep_ratio: false, interp: 2)` (bicubic stretch) →
    `NormalizeImage(norm_type: none)` (plain `1/255`) → `Permute`. The graph's `[M,200,200]` mask
    head is not fetched.
  - **`LayoutModel.DoclingLayoutHeron`** (IBM Docling) — RT-DETRv2 with a ResNet-50 backbone,
    640×640 input, 17 categories, ~164 MB, trained on ~150 k pages (DocLayNet, DocLayNet-v2,
    WordScape). Exported to ONNX with `training/export_onnx.py` (opset 17; `pixel_values
    [1,3,640,640]` in, `logits` + `pred_boxes` out). It is ~1.5× faster per page and holds up better
    on heavy blur and low resolution, and it is the only one with the **list, checkbox, form and
    key-value** classes — so it remains the better choice for form documents.

  The two taxonomies are complementary, not nested: V3 alone emits `Seal` and `PageNumber`, heron
  alone emits `List`, `Checkbox`, `Form` and `KeyValueRegion`. README's *Block types* table compares
  them label by label.
- **Per-model preprocessing contract**: the input resampler follows the exporting framework rather
  than being fixed — bicubic (`cv2.INTER_CUBIC`) for PaddleDetection exports as their `inference.yml`
  declares `interp: 2`, bilinear for Hugging Face RT-DETR processors and Ultralytics letterboxing.
  Custom models inherit the right one from their `OutputContract`.
- **Model delivery**: downloaded on first use, SHA-256 verified fail-closed, retried with back-off
  over HTTPS only, written atomically and cached (`LAYOUTSHARP_CACHE`,
  `LAYOUTSHARP_MODEL_BASE_URL`, `LAYOUTSHARP_OFFLINE`). Only one download runs at a time per process.
- **Normalized taxonomy** `LayoutBlockType` (Title, SectionHeader, Text, List, Table, Figure,
  Caption, Formula, Footnote, PageHeader, PageFooter, PageNumber, Seal, Checkbox, Form,
  KeyValueRegion, Other) with `IsTextBearing()` / `IsPageFurniture()` helpers and the detector's raw
  label preserved on every block (`RawClassId` / `RawClassName`), so `checkbox_selected` vs
  `checkbox_unselected` or `code` vs `text` stays available.
- **Detection clean-up**: IoU de-duplication of the NMS-free DETR head (`DuplicateIouThreshold`),
  containment suppression of same-type nested boxes (`SuppressNestedDuplicates`), and a minimum
  figure area (`MinFigureAreaFraction`, default 0.1 % of the page) that drops bullet glyphs and icons
  reported as pictures.
- **Reading order** by recursive XY-cut with the widest-gap rule, a small overlap tolerance for
  touching boxes (`ReadingOrderOverlapTolerance`), and page furniture pinned (`PinPageFurniture`:
  headers first, footers / page numbers last). `LayoutAnalysisOptions.ReadingOrderSource`
  (`Auto` / `Model` / `XyCut`) selects between the geometric order and a detector's own order when it
  emits one; `LayoutResult.ReadingOrderUsed` reports which ran.

### Bring your own model
- **`LayoutModel.Custom` + `CustomLayoutModel`** — run any local ONNX detector: `Path`, `InputSize`,
  `Labels` (index order), `Normalization` (`None` / `ImageNet`), `OutputContract`, optional `Sha256`,
  `TypeMap` and `Name`. Configured with `LayoutServiceOptions.CustomModel` or
  `UseCustomModel(...)` (record or shorthand overload). The file is loaded straight from disk — never
  downloaded, never cached — validated up front (existence, input size multiple of 32, non-blank
  distinct labels, well-formed digest) and verified against `Sha256` once, at session creation.
- **Three graph contracts** (`LayoutOutputContract`): `PaddleDetection` (Paddle2ONNX rows
  `[class_id, score, x1, y1, x2, y2]`, plus the seventh reading-order column PP-DocLayoutV3 emits),
  `Detr` (HF `transformers` RT-DETR / RT-DETRv2 / D-FINE `logits` + `pred_boxes`), and
  `YoloEndToEnd` (Ultralytics export with in-graph NMS, letterboxed and un-letterboxed for you).
- Custom labels normalize through `TypeMap`, then the built-in PP-DocLayout / Docling / common YOLO
  vocabularies (case- and separator-insensitive), else `Other`.

### Multi-page documents
- **`ILayoutService.AnalyzePagesAsync(IEnumerable<Image<Rgb24>>, …)`** analyzes a sequence of
  already-rasterized pages (a PDF renderer's output, for example) into one `LayoutDocument` with
  `PageNumber` 1..N, and **`AnalyzeAllFramesAsync`** (five overloads: path, `Stream`, `byte[]`,
  `ReadOnlyMemory<byte>`, `Image<Rgb24>`) does the same for every frame of a multi-page TIFF or
  animated GIF/WebP. Reading order restarts at 0 on each page; `Duration` is the total. No new
  dependencies — PDF rasterization stays a README recipe (PDFtoImage / Docnet.Core, both MIT).
- `LayoutAnalysisOptions.PageParallelism` (default 1) analyzes pages concurrently on the thread-safe
  ONNX session while keeping results in page order. At the default the sequence is pulled lazily, one
  page at a time, so a rasterizer can stream pages and keep only one in memory.
- `LayoutServiceOptions.MaxPages` (default 500) bounds a multi-page call; exceeding it throws the new
  `TooManyPagesException`. Multi-frame files are rejected from their header before any pixels are
  decoded.
- `ToPlainText()` separates pages with a blank line, `ToMarkdown()` with a `---` rule.

### Page correction (opt-in, off by default)
- **`LayoutServiceOptions.CorrectOrientation`** — detects 0/90/180/270 page rotation with PaddleClas'
  `PP-LCNet_x1_0_doc_ori` (Apache-2.0, **+6.7 MB**, ~5 ms/page on CPU) and rotates the page upright
  before detection when the winning class scores at least `OrientationConfidenceThreshold`
  (default 0.6). The model is downloaded and SHA-256 verified into the existing cache, honours every
  `LAYOUTSHARP_*` variable and `Offline`, and is pre-seeded by `WarmUpAsync()`. Nothing is downloaded
  unless the feature is used.
- **`LayoutService.ClassifyOrientationAsync(image, ct)`** — runs the classifier on its own, returning
  `(int Rotation, float Confidence)`, independent of `CorrectOrientation`.
- **`LayoutAnalysisOptions.Deskew` / `DeskewMaxAngle`** (default 15°) — small-angle deskew before
  detection, gated so straight pages are untouched. Pure EasyImageSharp, no model, no new dependency,
  ~20–100 ms per page. Runs after orientation correction when both are enabled.
- **`LayoutSharp.Preprocessing.PageDeskew`** (public) — `Estimate(...)` returning
  `SkewEstimate(Angle, Confidence, IsReliable)`, and `Rotate(image, degrees)` which expands the canvas
  and fills the exposed corners white.
- **`LayoutPage.Rotation`, `SkewAngle`, `SourceWidth`, `SourceHeight`, `IsCorrected`, `MapToSource(…)`** —
  when a correction was applied, `Width` / `Height` and all block boxes are in the **corrected** frame
  (the image the detector saw); `MapToSource` maps them back to the caller's image — exact for
  quarter turns, an enclosing rectangle for a deskewed page, identity when nothing was corrected.

### Recognition plugins
- **Text**: `ITextRecognizer` (one method: crop in, text out) and `TextRecognizer.FromDelegate`.
  LayoutSharp ships no OCR engine; without a recognizer it runs layout-only. `RecognitionParallelism`
  fans regions out to a thread-safe recognizer.
- **Tables**: `ITableRecognizer` → `TableStructure` on `LayoutBlock.Table`, with
  `TableRecognizer.FromDelegate` / `FromHtml`. `TableStructure` keeps merged cells as origin cells
  with `RowSpan` / `ColumnSpan`, preserves the recognizer's `Html`, and offers `FromHtml`, `ToGrid`,
  `ToHtml`, `ToMarkdown`, `ToCsv` and `Offset` (crop → page coordinates), all bounded against
  hostile markup.
- **Formulas**: `IFormulaRecognizer` → LaTeX on `LayoutBlock.Latex`, with
  `FormulaRecognizer.FromDelegate`.
- Toggled per call with `RecognizeText` / `RecognizeTables` / `RecognizeFormulas`; all three share
  `RecognitionParallelism`. `ToMarkdown()` renders recovered tables as pipe tables (HTML when cells
  are merged) and formulas as `$$…$$`, falling back to placeholders when nothing was recognized.
- DI helpers `AddLayoutSharpTableRecognizer<T>()` / `AddLayoutSharpFormulaRecognizer<T>()`;
  `AddLayoutSharp()` picks up all three recognizer types from the container in either registration
  order.
- `samples/LayoutSharp.EasyOcrSample` bridges EasyOcrSharp as text, table and formula recognizer.

### Output
- `LayoutDocument → LayoutPage → LayoutBlock` with `ToJson()` (source-generated, AOT-safe),
  `FromJson()`, `ToPlainText()` and `ToMarkdown()`.
- `LayoutResult` reports `Model`, `ModelName`, `UsedGpu`, `TextRecognized`, `TablesRecognized`,
  `FormulasRecognized`, `ReadingOrderUsed` and `Duration`.

### Service surface & hardening
- `LayoutService` / `ILayoutService` with file, `Stream`, `byte[]`, `ReadOnlyMemory<byte>` and
  `Image<Rgb24>` overloads; `WarmUpAsync` for cold-start avoidance and cache pre-seeding;
  `AddLayoutSharp()` / `AddLayoutSharp<TRecognizer>()` for DI (singleton, thread-safe session reuse).
- Guards and typed errors: `MaxImagePixels` decompression-bomb guard (checked before decoding),
  `MaxPages`, `Offline` mode (`LAYOUTSHARP_OFFLINE=1`), HTTPS-only downloads, atomic cache writes,
  and `LayoutSharpException` subclasses (`ModelDownloadException`, `ModelChecksumException`,
  `OfflineModelMissingException`, `ImageTooLargeException`, `TooManyPagesException`,
  `LayoutInferenceException`). Every call takes a `CancellationToken`.
- Optional CUDA execution provider (`UseGpu`) with CPU fallback; `LayoutResult.UsedGpu` reports what ran.

### Performance
- Sessions are created with `GraphOptimizationLevel.ORT_ENABLE_ALL` and
  `ExecutionMode.ORT_SEQUENTIAL`, ~17 % faster than ORT's defaults on the reference machine
  (AMD Ryzen 5 4600H, 12 logical cores, CPU only).
- `LayoutServiceOptions.IntraOpThreads` caps the ORT intra-op pool: leave it `null` for one page at a
  time, set 2–4 when analyzing pages concurrently so sessions stop competing for cores.
- Reference timings on that machine, warm, 762×1000 page, `DoclingLayoutHeron`: ~500–550 ms per page
  end-to-end (`PPDocLayoutV3`, the default, is ~1.5× that at ~780–880 ms), of which
  inference is ~490–530 ms (~96 %), preprocessing 8–16 ms and detection decode < 2 ms; PNG decode
  19–89 ms; session creation ~0.9–1.3 s once, first analysis after it ~875 ms — both removed by
  `WarmUpAsync()`.
- int8 quantization was evaluated and **rejected**: dynamic int8 ran ~2× slower than fp32
  (1002 ms vs 498 ms) because ORT's `ConvInteger` kernels are slow on this CPU, and static int8 (QDQ)
  only matched fp32 (504 ms vs 522 ms) while collapsing accuracy (127 detections → 0 on a fixture).
  The model ships as fp32.

### Project
- Dependencies: `Microsoft.ML.OnnxRuntime` 1.29, `EasyImageSharp` 1.0, `Microsoft.Extensions.*`
  abstractions 10.0. Targets .NET 10, `IsAotCompatible`.
- **Imaging on `EasyImageSharp` (MIT).** All image I/O, resizing, rotation, cropping and deskew run
  on EasyImageSharp rather than SixLabors.ImageSharp, whose 3.x releases are under the Six Labors
  Split License — a commercial tier above a revenue threshold, which is awkward to inherit through an
  MIT package. EasyImageSharp is MIT with no threshold, and is one fully managed assembly with no
  native binaries, so there is no per-architecture asset payload and nothing extra to deploy for
  Native AOT, trimmed, single-file, Alpine or ARM64 targets.
- Multi-frame files whose **frames differ in size** are now supported: each frame is analyzed at its
  own dimensions instead of the whole file failing to decode. Consequently `MaxImagePixels` is
  enforced per frame rather than once from the container header, so an oversized later frame cannot
  slip past the guard behind a small first one.
- Unit tests (pipeline over a scripted detector, all three decoders, XY-cut, registry, custom-model
  validation, page correction, multi-page, table structure, exports, download policy) and
  model-backed integration tests over every image in `test/assets` (`Category=Integration`).
- `training/` holds the ONNX export and model-comparison scripts used to produce and vet the shipped
  detector.
