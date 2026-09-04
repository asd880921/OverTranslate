using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The geometry grouping measures with, which must not depend on the source language the user
/// picked. Bounds may still be normalised per script — that is the overlay's business.
/// </summary>
public class OcrLayoutGeometryTests
{
    [Fact]
    public void LayoutBounds_IsIdenticalAcrossSourceLanguages_ForSameRawDetection()
    {
        var detected = new System.Windows.Rect(40, 120, 240, 30);
        List<OcrTextBlock> Blocks() =>
        [
            new("Shots deal more damage", detected),
            new("彈匣內每發子彈的傷害", detected),
        ];

        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        foreach (var normalized in new[] { latin, cjk, automatic })
        {
            Assert.Equal(detected, normalized[0].LayoutBounds);
            Assert.Equal(detected, normalized[1].LayoutBounds);
        }

        // The 0.820 that this exists to get away from: Bounds still differs by script, and still
        // differs between the paths the source language routes to. Grouping must stop reading it.
        Assert.NotEqual(latin[1].Bounds.Height, cjk[1].Bounds.Height);
    }

    [Fact]
    public void LayoutBounds_IsTheDetectionBox_NotTheNormalisedOne()
    {
        var detected = new System.Windows.Rect(0, 0, 120, 30);
        var normalized = OnnxOcrEngine.NormalizeBlocks([new("設定設定", detected)], isCjk: true);

        Assert.Equal(detected, normalized[0].LayoutBounds);
        Assert.True(normalized[0].Bounds.Height < detected.Height);
    }

    [Fact]
    public void LayoutGlyphHeight_IsIdenticalAcrossSourceLanguages_ForSameRawDetection()
    {
        var detected = new System.Windows.Rect(40, 120, 240, 30);
        List<OcrTextBlock> Blocks() =>
        [
            new("Shots deal more damage", detected),
            new("彈匣內每發子彈的傷害", detected),
        ];

        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        Assert.Equal(latin.Select(b => b.LayoutGlyphHeight), cjk.Select(b => b.LayoutGlyphHeight));
        Assert.Equal(latin.Select(b => b.LayoutGlyphHeight), automatic.Select(b => b.LayoutGlyphHeight));

        // The render metric is what still differs: it is keyed on the language route, on purpose.
        Assert.NotEqual(latin[0].RenderGlyphHeight, automatic[0].RenderGlyphHeight);
    }

    [Fact]
    public void CjkLayoutGlyphHeight_IsAnEstimate_NotTheDetectionBox()
    {
        var detected = new System.Windows.Rect(0, 0, 120, 30);
        var normalized = OnnxOcrEngine.NormalizeBlocks([new("設定設定", detected)], isCjk: true);

        var height = Assert.IsType<double>(normalized[0].LayoutGlyphHeight);
        Assert.True(height < normalized[0].LayoutBounds.Height);
        // Same 0.82 shrink and pitch clamp the overlay's own CJK box carries.
        Assert.Equal(normalized[0].Bounds.Height, height, precision: 9);
    }

    [Theory]
    [InlineData("甲Glossaries")]
    [InlineData("→")]
    public void MixedAndUnknownBlocks_CarryNoLayoutGlyphHeight(string text)
    {
        var detected = new System.Windows.Rect(0, 0, 240, 30);
        var normalized = OnnxOcrEngine.NormalizeBlocks([new(text, detected)], isCjk: false);

        Assert.Null(normalized[0].LayoutGlyphHeight);
    }
}
