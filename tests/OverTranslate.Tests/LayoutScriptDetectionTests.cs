using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The layout side's view of a block's script, which must come from the text and nothing else.
/// </summary>
public class LayoutScriptDetectionTests
{
    [Theory]
    [InlineData("Shots deal more damage", OcrLayoutScript.Latin)]
    [InlineData("Lv100", OcrLayoutScript.Latin)]
    [InlineData("2026/07/24 23:29", OcrLayoutScript.Latin)]
    [InlineData("彈匣內每發子彈", OcrLayoutScript.Cjk)]
    [InlineData("カタカナ", OcrLayoutScript.Cjk)]
    // One Han character is a full-width glyph like any other. Two was only ever needed to tell
    // Chinese from Japanese, which the layout side never asks. See #161.
    [InlineData("攻", OcrLayoutScript.Cjk)]
    [InlineData("→", OcrLayoutScript.Unknown)]
    [InlineData("", OcrLayoutScript.Unknown)]
    public void Script_IsReadOffTheTextItself(string text, OcrLayoutScript expected)
    {
        Assert.Equal(expected, LayoutScriptDetection.For(text));
    }

    [Theory]
    // Not an edge case: a line of Japanese technical prose carrying Western terms, a game panel
    // labelling a stat, and a wiki heading are all ordinary content.
    [InlineData("甲Glossaries")]
    [InlineData("文A日本語")]
    [InlineData("本Wikiについて")]
    [InlineData("2005 年から CSS, HTML, JavaScript のドキュメントを作成しています。")]
    public void MixedScriptBlock_IsClassifiedAsMixed(string text)
    {
        Assert.Equal(OcrLayoutScript.Mixed, LayoutScriptDetection.For(text));
    }

    [Fact]
    public void LayoutScript_IsChosenPerBlock_RegardlessOfSourceLanguage()
    {
        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        List<OcrTextBlock> Blocks() =>
        [
            new("OPTIONS", bounds),
            new("ゲーム設定", bounds),
            new("甲Glossaries", bounds),
            new("文A日本語", bounds),
            new("攻", bounds),
        ];

        // The three normalisation paths the source language routes to today.
        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        OcrLayoutScript[] expected =
        [
            OcrLayoutScript.Latin,
            OcrLayoutScript.Cjk,
            OcrLayoutScript.Mixed,
            OcrLayoutScript.Mixed,
            OcrLayoutScript.Cjk,
        ];

        Assert.Equal(expected, latin.Select(block => block.LayoutScript));
        Assert.Equal(expected, cjk.Select(block => block.LayoutScript));
        Assert.Equal(expected, automatic.Select(block => block.LayoutScript));
    }

    [Fact]
    public void MixedScriptClassification_IsIndependentOfSourceLanguage()
    {
        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        List<OcrTextBlock> Blocks() => [new("本Wikiについて", bounds)];

        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false)[0].LayoutScript);
        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true)[0].LayoutScript);
        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks())[0].LayoutScript);
    }

    /// <summary>
    /// Nothing rewrites a block's text on the way to layout any more, so the classification is of
    /// what was actually read.
    /// </summary>
    /// <remarks>
    /// The lone-ideograph cleanup used to run on the Latin and automatic paths and not on the CJK
    /// one, which took the leading Han off each of these under 英文 and 自動 and left it under 日文.
    /// For 甲Glossaries that changed the answer outright — Mixed became Latin — and for the other two
    /// it changed the text a reader would be shown. See 本Wikiについて, which is a heading, not noise.
    /// </remarks>
    [Theory]
    [InlineData("甲Glossaries")]
    [InlineData("文A日本語")]
    [InlineData("本Wikiについて")]
    public void MixedScriptText_IsKeptVerbatim_OnEverySourceLanguage(string text)
    {
        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        List<OcrTextBlock> Blocks() => [new(text, bounds)];

        foreach (var normalized in new[]
        {
            OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false),
            OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true),
            OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks()),
        })
        {
            var block = Assert.Single(normalized);
            Assert.Equal(text, block.Text);
            Assert.Equal(OcrLayoutScript.Mixed, block.LayoutScript);
        }
    }

    /// <summary>
    /// The rule itself stays, unreferenced by the product, as the starting point for whatever
    /// replaces it: 904 blocks measured, four hits, all four real icon noise.
    /// </summary>
    [Fact]
    public void TheLoneIdeographRule_StillExists_ButNoLongerRunsOnAnyPath()
    {
        Assert.Equal("Glossaries", OnnxOcrEngine.StripLoneIdeographs("甲Glossaries"));

        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        Assert.Equal(
            "甲Glossaries",
            OnnxOcrEngine.NormalizeAutomaticBlocks([new("甲Glossaries", bounds)])[0].Text);
    }
}
