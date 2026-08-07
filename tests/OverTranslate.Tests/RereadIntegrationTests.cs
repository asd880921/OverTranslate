using System.Drawing;
using OverTranslate.Services;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

public class RereadIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AKnownCaptureStillReadsTheSameWithTheRereadPathInPlace()
    {
        using var ocr = new OcrService();
        using var capture = Load("capture-270x60.png");

        var blocks = await ocr.RecognizeAsync(capture, "EN");
        var text = string.Join(" ", blocks.Select(b => b.Text)).ToLowerInvariant();

        output.WriteLine(text);
        Assert.Contains("skill(domain-modeling)", text);
    }

    [Fact]
    public async Task ALineInACaptureLargeEnoughToBeDownscaledIsStillRead()
    {
        // A capture wide enough that the whole thing is downscaled on the way to the detector,
        // which is where large captures lose their short axis. Pins the screenshot path end to end
        // with the re-read in place; it does not exercise the re-read, because this line is read
        // confidently the first time.
        using var ocr = new OcrService();
        using var subtitle = Load("subtitle-over-light-floor-1226x196.png");
        using var capture = OnALargeCanvas(subtitle, 2400, 1200);

        var blocks = await ocr.RecognizeAsync(capture, "EN");
        var text = string.Join(" ", blocks.Select(b => b.Text));

        output.WriteLine($"{blocks.Count} block(s): {text}");
        foreach (var block in blocks)
            output.WriteLine($"  {block.Confidence:0.00}  {block.Text}");

        Assert.Contains("okay", text, StringComparison.OrdinalIgnoreCase);
    }

    private static Bitmap Load(string name) =>
        new(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Bitmap OnALargeCanvas(Bitmap content, int width, int height)
    {
        var canvas = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.FromArgb(30, 30, 34));
        graphics.DrawImageUnscaled(content, (width - content.Width) / 2, (height - content.Height) / 2);
        return canvas;
    }
}
