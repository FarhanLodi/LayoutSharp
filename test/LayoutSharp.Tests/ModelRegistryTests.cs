using LayoutSharp.Internal;
using LayoutSharp.Models;
using LayoutSharp.Services;
using Xunit;

namespace LayoutSharp.Tests;

/// <summary>
/// The registry is the wire contract: a wrong input size, normalization flag or label index silently
/// mislabels every block, so each value is pinned against the exported model's own metadata.
/// </summary>
public class ModelRegistryTests
{
    [Fact]
    public void HeronSpec_MatchesTheExportedModel()
    {
        var spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

        Assert.Equal("docling-layout-heron.onnx", spec.FileName);
        Assert.Equal(640, spec.InputSize);
        Assert.Equal(17, spec.ClassCount);
        Assert.False(spec.ImageNetNormalize);           // preprocessor_config.json: rescale only
        Assert.Equal(DetectorKind.Detr, spec.Kind);     // raw logits + pred_boxes head, no NMS
        Assert.Equal(64, spec.Sha256.Length);
        Assert.Null(spec.LocalPath);                    // downloaded, not a bring-your-own file
        Assert.StartsWith(ModelRegistry.DefaultBaseUrl, spec.Url);
    }

    [Fact]
    public void Heron_LabelOrder_MatchesConfigJson()
    {
        // config.json id2label, verbatim: indices 0–10 are DocLayNet's 11, 11–16 Docling's extensions.
        var expected = new[]
        {
            "caption", "footnote", "formula", "list_item", "page_footer", "page_header", "picture",
            "section_header", "table", "text", "title", "document_index", "code", "checkbox_selected",
            "checkbox_unselected", "form", "key_value_region",
        };

        var spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);
        Assert.Equal(expected, spec.Classes.Select(c => c.Name));
    }

    [Fact]
    public void V3Spec_MatchesTheExportedModel()
    {
        var spec = ModelRegistry.Get(LayoutModel.PPDocLayoutV3);

        Assert.Equal("PP-DocLayoutV3.onnx", spec.FileName);
        Assert.Equal(800, spec.InputSize);
        Assert.Equal(25, spec.ClassCount);
        Assert.False(spec.ImageNetNormalize);                     // PaddleX: stretch resize + 1/255 only
        Assert.Equal(DetectorKind.PaddleDetection, spec.Kind);    // post-processing baked into the graph
        Assert.Equal(64, spec.Sha256.Length);
        Assert.Null(spec.LocalPath);
        // Re-hosted byte-for-byte from PaddleX's own export, so the SHA is PaddleX's.
        Assert.StartsWith(ModelRegistry.DefaultBaseUrl, spec.Url);
        Assert.EndsWith("/PP-DocLayoutV3.onnx", spec.Url);
    }

    [Fact]
    public void V3_IsTheDefaultModel()
        => Assert.Equal(LayoutModel.PPDocLayoutV3, new LayoutServiceOptions().Model);

    [Fact]
    public void V3_LabelOrder_MatchesTheExportedLabelFile()
    {
        // PaddleX emits its 25 labels alphabetically; the index order is the wire contract.
        var expected = new[]
        {
            "abstract", "algorithm", "aside_text", "chart", "content", "display_formula", "doc_title",
            "figure_title", "footer", "footer_image", "footnote", "formula_number", "header",
            "header_image", "image", "inline_formula", "number", "paragraph_title", "reference",
            "reference_content", "seal", "table", "text", "vertical_text", "vision_footnote",
        };

        var spec = ModelRegistry.Get(LayoutModel.PPDocLayoutV3);
        Assert.Equal(expected, spec.Classes.Select(c => c.Name));
    }

    [Fact]
    public void V3_ResolvesTheClassesHeronCannotEmit()
    {
        // The reason V3 is the default: seals, charts, page numbers and vertical CJK text have no
        // equivalent anywhere in heron's 17-label DocLayNet vocabulary.
        var spec = ModelRegistry.Get(LayoutModel.PPDocLayoutV3);

        Assert.Equal(LayoutBlockType.Seal, spec.Resolve(20).Type);
        Assert.Equal("seal", spec.Resolve(20).Name);
        Assert.Equal(LayoutBlockType.Figure, spec.Resolve(3).Type);       // chart
        Assert.Equal(LayoutBlockType.PageNumber, spec.Resolve(16).Type);  // number
        Assert.Equal(LayoutBlockType.Text, spec.Resolve(23).Type);        // vertical_text

        var heron = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);
        Assert.DoesNotContain(LayoutBlockType.Seal, heron.Classes.Select(c => c.Type));
    }

    [Fact]
    public void Interpolation_FollowsTheExportingFrameworksContract()
    {
        // PP-DocLayoutV3's inference.yml declares Resize(interp: 2), i.e. cv2.INTER_CUBIC; heron's
        // Hugging Face preprocessor_config.json declares BILINEAR. Using the wrong resampler shifts
        // every edge in the tensor and moves borderline scores across the confidence threshold.
        Assert.Equal(ResizeInterpolation.Bicubic, ModelRegistry.Get(LayoutModel.PPDocLayoutV3).Interpolation);
        Assert.Equal(ResizeInterpolation.Bilinear, ModelRegistry.Get(LayoutModel.DoclingLayoutHeron).Interpolation);
    }

    [Fact]
    public void All_ContainsEveryDownloadableModelOnce()
    {
        // LayoutModel.Custom is described by LayoutServiceOptions.CustomModel, not by the registry.
        var expected = Enum.GetValues<LayoutModel>().Where(m => m != LayoutModel.Custom).OrderBy(m => m).ToArray();
        Assert.Equal(expected, ModelRegistry.All.Select(s => s.Model).OrderBy(m => m));
        Assert.Equal(ModelRegistry.All.Count, ModelRegistry.All.Select(s => s.FileName).Distinct().Count());
    }

    [Fact]
    public void Get_Custom_Throws_BecauseItHasNoRegistryEntry()
        => Assert.ThrowsAny<ArgumentException>(() => ModelRegistry.Get(LayoutModel.Custom));

    [Fact]
    public void Classes_AreContiguousAndZeroBased()
    {
        foreach (var spec in ModelRegistry.All)
        {
            for (int i = 0; i < spec.Classes.Count; i++)
                Assert.Equal(i, spec.Classes[i].Index);
        }
    }

    [Fact]
    public void Resolve_KnownIndex_ReturnsMappedType()
    {
        var spec = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron);

        Assert.Equal(LayoutBlockType.Caption, spec.Resolve(0).Type);
        Assert.Equal(LayoutBlockType.Table, spec.Resolve(8).Type);
        Assert.Equal(LayoutBlockType.Title, spec.Resolve(10).Type);
        Assert.Equal(LayoutBlockType.KeyValueRegion, spec.Resolve(16).Type);
    }

    [Fact]
    public void Resolve_OutOfRange_FallsBackToOther()
    {
        var resolved = ModelRegistry.Get(LayoutModel.DoclingLayoutHeron).Resolve(999);

        Assert.Equal(LayoutBlockType.Other, resolved.Type);
        Assert.Equal(999, resolved.Index);
        Assert.Equal("class_999", resolved.Name);
    }

    [Fact]
    public void EveryLabel_HasAnExplicitMapping()
    {
        foreach (var spec in ModelRegistry.All)
            Assert.All(spec.Classes, c => Assert.NotEqual(LayoutBlockType.Other, c.Type));
    }

    [Theory]
    [InlineData("title", LayoutBlockType.Title)]
    [InlineData("section_header", LayoutBlockType.SectionHeader)]
    [InlineData("list_item", LayoutBlockType.List)]
    [InlineData("picture", LayoutBlockType.Figure)]
    [InlineData("checkbox_selected", LayoutBlockType.Checkbox)]
    [InlineData("key_value_region", LayoutBlockType.KeyValueRegion)]
    // PP-DocLayoutV3's vocabulary spells several types differently; both spellings map.
    [InlineData("doc_title", LayoutBlockType.Title)]
    [InlineData("paragraph_title", LayoutBlockType.SectionHeader)]
    [InlineData("chart", LayoutBlockType.Figure)]
    [InlineData("seal", LayoutBlockType.Seal)]
    [InlineData("stamp", LayoutBlockType.Seal)]
    [InlineData("number", LayoutBlockType.PageNumber)]
    [InlineData("header", LayoutBlockType.PageHeader)]
    [InlineData("footer", LayoutBlockType.PageFooter)]
    [InlineData("header_image", LayoutBlockType.PageHeader)]
    [InlineData("footer_image", LayoutBlockType.PageFooter)]
    [InlineData("vertical_text", LayoutBlockType.Text)]
    [InlineData("nonsense_label", LayoutBlockType.Other)]
    public void TypeOf_MapsLabels(string label, LayoutBlockType expected)
        => Assert.Equal(expected, ModelRegistry.TypeOf(label));

    [Fact]
    public void TextBearingClassification_MatchesExpectations()
    {
        Assert.True(LayoutBlockType.Text.IsTextBearing());
        Assert.True(LayoutBlockType.Title.IsTextBearing());
        Assert.True(LayoutBlockType.Caption.IsTextBearing());
        Assert.True(LayoutBlockType.List.IsTextBearing());

        Assert.False(LayoutBlockType.Table.IsTextBearing());
        Assert.False(LayoutBlockType.Figure.IsTextBearing());
        Assert.False(LayoutBlockType.Formula.IsTextBearing());
        Assert.False(LayoutBlockType.Checkbox.IsTextBearing());
    }

    [Fact]
    public void DocOrientationAsset_IsPinned()
    {
        var asset = ModelRegistry.DocOrientation;

        Assert.Equal("PP-LCNet_x1_0_doc_ori.onnx", asset.FileName);
        Assert.Equal(64, asset.Sha256.Length);
        Assert.StartsWith(ModelRegistry.DefaultBaseUrl, asset.Url);
    }
}
