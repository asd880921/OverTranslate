using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using OverTranslate.Services.Ocr;

namespace OcrHarness;

/// <summary>
/// A fixed set of text lines drawn at known fonts, sizes and shapes, each one kept together with
/// the two boxes that can be measured off the drawing itself.
/// </summary>
/// <remarks>
/// <para>Written for the glyph height comparison, whose first requirement is that the three kinds
/// of box never get confused with one another. A <b>layout box</b> is what the typesetter says the
/// line occupies: the advance width and the font's own line height, so it moves with the font's
/// metrics and not with which letters were asked for. An <b>ink box</b> is the rectangle the marks
/// actually landed in, so a line of "illi" and a line of "gjpqy" at one size give different ones. A
/// <b>detection box</b> is neither, and does not exist here at all — it only appears once a
/// rendered sheet has been through OCR, which is the other half of this file's job.</para>
///
/// <para>So the difference between two estimates measured off this set is not attributable to the
/// formula alone: font and string shape are inside it. The formula on its own is what
/// <c>--estimate-precision</c> answers, on boxes typed in by hand.</para>
/// </remarks>
internal static class GlyphSampleSheets
{
    /// <summary>One line to draw, and everything known about it before it is drawn.</summary>
    internal sealed record Sample(
        string Id,
        string Font,
        int SizePx,
        OcrLayoutScript Script,
        string Content,
        int Glyphs,
        string Text);

    /// <summary>Where one drawn line ended up, in the sheet's pixels.</summary>
    /// <param name="LayoutBox">Advance width by the font's line height, from the origin it was drawn at.</param>
    /// <param name="InkBox">The bounding rectangle of the pixels that were actually darkened.</param>
    internal sealed record Placed(
        Sample Sample,
        string Sheet,
        System.Windows.Rect LayoutBox,
        System.Windows.Rect InkBox,
        bool InkFound);

    /// <summary>Widest sheet allowed. Long lines exist to be measured, so this is generous.</summary>
    private const int MaxSheetWidth = 3600;

    /// <summary>Tallest sheet allowed, so no sheet is downscaled far harder than a real capture.</summary>
    private const int MaxSheetHeight = 1400;

    private const int Margin = 30;

    /// <summary>Anything darker than this on the white ground counts as a mark.</summary>
    private const int InkLuminance = 200;

    private static readonly int[] Sizes = [12, 14, 16, 20, 28, 40];

    /// <summary>Glyph counts every content class is drawn at.</summary>
    private static readonly int[] Lengths = [1, 2, 3, 4, 5, 8, 12, 20, 40];

    /// <summary>
    /// The long end. A five-glyph line was measured against a 140-glyph one on the capture that
    /// started this, so a set that stops at 40 cannot say anything about the pair that matters.
    /// </summary>
    private static readonly int[] LongLengths = [80, 140];

    /// <summary>
    /// Cycled to the glyph count asked for. Trailing spaces are part of the pattern: without them
    /// a 140-glyph line is one unbroken word, which is a width nothing on a real page produces.
    /// </summary>
    private static readonly (string Content, string Pattern, bool Long)[] LatinClasses =
    [
        ("narrow", "illi ", true),
        ("wide", "WMWM ", true),
        ("upper", "ABCDE ", false),
        ("descender", "gjpqy ", false),
        ("digit", "01234 ", false),
        ("prose", "The quick brown fox jumps over the lazy dog and the print cost. ", true),
        ("punct", "cost, price: item; end. Are you sure? ", false),
    ];

    private static readonly (string Content, string Pattern)[] CjkClasses =
    [
        ("han", "這是一段用來量測字高估值的中文測試文字內容範例"),
        ("kana", "これはじこうすいていちをはかるためのてすとぶんしょうです"),
        ("cjkpunct", "「設定」、「表示」。それから、ほか。"),
    ];

    private static readonly (string Content, string Pattern)[] MixedClasses =
    [
        ("mixed_title", "BanG Dream! アニメ、ゲーム、コミック "),
        ("mixed_prose", "CSS アンカー位置指定の使用 guide 2005 年から "),
    ];

    /// <summary>Punctuation with no letter in it, which has no script and so gets no estimate.</summary>
    private const string PunctOnlyPattern = ".,;:!? ";

    private static readonly string[] LatinFonts = ["Segoe UI", "Consolas"];
    private static readonly string[] CjkFonts = ["Microsoft JhengHei", "MS Gothic"];
    private const string MixedFont = "Microsoft JhengHei";

    /// <summary>
    /// Every sample in the set, in a fixed order, so two runs of this produce the same ids for the
    /// same lines and the manifests can be diffed.
    /// </summary>
    internal static List<Sample> BuildSamples()
    {
        var samples = new List<Sample>();

        foreach (var font in LatinFonts)
        foreach (var size in Sizes)
        {
            foreach (var (content, pattern, allowsLong) in LatinClasses)
            {
                foreach (var glyphs in Lengths)
                    samples.Add(Make(font, size, OcrLayoutScript.Latin, content, glyphs, pattern));

                if (!allowsLong)
                    continue;

                foreach (var glyphs in LongLengths)
                    samples.Add(Make(font, size, OcrLayoutScript.Latin, content, glyphs, pattern));
            }

            // Unknown, not Latin: nothing here reads as a letter or a digit, so the estimate
            // declines it the same way it declines Mixed. Kept in the set because the coverage
            // figure has to count what gets no estimate, not quietly drop it.
            foreach (var glyphs in new[] { 1, 2, 4 })
                samples.Add(Make(font, size, OcrLayoutScript.Unknown, "punctonly", glyphs, PunctOnlyPattern));
        }

        foreach (var font in CjkFonts)
        foreach (var size in Sizes)
        {
            foreach (var (content, pattern) in CjkClasses)
            foreach (var glyphs in Lengths)
                samples.Add(Make(font, size, OcrLayoutScript.Cjk, content, glyphs, pattern));

            samples.Add(Make(font, size, OcrLayoutScript.Cjk, "han", 80, CjkClasses[0].Pattern));
        }

        foreach (var size in Sizes)
        foreach (var (content, pattern) in MixedClasses)
        foreach (var glyphs in new[] { 4, 8, 12, 20, 40 })
            samples.Add(Make(MixedFont, size, OcrLayoutScript.Mixed, content, glyphs, pattern));

        return samples;
    }

    private static Sample Make(string font, int size, OcrLayoutScript script, string content, int glyphs, string pattern)
    {
        var text = TextOf(pattern, glyphs);
        var id = $"{Slug(font)}-{size}-{content}-{glyphs}";

        // The script is what the text says it is, not what the class was filed under: a truncated
        // mixed pattern can be all Latin at n=4, and calling it Mixed would put a wrong label on
        // every row that used it.
        return new Sample(id, font, size, LayoutScriptDetection.For(text), content, glyphs, text);
    }

    /// <summary>The pattern cycled until it has contributed exactly <paramref name="glyphs"/> non-space characters.</summary>
    private static string TextOf(string pattern, int glyphs)
    {
        var builder = new StringBuilder();
        var taken = 0;
        var index = 0;

        while (taken < glyphs)
        {
            var c = pattern[index++ % pattern.Length];

            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                    builder.Append(c);
                continue;
            }

            builder.Append(c);
            taken++;
        }

        return builder.ToString().TrimEnd();
    }

    private static string Slug(string font) => font.Replace(" ", string.Empty).ToLowerInvariant();

    /// <summary>
    /// Draws the set onto sheets under <paramref name="outDir"/> and returns where every line
    /// landed. One font and size per sheet: the point of these is a controlled measurement, and a
    /// sheet mixing sizes cannot be read at one detector scale without that being a variable too.
    /// </summary>
    internal static List<Placed> Render(string outDir, IReadOnlyList<Sample> samples, TextWriter log)
    {
        Directory.CreateDirectory(outDir);
        var placed = new List<Placed>();

        foreach (var group in samples.GroupBy(s => (s.Font, s.SizePx)))
        {
            using var probe = new Bitmap(1, 1);
            using var probeGraphics = Graphics.FromImage(probe);
            probeGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Asked for by family rather than by passing the name to Font: GDI+ substitutes a
            // default silently, and a sheet labelled "Consolas" that is really Segoe UI would be
            // read as a font effect that is not there. FontFamily throws instead of substituting.
            // The name it reports back is the localised one on a Chinese Windows, so it is the
            // construction that is the check, never a comparison of the two names.
            FontFamily family;
            try
            {
                family = new FontFamily(group.Key.Font);
            }
            catch (ArgumentException)
            {
                log.WriteLine($"(font missing) {group.Key.Font}; skipped");
                continue;
            }

            using var font = new Font(family, group.Key.SizePx, FontStyle.Regular, GraphicsUnit.Pixel);

            var lineHeight = font.GetHeight(probeGraphics);
            var pitch = (int)Math.Ceiling(lineHeight + Math.Max(10, group.Key.SizePx * 1.2));

            var measured = group
                .Select(sample => (Sample: sample,
                    Width: (double)probeGraphics.MeasureString(sample.Text, font, PointF.Empty, StringFormat.GenericTypographic).Width))
                .ToList();

            var rowsPerSheet = Math.Max(1, (MaxSheetHeight - Margin * 2) / pitch);

            for (var start = 0; start < measured.Count; start += rowsPerSheet)
            {
                var rows = measured.Skip(start).Take(rowsPerSheet).ToList();
                var width = (int)Math.Min(MaxSheetWidth, Math.Max(640, Math.Ceiling(rows.Max(r => r.Width)) + Margin * 2));
                var height = Margin * 2 + pitch * rows.Count;
                var name = $"{Slug(group.Key.Font)}-{group.Key.SizePx}-{start / rowsPerSheet:00}.png";

                using var sheet = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(sheet))
                {
                    graphics.Clear(Color.White);
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    for (var row = 0; row < rows.Count; row++)
                    {
                        graphics.DrawString(
                            rows[row].Sample.Text, font, Brushes.Black,
                            new PointF(Margin, Margin + pitch * row), StringFormat.GenericTypographic);
                    }
                }

                for (var row = 0; row < rows.Count; row++)
                {
                    var top = Margin + pitch * row;
                    var ink = InkBoxIn(sheet, top, Math.Min(top + pitch, height));

                    placed.Add(new Placed(
                        rows[row].Sample,
                        name,
                        new System.Windows.Rect(Margin, top, rows[row].Width, lineHeight),
                        ink ?? default,
                        ink is not null));
                }

                sheet.Save(Path.Combine(outDir, name), ImageFormat.Png);
            }
        }

        return placed;
    }

    /// <summary>The rectangle of darkened pixels within one row band, or null where nothing was drawn.</summary>
    private static System.Windows.Rect? InkBoxIn(Bitmap sheet, int top, int bottom)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        var data = sheet.LockBits(
            new Rectangle(0, top, sheet.Width, bottom - top), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            var buffer = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (var y = 0; y < data.Height; y++)
            {
                var scan = y * data.Stride;

                for (var x = 0; x < data.Width; x++)
                {
                    var b = buffer[scan + x * 3];
                    var g = buffer[scan + x * 3 + 1];
                    var r = buffer[scan + x * 3 + 2];

                    if ((r * 299 + g * 587 + b * 114) / 1000 >= InkLuminance)
                        continue;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        finally
        {
            sheet.UnlockBits(data);
        }

        if (minX > maxX)
            return null;

        // Inclusive pixel indices, so the extent is one more than the difference.
        return new System.Windows.Rect(minX, top + minY, maxX - minX + 1, maxY - minY + 1);
    }

    internal static readonly string[] ManifestHeader =
    [
        "id", "sheet", "font", "sizePx", "script", "content", "glyphs", "text",
        "layoutX", "layoutY", "layoutW", "layoutH",
        "inkX", "inkY", "inkW", "inkH", "inkFound",
    ];

    internal static string ManifestLine(Placed placed) => string.Join('\t',
        placed.Sample.Id, placed.Sheet, placed.Sample.Font,
        placed.Sample.SizePx.ToString(CultureInfo.InvariantCulture), placed.Sample.Script,
        placed.Sample.Content, placed.Sample.Glyphs.ToString(CultureInfo.InvariantCulture),
        placed.Sample.Text,
        F(placed.LayoutBox.X), F(placed.LayoutBox.Y), F(placed.LayoutBox.Width), F(placed.LayoutBox.Height),
        F(placed.InkBox.X), F(placed.InkBox.Y), F(placed.InkBox.Width), F(placed.InkBox.Height),
        placed.InkFound);

    /// <summary>One manifest row read back, for the OCR half to work from.</summary>
    internal static Placed ParseManifestLine(string line)
    {
        var f = line.Split('\t');
        var sample = new Sample(
            f[0], f[2], int.Parse(f[3], CultureInfo.InvariantCulture),
            Enum.Parse<OcrLayoutScript>(f[4]), f[5], int.Parse(f[6], CultureInfo.InvariantCulture), f[7]);

        return new Placed(
            sample, f[1],
            new System.Windows.Rect(D(f[8]), D(f[9]), D(f[10]), D(f[11])),
            new System.Windows.Rect(D(f[12]), D(f[13]), D(f[14]), D(f[15])),
            bool.Parse(f[16]));
    }

    /// <summary>Full precision, because a boundary this set exists to find sits at the fourth decimal.</summary>
    internal static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static double D(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// The estimate for one box, straight out of the production function, with the trace it filled
    /// in on the way. Nothing here recomputes any part of the formula.
    /// </summary>
    internal static string EstimateColumns(OcrLayoutScript script, System.Windows.Rect box, string text)
    {
        var height = OnnxOcrEngine.LayoutGlyphHeightFor(script, box, text, out var trace);

        return string.Join('\t',
            height is { } value ? F(value) : "null",
            trace.Source,
            F(trace.BoxEstimate),
            trace.PitchCandidate is { } candidate ? F(candidate) : "null",
            F(trace.WidthMinusTwiceHeight),
            trace.PitchBranchEntered,
            trace.PitchSelected,
            trace.ShortTextSelected);
    }

    internal static readonly string[] EstimateHeader =
    [
        "est", "src", "boxEst", "pitchCand", "wMinus2h", "branch", "pitchWon", "shortWon",
    ];
}
