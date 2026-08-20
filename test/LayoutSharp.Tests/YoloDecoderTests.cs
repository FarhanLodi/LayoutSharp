using LayoutSharp.Internal;
using LayoutSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// Pure-function coverage of the Ultralytics end-to-end contract: letterbox geometry, the row
/// decoder, and the preprocessing that produces the padded canvas. No ONNX session involved.
/// </summary>
public class YoloDecoderTests
{
    /// <summary>A three-class custom spec standing in for a YOLO layout model.</summary>
    private static readonly LayoutModelSpec Spec = ModelRegistry.FromCustom(new CustomLayoutModel
    {
        Path = typeof(YoloDecoderTests).Assembly.Location, // any existing file: FromCustom only reads the name
        InputSize = 640,
        Labels = new[] { "title", "plain text", "table" },
        OutputContract = LayoutOutputContract.YoloEndToEnd,
    });

    // Rows: [x1, y1, x2, y2, score, class_id] in letterboxed input pixels.
    private static float[] Rows(params float[][] rows) => rows.SelectMany(r => r).ToArray();

    [Fact]
    public void Letterbox_ScalesByTheSmallerRatio_AndCentersTheContent()
    {
        var lb = Letterbox.Compute(2816, 1536, 640);

        Assert.Equal(640.0 / 2816, lb.Scale, 6);
        Assert.Equal(640, lb.ContentWidth);
        Assert.Equal(349, lb.ContentHeight);   // round(1536 * 640/2816)
        Assert.Equal(0, lb.PadX, 6);
        Assert.Equal((640 - 349) / 2.0, lb.PadY, 6);
        Assert.Equal(0, lb.Left);
        Assert.Equal(145, lb.Top);             // the odd pixel goes to the bottom, as in Ultralytics
    }

    [Fact]
    public void Letterbox_SquareImage_NeedsNoPadding_AndScalesUp()
    {
        var lb = Letterbox.Compute(320, 320, 640);
        Assert.Equal(2.0, lb.Scale, 6);
        Assert.Equal(640, lb.ContentWidth);
        Assert.Equal(640, lb.ContentHeight);
        Assert.Equal(0, lb.PadX, 6);
        Assert.Equal(0, lb.PadY, 6);
    }

    [Fact]
    public void Letterbox_RoundTripsCoordinates()
    {
        var lb = Letterbox.Compute(1000, 400, 640);
        // A source point maps into the canvas and back to itself.
        double canvasX = 250 * lb.Scale + lb.PadX;
        double canvasY = 100 * lb.Scale + lb.PadY;
        Assert.Equal(250, lb.ToSourceX(canvasX), 6);
        Assert.Equal(100, lb.ToSourceY(canvasY), 6);
    }

    [Fact]
    public void DecodeRows_UndoesTheLetterbox_ToSourcePixels()
    {
        // 1000×400 on a 640 canvas: scale 0.64, padY = (640 - 256) / 2 = 192, padX = 0.
        var lb = Letterbox.Compute(1000, 400, 640);
        var rows = Rows(new[] { 64f, 192f + 32f, 320f, 192f + 160f, 0.9f, 1f });

        var det = Assert.Single(YoloDecoder.DecodeRows(rows, 1, lb, 1000, 400, 0.5f, Spec));

        Assert.Equal("plain text", det.Class.Name);
        Assert.Equal(LayoutBlockType.Text, det.Class.Type);
        Assert.Equal(0.9f, det.Score, 5);
        Assert.Equal(100, det.Box.MinX, 1);
        Assert.Equal(50, det.Box.MinY, 1);
        Assert.Equal(500, det.Box.MaxX, 1);
        Assert.Equal(250, det.Box.MaxY, 1);
        Assert.False(det.HasOrderHint);
    }

    [Fact]
    public void DecodeRows_DropsPadding_LowScores_AndNegativeClasses()
    {
        var lb = Letterbox.Compute(640, 640, 640);
        var rows = Rows(
            new[] { 10f, 10f, 100f, 100f, 0.95f, 2f },   // kept
            new[] { 10f, 10f, 100f, 100f, 0.10f, 0f },   // below threshold
            new[] { 10f, 10f, 100f, 100f, 0.95f, -1f },  // sentinel class
            new[] { 0f, 0f, 0f, 0f, 0f, 0f });           // zero padding row

        var det = YoloDecoder.DecodeRows(rows, 4, lb, 640, 640, 0.5f, Spec);

        var d = Assert.Single(det);
        Assert.Equal(LayoutBlockType.Table, d.Class.Type);
    }

    [Fact]
    public void DecodeRows_ClampsToTheImage_AndRespectsRowCount()
    {
        var lb = Letterbox.Compute(640, 640, 640);
        var rows = Rows(
            new[] { -20f, -20f, 200f, 200f, 0.9f, 0f },
            new[] { 500f, 500f, 900f, 900f, 0.9f, 0f });

        var det = YoloDecoder.DecodeRows(rows, 5, lb, 640, 640, 0.5f, Spec); // asked for more rows than exist
        Assert.Equal(2, det.Count);
        Assert.Equal(0, det[0].Box.MinX);
        Assert.Equal(0, det[0].Box.MinY);
        Assert.Equal(640, det[1].Box.MaxX);
        Assert.Equal(640, det[1].Box.MaxY);

        Assert.Single(YoloDecoder.DecodeRows(rows, 1, lb, 640, 640, 0.5f, Spec));
        Assert.Empty(YoloDecoder.DecodeRows(ReadOnlySpan<float>.Empty, 0, lb, 640, 640, 0.5f, Spec));
    }

    [Fact]
    public void DecodeRows_UnknownClassIndex_BecomesOther()
    {
        var lb = Letterbox.Compute(640, 640, 640);
        var rows = Rows(new[] { 0f, 0f, 100f, 100f, 0.9f, 42f });
        var d = Assert.Single(YoloDecoder.DecodeRows(rows, 1, lb, 640, 640, 0.5f, Spec));
        Assert.Equal(LayoutBlockType.Other, d.Class.Type);
        Assert.Equal("class_42", d.Class.Name);
    }

    [Fact]
    public void PreprocessLetterbox_PadsWithGray114_AndKeepsAspect()
    {
        using var img = new Image<Rgb24>(64, 32, new Rgb24(255, 0, 0)); // wide red image
        var tensor = ImageProcessing.PreprocessLetterbox(img, 64, imageNetNormalize: false, out var lb);

        Assert.Equal(3 * 64 * 64, tensor.Length);
        Assert.Equal(1.0, lb.Scale, 6);
        Assert.Equal(16, lb.Top);

        // Row 0 is padding: gray 114 in every channel. Row 32 is content: pure red.
        Assert.Equal(114 / 255f, tensor[0], 4);                       // R plane, row 0
        Assert.Equal(114 / 255f, tensor[64 * 64 + 0], 4);             // G plane, row 0
        Assert.Equal(1f, tensor[32 * 64], 4);                         // R plane, row 32
        Assert.Equal(0f, tensor[64 * 64 + 32 * 64], 4);               // G plane, row 32
    }
}
