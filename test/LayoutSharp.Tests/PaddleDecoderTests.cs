using LayoutSharp.Internal;
using LayoutSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

public class PaddleDecoderTests
{
    private static readonly LayoutModelSpec PlusL = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

    // Rows: [class_id, score, x1, y1, x2, y2] in the model's 800×800 input space.
    private static float[] Rows(params float[][] rows) => rows.SelectMany(r => r).ToArray();

    [Fact]
    public void Decode_ScalesBoxesFromInputSpaceToSource()
    {
        // Source is 1600×400 → x scale 2, y scale 0.5.
        var rows = Rows(new float[] { 9, 0.9f, 100, 200, 300, 400 }); // class 9 = text
        var det = PaddleDetectionDecoder.Decode(rows, 1, 800, 1600, 400, 0.5f, PlusL);

        var d = Assert.Single(det);
        Assert.Equal(LayoutBlockType.Text, d.Class.Type);
        Assert.Equal("text", d.Class.Name);
        Assert.Equal(0.9f, d.Score, 5);
        Assert.Equal(200, d.Box.MinX, 3);
        Assert.Equal(100, d.Box.MinY, 3);
        Assert.Equal(600, d.Box.MaxX, 3);
        Assert.Equal(200, d.Box.MaxY, 3);
    }

    [Fact]
    public void Decode_FiltersBelowThreshold_AndNegativeClass()
    {
        var rows = Rows(
            new float[] { 8, 0.95f, 0, 0, 100, 100 },   // table, kept
            new float[] { 2, 0.10f, 0, 0, 100, 100 },   // below threshold
            new float[] { -1, 0.99f, 0, 0, 100, 100 }); // PicoDet "nothing found" sentinel
        var det = PaddleDetectionDecoder.Decode(rows, 3, 800, 800, 800, 0.5f, PlusL);

        var d = Assert.Single(det);
        Assert.Equal(LayoutBlockType.Table, d.Class.Type);
    }

    [Fact]
    public void Decode_ClampsToImage_AndDropsDegenerateBoxes()
    {
        var rows = Rows(
            new float[] { 2, 0.9f, -50, -50, 100, 100 },      // spills outside → clamped
            new float[] { 2, 0.9f, 790, 10, 900, 20 },        // spills right → clamped to 800
            new float[] { 2, 0.9f, 10, 10, 10.2f, 10.3f });   // < 1px after scaling → dropped
        var det = PaddleDetectionDecoder.Decode(rows, 3, 800, 800, 800, 0.5f, PlusL);

        Assert.Equal(2, det.Count);
        Assert.Equal(0, det[0].Box.MinX);
        Assert.Equal(0, det[0].Box.MinY);
        Assert.Equal(800, det[1].Box.MaxX);
    }

    [Fact]
    public void Decode_RespectsRowCount_AndBufferLength()
    {
        var rows = Rows(
            new float[] { 2, 0.9f, 0, 0, 100, 100 },
            new float[] { 2, 0.9f, 0, 0, 100, 100 });
        // Asked for 5 rows but only 2 present → decodes 2 without reading past the buffer.
        Assert.Equal(2, PaddleDetectionDecoder.Decode(rows, 5, 800, 800, 800, 0.5f, PlusL).Count);
        // Asked for 1 → decodes 1.
        Assert.Single(PaddleDetectionDecoder.Decode(rows, 1, 800, 800, 800, 0.5f, PlusL));
        // Empty output → no detections, no throw.
        Assert.Empty(PaddleDetectionDecoder.Decode(ReadOnlySpan<float>.Empty, 0, 800, 800, 800, 0.5f, PlusL));
    }

    [Fact]
    public void Decode_UnknownClassIndex_BecomesOther()
    {
        var rows = Rows(new float[] { 57, 0.9f, 0, 0, 100, 100 });
        var d = Assert.Single(PaddleDetectionDecoder.Decode(rows, 1, 800, 800, 800, 0.5f, PlusL));
        Assert.Equal(LayoutBlockType.Other, d.Class.Type);
        Assert.Equal(57, d.Class.Index);
    }

    [Fact]
    public void Preprocess_ProducesNchwRgbTensor_RescaledOnly()
    {
        using var img = new Image<Rgb24>(4, 2, new Rgb24(255, 0, 0)); // solid red
        var t = ImageProcessing.Preprocess(img, 2, imageNetNormalize: false);

        Assert.Equal(3 * 2 * 2, t.Length);
        // R plane = 1.0, G and B planes = 0.
        Assert.All(t.Take(4), v => Assert.Equal(1f, v, 5));
        Assert.All(t.Skip(4), v => Assert.Equal(0f, v, 5));
    }

    [Fact]
    public void Preprocess_ImageNetNormalization_AppliesMeanStd()
    {
        using var img = new Image<Rgb24>(2, 2, new Rgb24(255, 255, 255)); // solid white
        var t = ImageProcessing.Preprocess(img, 2, imageNetNormalize: true);

        // (1 - mean) / std per channel.
        Assert.Equal((1f - 0.485f) / 0.229f, t[0], 3);
        Assert.Equal((1f - 0.456f) / 0.224f, t[4], 3);
        Assert.Equal((1f - 0.406f) / 0.225f, t[8], 3);
    }
}
