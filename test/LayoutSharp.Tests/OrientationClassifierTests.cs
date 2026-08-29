using LayoutSharp.Internal;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// The parts of the document-orientation stage that need no model: output decoding, the PaddleClas
/// centre-crop preprocessing, and the asset entry.
/// </summary>
public class OrientationClassifierTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 90)]
    [InlineData(2, 180)]
    [InlineData(3, 270)]
    public void Decode_ArgmaxMapsToClockwiseDegrees(int index, int expectedRotation)
    {
        var probs = new[] { 0.1f, 0.1f, 0.1f, 0.1f };
        probs[index] = 0.7f;

        var p = OnnxOrientationClassifier.Decode(probs);

        Assert.Equal(expectedRotation, p.Rotation);
        Assert.Equal(0.7f, p.Confidence, 5);
        Assert.Equal(probs[0], p.P0, 5);
        Assert.Equal(probs[1], p.P90, 5);
        Assert.Equal(probs[2], p.P180, 5);
        Assert.Equal(probs[3], p.P270, 5);
    }

    [Fact]
    public void Decode_AlreadySoftmaxedOutput_IsNotSoftmaxedAgain()
    {
        // The graph's last node is Softmax, so probabilities must pass through untouched.
        var probs = new[] { 0.92f, 0.03f, 0.02f, 0.03f };
        var p = OnnxOrientationClassifier.Decode(probs);
        Assert.Equal(0, p.Rotation);
        Assert.Equal(0.92f, p.Confidence, 5);
    }

    [Fact]
    public void Decode_RawLogits_AreSoftmaxed()
    {
        var p = OnnxOrientationClassifier.Decode(new[] { 1f, 4f, 2f, 0f });
        Assert.Equal(90, p.Rotation);
        Assert.InRange(p.Confidence, 0.75f, 0.85f);   // softmax of (1,4,2,0)
        Assert.Equal(1f, p.P0 + p.P90 + p.P180 + p.P270, 4);
    }

    [Fact]
    public void Decode_TooFewValues_Throws()
        => Assert.Throws<LayoutSharpException>(() => OnnxOrientationClassifier.Decode(new[] { 1f, 2f }));

    [Fact]
    public void PreprocessCenterCrop_ShapeAndNormalization()
    {
        using var grey = new Image<Rgb24>(300, 600, new Rgb24(128, 128, 128));
        var tensor = ImageProcessing.PreprocessCenterCrop(grey);

        Assert.Equal(3 * 224 * 224, tensor.Length);

        // (128/255 - mean) / std per channel.
        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        for (int c = 0; c < 3; c++)
        {
            float expected = (128f / 255f - mean[c]) / std[c];
            Assert.Equal(expected, tensor[c * 224 * 224], 3);
            Assert.Equal(expected, tensor[c * 224 * 224 + 224 * 112 + 112], 3);
        }
    }

    [Fact]
    public void PreprocessCenterCrop_KeepsTheCentre_AndDropsTheEdges()
    {
        // A tall page: white with a red band down the middle third. After short-side-256 + crop-224
        // the centre pixel must be red and the corners white — a stretch-to-224 would keep the edges.
        using var page = new Image<Rgb24>(400, 1600, Color.White.ToPixel<Rgb24>());
        for (int y = 0; y < page.Height; y++)
            for (int x = 150; x < 250; x++)
                page[x, y] = new Rgb24(255, 0, 0);

        var t = ImageProcessing.PreprocessCenterCrop(page);
        int hw = 224 * 224;
        int centre = 112 * 224 + 112;

        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        float RedAt(int c, int i) => t[c * hw + i];

        // Centre: red (R high, G/B at 0).
        Assert.Equal((1f - mean[0]) / std[0], RedAt(0, centre), 2);
        Assert.Equal((0f - mean[1]) / std[1], RedAt(1, centre), 2);

        // Left edge of the crop: white.
        int leftEdge = 112 * 224 + 2;
        Assert.Equal((1f - mean[1]) / std[1], RedAt(1, leftEdge), 2);
    }

    [Fact]
    public void PreprocessCenterCrop_ValidatesArguments()
    {
        using var img = new Image<Rgb24>(10, 10);
        Assert.Throws<ArgumentNullException>(() => ImageProcessing.PreprocessCenterCrop(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageProcessing.PreprocessCenterCrop(img, shortSide: 100, crop: 224));
        // Upscaling a tiny image is fine.
        Assert.Equal(3 * 224 * 224, ImageProcessing.PreprocessCenterCrop(img).Length);
    }

    [Fact]
    public void DocOrientationAsset_IsRegistered()
    {
        var asset = ModelRegistry.DocOrientation;
        Assert.Equal("PP-LCNet_x1_0_doc_ori.onnx", asset.FileName);
        Assert.Matches("^[0-9A-F]{64}$", asset.Sha256);
        Assert.StartsWith(ModelRegistry.DefaultBaseUrl, asset.Url);
        Assert.EndsWith("/PP-LCNet_x1_0_doc_ori.onnx", asset.Url);
    }

    [Fact]
    public void LayoutModelSpec_ExposesItselfAsAnAsset()
    {
        var spec = ModelRegistry.Get(LayoutSharp.Models.LayoutModel.DoclingLayoutHeron);
        Assert.Equal(spec.FileName, spec.Asset.FileName);
        Assert.Equal(spec.Sha256, spec.Asset.Sha256);
        Assert.Equal(spec.Url, spec.Asset.Url);
    }

    [Fact]
    public async Task Classifier_OfflineWithoutCachedModel_ThrowsOfflineModelMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "layoutsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var clf = new OnnxOrientationClassifier(dir, useGpu: false, offline: true, logger: null);
        using var img = new Image<Rgb24>(50, 50);

        var ex = await Assert.ThrowsAsync<OfflineModelMissingException>(() => clf.ClassifyAsync(img, CancellationToken.None));
        Assert.Equal(Path.Combine(dir, "PP-LCNet_x1_0_doc_ori.onnx"), ex.ExpectedPath);
    }
}
