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
            // 文A日本語 rather than 甲Glossaries: the icon-noise rule still runs on the
            // automatic path today and strips the leading Han off both, which only changes the
            // answer for the one that has no other CJK left. Step 7 turns that rule off and
            // 甲Glossaries belongs back here then.
            new("文A日本語", bounds),
            new("攻", bounds),
        ];

        // The three normalisation paths the source language routes to today.
        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        OcrLayoutScript[] expected =
        [
            OcrLayoutScript.Latin,
            OcrLayoutScript.Cjk,
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

        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: false)[0].LayoutScript);
        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeBlocks(Blocks(), isCjk: true)[0].LayoutScript);
        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks())[0].LayoutScript);
    }
}
