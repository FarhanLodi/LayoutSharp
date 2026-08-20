using LayoutSharp.Internal;
using LayoutSharp.Models;
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
    // A custom PaddleDetection export uses PP-DocLayout's vocabulary; those labels map too.
    [InlineData("doc_title", LayoutBlockType.Title)]
    [InlineData("paragraph_title", LayoutBlockType.SectionHeader)]
    [InlineData("chart", LayoutBlockType.Figure)]
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
