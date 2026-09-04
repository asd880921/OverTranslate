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
}
