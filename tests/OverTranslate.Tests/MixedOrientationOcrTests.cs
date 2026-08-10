using System.Drawing;
using System.Drawing.Text;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class MixedOrientationOcrTests
{
    [Theory]
    [InlineData("AUTO", true)]
    [InlineData("ZH", true)]
    [InlineData("ZH-HANT", true)]
    [InlineData("JA", true)]
    [InlineData("EN", false)]
    [InlineData("KO", false)]
    public void UsesVerticalPass_OnlyForLanguagesCoveredByTheCjkModel(
        string language,
        bool expected)
    {
        Assert.Equal(expected, MixedOrientationOcr.UsesVerticalPass(language));
    }

    [Fact]
    public void MapBack_RestoresBoundsAndSourceLinesAfterLeftQuarterTurn()
    {
        var rotated = new OcrTextBlock(
            "日本語",
            new Rect(20, 30, 100, 20),
            [new Rect(22, 32, 96, 16)],
            Confidence: 0.91);

        var mapped = MixedOrientationOcr.MapBack(rotated, originalWidth: 300);

        Assert.Equal(new Rect(250, 20, 20, 100), mapped.Bounds);
        Assert.Equal(new Rect(252, 22, 16, 96), Assert.Single(mapped.SourceLineBounds!));
        Assert.Equal(20, mapped.SourceGlyphHeight);
        Assert.Equal(0.91, mapped.Confidence);
    }

    [Fact]
    public void Merge_KeepsIndependentHorizontalAndVerticalText()
    {
        var horizontal = new OcrTextBlock("horizontal", new Rect(10, 10, 120, 24));
        var vertical = new OcrTextBlock("日本語", new Rect(250, 20, 20, 100));

        var merged = MixedOrientationOcr.Merge([horizontal], [vertical]);

        Assert.Equal(2, merged.Count);
        Assert.Contains(horizontal, merged);
        Assert.Contains(vertical, merged);
    }

    [Fact]
    public void Merge_ReplacesOverlappingFragmentsWithTheStrongerVerticalReading()
    {
        var firstFragment = new OcrTextBlock("日本", new Rect(101, 25, 18, 26), Confidence: 0.76);
        var secondFragment = new OcrTextBlock("語", new Rect(101, 64, 18, 26), Confidence: 0.72);
        var vertical = new OcrTextBlock("日本語縦書", new Rect(100, 20, 20, 120), Confidence: 0.88);

        var merged = MixedOrientationOcr.Merge([firstFragment, secondFragment], [vertical]);

        Assert.Equal(vertical, Assert.Single(merged));
    }

    [Fact]
    public void Merge_KeepsLongerHigherConfidenceHorizontalReading()
    {
        var horizontal = new OcrTextBlock("日本語縦書", new Rect(100, 20, 20, 120), Confidence: 0.96);
        var vertical = new OcrTextBlock("日本語", new Rect(100, 20, 20, 120), Confidence: 0.51);

        var merged = MixedOrientationOcr.Merge([horizontal], [vertical]);

        Assert.Equal(horizontal, Assert.Single(merged));
    }

    [Fact]
    public async Task RecognizeAsync_MergesOriginalAndRotatedPassWithoutAUserMode()
    {
        using var engine = new FakeOcrEngine(bitmap =>
            bitmap.Width == 300
                ? [new OcrTextBlock("横書", new Rect(20, 20, 80, 20), Confidence: 0.93)]
                : [new OcrTextBlock("日本語", new Rect(30, 30, 100, 20), Confidence: 0.89)]);
        using var bitmap = new Bitmap(300, 200);

        var blocks = await MixedOrientationOcr.RecognizeAsync(
            engine, bitmap, "JA", CancellationToken.None);

        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, block => block.Text == "横書");
        var vertical = Assert.Single(blocks, block => block.Text == "日本語");
        Assert.Equal(new Rect(250, 30, 20, 100), vertical.Bounds);
        Assert.Equal(2, engine.CallCount);
    }

    [Fact]
    public async Task RecognizeAsync_WhenVerticalPassFails_ReturnsHorizontalResult()
    {
        using var engine = new FakeOcrEngine(bitmap =>
        {
            if (bitmap.Width != 300)
                throw new InvalidOperationException("rotated pass failed");

            return [new OcrTextBlock("横書", new Rect(20, 20, 80, 20))];
        });
        using var bitmap = new Bitmap(300, 200);

        var blocks = await MixedOrientationOcr.RecognizeAsync(
            engine, bitmap, "JA", CancellationToken.None);

        Assert.Equal("横書", Assert.Single(blocks).Text);
        Assert.Equal(2, engine.CallCount);
    }

    [Fact]
    public async Task RecognizeAsync_NonCjkLanguageRunsOnlyOriginalPass()
    {
        using var engine = new FakeOcrEngine(_ =>
            [new OcrTextBlock("English", new Rect(20, 20, 80, 20))]);
        using var bitmap = new Bitmap(300, 200);

        var blocks = await MixedOrientationOcr.RecognizeAsync(
            engine, bitmap, "EN", CancellationToken.None);

        Assert.Equal("English", Assert.Single(blocks).Text);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task RecognizeAsync_CjkHorizontalTextIsNotAddedAgainFromRotatedPass()
    {
        using var engine = new FakeOcrEngine(bitmap =>
            bitmap.Width == 300
                ? [new OcrTextBlock("日本語横書き", new Rect(20, 20, 120, 20))]
                : [new OcrTextBlock("日本語横書き", new Rect(20, 30, 20, 120))]);
        using var bitmap = new Bitmap(300, 200);

        var blocks = await MixedOrientationOcr.RecognizeAsync(
            engine, bitmap, "JA", CancellationToken.None);

        var block = Assert.Single(blocks);
        Assert.Equal("日本語横書き", block.Text);
        Assert.Equal(new Rect(20, 20, 120, 20), block.Bounds);
        Assert.Equal(2, engine.CallCount);
    }

    [Fact]
    public async Task OcrService_RecognizesGeneratedVerticalJapaneseWithoutDetectedUprightCandidates()
    {
        using var bitmap = new Bitmap(120, 360);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(
                   "Yu Gothic UI",
                   44,
                   System.Drawing.FontStyle.Regular,
                   GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            const string text = "日本語縦書き";
            for (var index = 0; index < text.Length; index++)
                graphics.DrawString(text[index].ToString(), font, Brushes.Black, 25, 8 + index * 50);
        }

        using var service = new OcrService();
        var blocks = await service.RecognizeAsync(bitmap, "JA");
        var recognized = string.Concat(blocks.Select(block => block.Text.Replace(" ", "")));

        Assert.Contains("日本語", recognized);
        Assert.Contains(blocks, block => block.Bounds.Height > block.Bounds.Width * 1.5);
    }

    private sealed class FakeOcrEngine(Func<Bitmap, List<OcrTextBlock>> recognize) : IOcrEngine
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<List<OcrTextBlock>> RecognizeAsync(
            Bitmap bitmap,
            string sourceLanguage,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(recognize(bitmap));
        }

        public Task<List<OcrTextBlock>?> TryRecognizeAsync(
            Bitmap bitmap,
            string sourceLanguage,
            int? maxDetectSize = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<List<OcrTextBlock>?>(recognize(bitmap));

        public void Dispose()
        {
        }
    }
}
