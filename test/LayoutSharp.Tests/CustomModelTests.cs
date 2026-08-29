using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// Bring-your-own model coverage: description validation, spec construction, local-path loading
/// (no network, optional checksum) and end-to-end runs of all three output contracts over the tiny
/// synthetic graphs in <see cref="SyntheticOnnxModels"/>.
/// </summary>
[Collection("EnvironmentVariables")]
public class CustomModelTests
{
    private static string TempOnnx() => SyntheticOnnxModels.WriteTo(SyntheticOnnxModels.YoloEndToEndBase64, "custom.onnx");

    private static CustomLayoutModel Valid(string path) => new()
    {
        Path = path,
        InputSize = 640,
        Labels = new[] { "title", "text" },
    };

    // ---- validation ----

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var model = Valid(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.onnx"));
        var ex = Assert.Throws<FileNotFoundException>(() => new LayoutServiceOptions { CustomModel = model }.Validate());
        Assert.Contains("Custom layout model not found", ex.Message);
    }

    [Fact]
    public void Validate_BlankPath_Throws()
    {
        var options = new LayoutServiceOptions { CustomModel = new CustomLayoutModel { Path = "  ", InputSize = 640, Labels = new[] { "a" } } };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(100)]   // not a multiple of 32
    [InlineData(32)]    // below the floor
    [InlineData(8192)]  // above the ceiling
    public void Validate_BadInputSize_Throws(int size)
    {
        var path = TempOnnx();
        var options = new LayoutServiceOptions { CustomModel = Valid(path) with { InputSize = size } };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_BadLabels_Throw()
    {
        var path = TempOnnx();
        Assert.Throws<ArgumentException>(() => new LayoutServiceOptions { CustomModel = Valid(path) with { Labels = Array.Empty<string>() } }.Validate());
        Assert.Throws<ArgumentException>(() => new LayoutServiceOptions { CustomModel = Valid(path) with { Labels = new[] { "a", " " } } }.Validate());
        Assert.Throws<ArgumentException>(() => new LayoutServiceOptions { CustomModel = Valid(path) with { Labels = new[] { "a", "a" } } }.Validate());
    }

    [Fact]
    public void Validate_BadSha256_Throws()
    {
        var path = TempOnnx();
        var ex = Assert.Throws<ArgumentException>(() => new LayoutServiceOptions { CustomModel = Valid(path) with { Sha256 = "abc" } }.Validate());
        Assert.Contains("64 hexadecimal", ex.Message);

        // A well-formed checksum passes validation (it is verified later, when the session loads).
        new LayoutServiceOptions { CustomModel = Valid(path) with { Sha256 = SyntheticOnnxModels.Sha256Of(path) } }.Validate();
        new LayoutServiceOptions { CustomModel = Valid(path) with { Sha256 = new string('a', 64) } }.Validate();
    }

    // ---- service options ----

    [Fact]
    public void Options_CustomWithoutModel_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new LayoutServiceOptions { Model = LayoutModel.Custom }.Validate());
        Assert.Contains("CustomModel", ex.Message);
        Assert.Throws<ArgumentException>(() => new LayoutService(new LayoutServiceOptions { Model = LayoutModel.Custom }));
    }

    [Fact]
    public void Options_WithCustomModel_SwitchModelToCustom()
    {
        var path = TempOnnx();
        var options = new LayoutServiceOptions { CustomModel = Valid(path) };
        options.Validate();
        Assert.Equal(LayoutModel.Custom, options.Model);

        // Asking for some other model alongside a custom one is a mistake, not a preference.
        var conflicting = new LayoutServiceOptions { Model = (LayoutModel)42, CustomModel = Valid(path) };
        Assert.Throws<ArgumentException>(conflicting.Validate);
    }

    [Fact]
    public void UseCustomModel_SetsModelAndOptions()
    {
        var path = TempOnnx();
        var options = new LayoutServiceOptions().UseCustomModel(path, 640, new[] { "title", "text" }, LayoutOutputContract.YoloEndToEnd);

        Assert.Equal(LayoutModel.Custom, options.Model);
        Assert.NotNull(options.CustomModel);
        Assert.Equal(LayoutOutputContract.YoloEndToEnd, options.CustomModel!.OutputContract);
        Assert.Throws<ArgumentNullException>(() => new LayoutServiceOptions().UseCustomModel(null!));
    }

    [Fact]
    public void Options_Clone_DeepCopiesTheCustomModel()
    {
        var path = TempOnnx();
        var labels = new List<string> { "title", "text" };
        var options = new LayoutServiceOptions { CustomModel = Valid(path) with { Labels = labels } };

        var spec = ModelRegistry.FromCustom(options.CustomModel!.Snapshot());
        labels.Add("sneaky"); // mutating the caller's list must not change the snapshot
        Assert.Equal(2, spec.ClassCount);
    }

    // ---- spec construction ----

    [Fact]
    public void FromCustom_MapsLabels_ThroughBothVocabularies_ThenTypeMap()
    {
        var path = TempOnnx();
        var spec = ModelRegistry.FromCustom(new CustomLayoutModel
        {
            Path = path,
            InputSize = 800,
            Labels = new[] { "table", "Picture", "plain text", "weird_thing", "seal" },
            Normalization = LayoutModelNormalization.ImageNet,
            Name = "my-detector",
        });

        Assert.Equal(LayoutModel.Custom, spec.Model);
        Assert.Equal("custom.onnx", spec.FileName);
        Assert.Equal(Path.GetFullPath(path), spec.LocalPath);
        Assert.Equal(800, spec.InputSize);
        Assert.True(spec.ImageNetNormalize);
        Assert.Equal(DetectorKind.PaddleDetection, spec.Kind);
        Assert.Equal("my-detector", spec.Name);
        Assert.Equal(string.Empty, spec.Sha256);

        Assert.Equal(LayoutBlockType.Table, spec.Resolve(0).Type);      // PP-DocLayout vocabulary
        Assert.Equal(LayoutBlockType.Figure, spec.Resolve(1).Type);     // Docling vocabulary
        Assert.Equal(LayoutBlockType.Text, spec.Resolve(2).Type);       // DocLayout-YOLO vocabulary
        Assert.Equal(LayoutBlockType.Other, spec.Resolve(3).Type);      // unknown
        Assert.Equal(LayoutBlockType.Seal, spec.Resolve(4).Type);
        Assert.Equal("plain text", spec.Resolve(2).Name);               // raw label preserved verbatim
    }

    [Fact]
    public void FromCustom_TypeMapOverridesTheDefaults()
    {
        var path = TempOnnx();
        var spec = ModelRegistry.FromCustom(new CustomLayoutModel
        {
            Path = path,
            InputSize = 640,
            Labels = new[] { "table", "weird_thing" },
            TypeMap = new Dictionary<string, LayoutBlockType> { ["table"] = LayoutBlockType.Figure, ["weird_thing"] = LayoutBlockType.List },
        });

        Assert.Equal(LayoutBlockType.Figure, spec.Resolve(0).Type);
        Assert.Equal(LayoutBlockType.List, spec.Resolve(1).Type);
    }

    [Fact]
    public void FromCustom_MapsEveryOutputContractToItsDecoder()
    {
        var path = TempOnnx();
        Assert.Equal(DetectorKind.Detr, ModelRegistry.FromCustom(Valid(path) with { OutputContract = LayoutOutputContract.Detr }).Kind);
        Assert.Equal(DetectorKind.YoloEndToEnd, ModelRegistry.FromCustom(Valid(path) with { OutputContract = LayoutOutputContract.YoloEndToEnd }).Kind);
        Assert.Equal(DetectorKind.PaddleDetection, ModelRegistry.FromCustom(Valid(path)).Kind);
        Assert.Throws<ArgumentNullException>(() => ModelRegistry.FromCustom(null!));
    }

    [Fact]
    public void Registry_Get_Custom_Throws_AndAllExcludesIt()
    {
        Assert.Throws<ArgumentException>(() => ModelRegistry.Get(LayoutModel.Custom));
        Assert.DoesNotContain(ModelRegistry.All, s => s.Model == LayoutModel.Custom);
    }

    [Fact]
    public void SpecName_DefaultsToTheFileNameWithoutExtension()
    {
        Assert.Equal("docling-layout-heron", ModelRegistry.Get(LayoutModel.DoclingLayoutHeron).Name);
        Assert.Equal("custom", ModelRegistry.FromCustom(Valid(TempOnnx())).Name);
    }

    // ---- local-path loading ----

    [Fact]
    public async Task EnsureModel_LocalPath_SkipsTheNetworkEntirely()
    {
        var path = TempOnnx();
        var spec = ModelRegistry.FromCustom(Valid(path));

        var previous = Environment.GetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar);
        try
        {
            // Any attempt to download would fail against this host; the local path must short-circuit.
            Environment.SetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar, "https://127.0.0.1:1/models");
            var resolved = await ModelDownloadManager.EnsureModelAsync(spec, customCachePath: null, offline: false, logger: null, CancellationToken.None);
            Assert.Equal(Path.GetFullPath(path), resolved);
            Assert.Equal(Path.GetFullPath(path), ModelDownloadManager.GetModelPath(spec, "some-cache-dir"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelDownloadManager.BaseUrlEnvVar, previous);
        }
    }

    [Fact]
    public async Task EnsureModel_LocalPath_VerifiesSha256_AndKeepsTheFile()
    {
        var path = TempOnnx();
        var good = ModelRegistry.FromCustom(Valid(path) with { Sha256 = SyntheticOnnxModels.Sha256Of(path) });
        Assert.Equal(Path.GetFullPath(path), await ModelDownloadManager.EnsureModelAsync(good, null, false, null, CancellationToken.None));

        var bad = ModelRegistry.FromCustom(Valid(path) with { Sha256 = new string('A', 64) });
        var ex = await Assert.ThrowsAsync<ModelChecksumException>(() =>
            ModelDownloadManager.EnsureModelAsync(bad, null, false, null, CancellationToken.None));
        Assert.Contains("failed SHA-256 verification", ex.Message);
        Assert.True(File.Exists(path), "the user's file must not be deleted on a checksum mismatch");
    }

    [Fact]
    public async Task EnsureModel_LocalPath_MissingFile_Throws()
    {
        var path = TempOnnx();
        var spec = ModelRegistry.FromCustom(Valid(path));
        File.Delete(path);

        var ex = await Assert.ThrowsAsync<LayoutSharpException>(() =>
            ModelDownloadManager.EnsureModelAsync(spec, null, false, null, CancellationToken.None));
        Assert.Contains("not found", ex.Message);
    }

    // ---- end-to-end over the synthetic graphs ----

    [Fact]
    public async Task YoloEndToEnd_Contract_RunsAndUndoesTheLetterbox()
    {
        var path = SyntheticOnnxModels.WriteTo(SyntheticOnnxModels.YoloEndToEndBase64, "yolo.onnx");
        await using var svc = new LayoutService(new LayoutServiceOptions().UseCustomModel(new CustomLayoutModel
        {
            Path = path,
            InputSize = 64,
            Labels = new[] { "title", "plain text" },
            OutputContract = LayoutOutputContract.YoloEndToEnd,
            Sha256 = SyntheticOnnxModels.Sha256Of(path),
            Name = "synthetic-yolo",
        }));

        using var img = new Image<Rgb24>(64, 32);   // letterboxed: scale 1, padY 16
        var result = await svc.AnalyzeAsync(img);
        var blocks = result.Document.Pages[0].Blocks;

        Assert.Equal(LayoutModel.Custom, result.Model);
        Assert.Equal("synthetic-yolo", result.ModelName);
        Assert.Equal(ReadingOrderSource.XyCut, result.ReadingOrderUsed);
        Assert.Equal(2, blocks.Count);

        var title = blocks.Single(b => b.Type == LayoutBlockType.Title);
        Assert.Equal(0.9f, title.Confidence, 4);
        Assert.Equal(8, title.BoundingBox.MinX, 1);
        Assert.Equal(0, title.BoundingBox.MinY, 1);   // 8 - 16 → clamped to the top edge
        Assert.Equal(40, title.BoundingBox.MaxX, 1);
        Assert.Equal(8, title.BoundingBox.MaxY, 1);   // 24 - 16

        var text = blocks.Single(b => b.Type == LayoutBlockType.Text);
        Assert.Equal(16, text.BoundingBox.MinY, 1);   // 32 - 16
        Assert.Equal("plain text", text.RawClassName);
    }

    [Fact]
    public async Task YoloEndToEnd_RawHeadExport_FailsWithAnActionableMessage()
    {
        var path = SyntheticOnnxModels.WriteTo(SyntheticOnnxModels.YoloRawHeadBase64, "yolo-raw.onnx");
        await using var svc = new LayoutService(new LayoutServiceOptions().UseCustomModel(
            path, 64, new[] { "title", "text" }, LayoutOutputContract.YoloEndToEnd));

        var ex = await Assert.ThrowsAsync<LayoutSharpException>(() => svc.WarmUpAsync());
        Assert.Contains("raw YOLO head", ex.Message);
        Assert.Contains("nms=True", ex.Message);
    }

    [Fact]
    public async Task Detr_Contract_RunsAndValidatesTheLabelCount()
    {
        var path = SyntheticOnnxModels.WriteTo(SyntheticOnnxModels.DetrBase64, "detr.onnx");
        await using var svc = new LayoutService(new LayoutServiceOptions().UseCustomModel(
            path, 64, new[] { "Title", "Caption", "Table" }, LayoutOutputContract.Detr));

        using var img = new Image<Rgb24>(100, 200);
        var blocks = (await svc.AnalyzeAsync(img)).Document.Pages[0].Blocks;

        Assert.Equal(2, blocks.Count);   // the third query scores below 0.5
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Title);
        Assert.Contains(blocks, b => b.Type == LayoutBlockType.Table);

        // A label list that does not match the logits axis is a configuration error, not silent garbage.
        await using var wrong = new LayoutService(new LayoutServiceOptions().UseCustomModel(
            path, 64, new[] { "Title", "Caption", "Table", "Text", "Formula" }, LayoutOutputContract.Detr));
        var ex = await Assert.ThrowsAsync<LayoutSharpException>(() => wrong.AnalyzeAsync(img));
        Assert.Contains("CustomLayoutModel.Labels", ex.Message);
    }

    [Fact]
    public async Task PaddleDetection_Contract_ReadsOrderedRows_AndIgnoresTheMaskHead()
    {
        var path = SyntheticOnnxModels.WriteTo(SyntheticOnnxModels.PaddleOrderedBase64, "paddle-ordered.onnx");
        await using var svc = new LayoutService(new LayoutServiceOptions().UseCustomModel(
            path, 64, new[] { "doc_title", "text", "table" }));

        using var img = new Image<Rgb24>(64, 64);
        var result = await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { PinPageFurniture = false });
        var blocks = result.Document.Pages[0].Blocks;

        // Rows carry ranks 2 (text, right), 5 (doc_title, left) and 9 (table, bottom): model order wins
        // over the geometric left-to-right / top-to-bottom sort.
        Assert.Equal(ReadingOrderSource.Model, result.ReadingOrderUsed);
        Assert.Equal(new[] { LayoutBlockType.Text, LayoutBlockType.Title, LayoutBlockType.Table }, blocks.Select(b => b.Type));

        // Same detections, geometric order: the left-hand title comes first.
        var xyCut = (await svc.AnalyzeAsync(img, new LayoutAnalysisOptions { PinPageFurniture = false, ReadingOrderSource = ReadingOrderSource.XyCut }))
            .Document.Pages[0].Blocks;
        Assert.Equal(LayoutBlockType.Title, xyCut[0].Type);
    }

    [Fact]
    public void SelectOutputs_SkipsTheMaskHead()
    {
        var path = SyntheticOnnxModels.WriteTo(SyntheticOnnxModels.PaddleOrderedBase64, "paddle-ordered.onnx");
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(path);

        var names = PaddleLayoutDetector.SelectOutputs(session.OutputMetadata);
        Assert.NotNull(names);
        Assert.Equal(new[] { "fetch_name_0", "fetch_name_1" }, names);
    }
}
