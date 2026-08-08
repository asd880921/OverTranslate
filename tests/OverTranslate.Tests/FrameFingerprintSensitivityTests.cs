using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using OverTranslate.Services.Realtime;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

/// <summary>
/// The two thresholds in <see cref="FrameFingerprint"/> against rendered frames rather than
/// hand-built cell arrays, because what they have to separate is a subtitle changing from a scene
/// getting brighter behind one — and only pixels can say how far apart those really are.
/// </summary>
public class FrameFingerprintSensitivityTests(ITestOutputHelper output)
{
    private static readonly Rectangle Band = new(0, 60, 1226, 90);

    private const string Line = "Marina-san, are you okay?";

    [Theory]
    [InlineData("I know, right?", "換成短句")]
    [InlineData("You seem rather dispirited.", "換成等長的另一句")]
    [InlineData("", "字幕消失")]
    public void ReplacingTheSubtitleIsSeen(string replacement, string what)
    {
        var (before, after) = Fingerprints(Line, replacement);

        output.WriteLine($"{what}: {after.ChangedShare(before, 16):0.0%} of cells");
        Assert.True(after.Differs(before));
    }

    [Theory]
    // A scene brightening behind an unchanged subtitle. At the old tolerance of 12 the 16-level
    // step moved 84.8% of the cells — more than replacing the subtitle does — and every one of
    // these recognised the same words over again.
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void TheSceneGettingBrighterBehindAnUnchangedSubtitleIsNot(int levels)
    {
        using var still = Frame(Line);
        using var brighter = Frame(Line, backgroundShift: levels);

        var before = FrameFingerprint.Capture(still, [Band]);
        var after = FrameFingerprint.Capture(brighter, [Band]);

        output.WriteLine($"+{levels} levels: {after.ChangedShare(before, 16):0.0%} of cells");
        Assert.False(after.Differs(before));
    }

    [Fact]
    public void ARealChangeClearsTheBarWithRoomToSpare()
    {
        // The margin is the point: a threshold that only just separates the two would be one
        // rendering quirk away from doing neither job.
        var (before, after) = Fingerprints(Line, "You seem rather dispirited.");

        Assert.True(after.ChangedShare(before, 16) > FrameFingerprint.ChangedCellPercent / 100.0 * 2);
    }

    private static (FrameFingerprint Before, FrameFingerprint After) Fingerprints(string a, string b)
    {
        using var first = Frame(a);
        using var second = Frame(b);
        return (FrameFingerprint.Capture(first, [Band]), FrameFingerprint.Capture(second, [Band]));
    }

    private static Bitmap Frame(string text, int backgroundShift = 0)
    {
        var bitmap = new Bitmap(1226, 196);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(40 + backgroundShift, 44 + backgroundShift, 52 + backgroundShift));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (text.Length > 0)
        {
            using var font = new Font("Segoe UI", 46, FontStyle.Bold, GraphicsUnit.Pixel);
            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)FontStyle.Bold, 46,
                new PointF(120, 78), StringFormat.GenericTypographic);
            using var outline = new Pen(Color.Black, 4) { LineJoin = LineJoin.Round };
            graphics.DrawPath(outline, path);
            graphics.FillPath(Brushes.White, path);
        }

        return bitmap;
    }
}
