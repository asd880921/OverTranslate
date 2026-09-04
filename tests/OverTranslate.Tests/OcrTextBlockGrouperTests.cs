using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class OcrTextBlockGrouperTests
{
    [Fact]
    public void GroupsAdjacentAlignedLinesIntoSingleBlock()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家", new Rect(10, 40, 110, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal(2, grouped[0].Lines.Count);
        Assert.Equal("寧夏夜市美食攻略 食尚玩家", grouped[0].Text);
    }

    [Fact]
    public void KeepsDifferentColumnsSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("LEFT", new Rect(10, 10, 100, 20)),
            new("RIGHT", new Rect(220, 38, 110, 20)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsDifferentFontSizesSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("TITLE", new Rect(10, 10, 180, 28)),
            new("summary", new Rect(10, 48, 180, 16)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsTitleAndBodySeparateWhenHeightsAreCloseButDistinct()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("TITLE", new Rect(10, 10, 220, 28)),
            new("body text", new Rect(10, 44, 220, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void MergesAdjacentFragmentsOnTheSameLine()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏", new Rect(10, 10, 42, 24)),
            new("夜市", new Rect(58, 10, 42, 24)),
            new("攻略", new Rect(106, 10, 42, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("寧夏夜市攻略", grouped[0].Text);
    }

    [Fact]
    public void MergesPerWordBoxesOnOneLineWhenBoxTopsVaryFromAscendersAndDescenders()
    {
        // Real geometry: a single line the detector split into per-word boxes whose tops vary
        // (32..37) with ascenders/descenders. A Y-primary sort interleaves them, so the words must
        // be re-merged by reading order into one line instead of separate translations.
        var blocks = new List<OcrTextBlock>
        {
            new("Send", new Rect(0, 32, 132, 59)),
            new("and", new Rect(1082, 34, 100, 59)),
            new("terminal", new Rect(1475, 34, 246, 57)),
            new("this", new Rect(150, 35, 134, 57)),
            new("the", new Rect(632, 35, 101, 56)),
            new("background", new Rect(753, 35, 309, 58)),
            new("free", new Rect(1203, 35, 130, 58)),
            new("the", new Rect(1353, 35, 101, 56)),
            new("session", new Rect(302, 36, 221, 54)),
            new("to", new Rect(539, 37, 72, 54)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("Send this session to the background and free the terminal", grouped[0].Text);
    }

    [Fact]
    public void MergesHeadingWordBoxesThatOverlapHorizontallyFromUnclipExpansion()
    {
        // Real geometry from a large EN capture: the detector's unclip expansion enlarges the big
        // heading word-boxes until they overlap horizontally ("Translate" right edge 533 vs
        // "your website" left edge 515 → gap -18). They must still merge into one line instead of
        // being translated separately as "翻譯" + "你的 網站".
        var blocks = new List<OcrTextBlock>
        {
            new("Translate", new Rect(136, 68, 397, 99)),
            new("your website", new Rect(515, 71, 565, 102)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("Translate your website", grouped[0].Text);
    }

    [Fact]
    public void MergesShortMidLineWordThatIsFullyNestedInTheLine()
    {
        // Real geometry from a captured line: the short word "to" (h=25) is shorter than its
        // neighbours (h=31) so heightRatio is only ~0.81, but it sits on the same baseline (fully
        // nested, vertical overlap ~1.0) and must not be dropped out of the middle of the line.
        var blocks = new List<OcrTextBlock>
        {
            new("session", new Rect(120, 8, 95, 31)),
            new("to", new Rect(215, 11, 30, 25)),
            new("the", new Rect(250, 8, 46, 31)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
        Assert.Equal("session to the", grouped[0].Text);
    }

    [Fact]
    public void KeepsTwoDistinctSameRowButtonsSeparate()
    {
        // Real geometry: two distinct buttons on the same row — "Download key-level usage report"
        // (h=32) and a shorter/taller, vertically offset "Create key" (h=38). heightRatio 0.84 and
        // vertical overlap 0.47 must keep them apart; over-merging them was a regression.
        var blocks = new List<OcrTextBlock>
        {
            new("Download key-level usage report", new Rect(1293, 321, 305, 32)),
            new("Create key", new Rect(1632, 298, 112, 38)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsSeparatePhrasesOnTheSameLineWhenGapIsLarge()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("左側", new Rect(10, 10, 42, 24)),
            new("右側", new Rect(140, 10, 42, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void GroupsWrappedSentenceWhenOpeningQuoteContinuesOnNextLine()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Poppin'Party×Roselia 合同ライブ「DREAMS", new Rect(10, 10, 330, 24)),
            new("GO ON」のセットリストプレイリストを公開！", new Rect(10, 40, 290, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Single(grouped);
    }

    [Fact]
    public void KeepsAlignedIndependentLinesSeparateWhenWidthsAreSimilar()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家最新整理", new Rect(10, 40, 210, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsNextSentenceSeparateAfterSentenceTerminator()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("第一句已經結束。", new Rect(10, 10, 220, 24)),
            new("第二句重新開始", new Rect(10, 40, 110, 24)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void AWordWithNoDescenderIsStillPartOfItsLine()
    {
        // Measured: "Let's pay CiRCLE a visit on the way home." came back as these two boxes, a
        // height ratio of 0.58, 2px apart, the shorter one entirely inside the taller one's rows.
        // Split, "home" was translated on its own and drawn as a stray word beside a sentence with
        // its ending missing.
        var blocks = new List<OcrTextBlock>
        {
            new("Let's pay CiRCLE a visit on the way", new Rect(80, 78, 888, 88)),
            new("home.", new Rect(970, 106, 141, 51)),
        };

        var merged = Assert.Single(GroupDetected(blocks));
        Assert.Equal("Let's pay CiRCLE a visit on the way home.", merged.Text);
    }

    [Fact]
    public void StackedControlsOfSimilarHeightAreStillKeptApart()
    {
        // What the height guard was protecting, and why loosening it is safe: these overlap
        // vertically by about half, which the overlap test rejects on its own.
        var blocks = new List<OcrTextBlock>
        {
            new("Download report", new Rect(10, 10, 200, 32)),
            new("Create key", new Rect(215, 27, 160, 38)),
        };

        Assert.Equal(2, GroupDetected(blocks).Count);
    }

    [Fact]
    public void AMergedLineIsAsConfidentAsItsTextDeserves()
    {
        // Weighted by characters, not by fragment: a confidently-read line with a doubtful two-
        // character fragment beside it is still a confidently-read line, and a plain average would
        // make the score depend on where the detector happened to split it.
        var blocks = new List<OcrTextBlock>
        {
            new("Marina-san, are you okay?", new Rect(10, 10, 250, 24), Confidence: 1.0),
            new("!!", new Rect(266, 10, 20, 24), Confidence: 0.6),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.NotNull(merged.Confidence);
        Assert.InRange(merged.Confidence!.Value, 0.96, 1.0);
    }

    [Fact]
    public void ALineWithNoScoresCarriesNoConfidence()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("第一段文字", new Rect(10, 10, 100, 24)),
            new("第二段文字", new Rect(112, 10, 100, 24)),
        };

        var merged = Assert.Single(GroupDetected(blocks));
        Assert.Null(merged.Confidence);
    }

    /// <summary>
    /// The real bounds of a game panel whose description wraps onto a second line. The detector's
    /// unclip expansion leaves the two boxes overlapping by 3px, and while a `verticalGap &lt; 0`
    /// guard stood there they were translated apart — "…remaining in the" came back as 「每發子彈在」
    /// and "magazine" as 「雜誌」. See issue #74.
    /// </summary>
    [Fact]
    public void GroupsAWrappedLineWhoseBoxesOverlapByTheDetectorsExpansion()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Shots deal more damage for each bullet remaining in the", new Rect(700, 280, 570, 34)),
            new("magazine", new Rect(698, 311, 109, 31)),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(
            "Shots deal more damage for each bullet remaining in the magazine", merged.Text);
    }

    /// <summary>
    /// The real shape of the MAG SHOT panel: "magazine" has no ascender, so its box came back 26px
    /// under the 30px line it wraps from — 0.867, refused by 0.013 — while the letters themselves
    /// are all but the same size. Comparing the glyph heights the engine reports instead reads
    /// 0.937 and the line survives. See issue #79.
    /// </summary>
    [Fact]
    public void GroupsAWrappedLineWhoseBoxIsShortForWantOfAnAscender()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Shots deal more damage for each bullet remaining in the",
                new Rect(111, 78, 566, 30), RenderGlyphHeight: 16),
            new("magazine", new Rect(112, 108, 105, 26), RenderGlyphHeight: 15),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(
            "Shots deal more damage for each bullet remaining in the magazine", merged.Text);
    }

    /// <summary>
    /// And the case that keeps the swap honest: boxes near enough in height to pass the old test,
    /// holding text of visibly different sizes. A save-slot stamp over a character name did this —
    /// boxes 0.83 apart, letters 0.58. Reading the glyphs is what tells them apart.
    /// </summary>
    /// <remarks>
    /// The widths are what make the glyph heights come out at the measured 20 and 11.7: the engine
    /// does not measure ink, it estimates it from the average glyph pitch, so a box has to be as
    /// wide as the text in it was. They were 300 and 90 while this fixture set the glyph heights by
    /// hand, and those boxes yield 16.96 and 11.70 — a ratio of 0.98, which is to say the pair the
    /// test is built on would have merged in the app whatever this assertion said.
    /// </remarks>
    [Fact]
    public void KeepsLinesOfDifferentTextSizesApartEvenWhenTheirBoxesMatch()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("2026/07/24 23:29 AUTO SAVE", new Rect(40, 40, 354, 35), RenderGlyphHeight: 20),
            new("Narmaya", new Rect(41, 79, 63, 29), RenderGlyphHeight: 11.7),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// A stack of menu entries has the shape the width rule looks for — a longer label above a
    /// shorter one, aligned, evenly spaced — without being a sentence at all. These are the real
    /// bounds of 「キャラクター強化」over「所持品」in a game menu; joining them handed the translator
    /// one invented phrase and squeezed both labels into a single bubble. See issue #75.
    /// </summary>
    [Fact]
    public void KeepsStackedMenuLabelsSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("キャラクター強化", new Rect(166, 476, 233, 36)),
            new("所持品", new Rect(165, 539, 94, 38)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// And what that must not cost: a real wrapped sentence, whose first line is long because it
    /// ran out of room. Real bounds from the same corpus as the menu above.
    /// </summary>
    [Fact]
    public void StillGroupsASentenceLongEnoughToHaveWrapped()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("그랑 사이퍼의 갑판에 있는 연습용 더미를 조사하면", new Rect(354, 427, 564, 30)),
            new("플레이할 수 있습니다", new Rect(355, 478, 248, 29)),
        };

        var grouped = GroupDetected(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(2, merged.Lines.Count);
    }

    /// <summary>
    /// What the tolerance above must not let through: two boxes sharing a row overlap almost
    /// completely, and joining them as though the second wrapped from the first would read a button
    /// as the continuation of its neighbour. Far enough apart that the same-line merge does not take
    /// them first, so this reaches the next-line test the way the real ones did.
    /// </summary>
    [Fact]
    public void KeepsBoxesThatShareARowOutOfTheNextLineJoin()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("PICK UP", new Rect(10, 360, 67, 25)),
            new("HOLD TO SALVAGE", new Rect(20, 361, 160, 25)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(2, grouped.Count);
    }

    private static List<OcrTextBlock> GroupDetected(IReadOnlyList<OcrTextBlock> blocks) =>
        OcrTextBlockGrouper.Group(blocks.AsDetected());

    /// <summary>
    /// Symptom B: two lines whose detection boxes are exactly the same size, one Latin and one CJK.
    /// </summary>
    /// <remarks>
    /// The size test used to compare normalised boxes, and a CJK one is pulled in to 0.82 of its
    /// detection box while a Latin one is left whole — so this pair read 0.820 against a 0.88 gate
    /// and was refused before anything about the text, the spacing or the alignment was consulted.
    /// No mixed-script pair could ever pass it.
    /// </remarks>
    [Fact]
    public void CrossScriptPair_WithEqualDetectionBoxes_IsNotRefusedBySizeTest()
    {
        var latin = new OcrTextBlock("OPTIONS", new Rect(10, 10, 240, 30)).AsDetected();
        var cjk = new OcrTextBlock("ゲーム設定", new Rect(10, 10, 240, 30)).AsDetected();

        Assert.Equal(OcrLayoutScript.Latin, latin.LayoutScript);
        Assert.Equal(OcrLayoutScript.Cjk, cjk.LayoutScript);
        Assert.Equal(1.0, OcrTextBlockGrouper.TextSizeRatio(latin, cjk), precision: 9);

        // What it used to be: normalisation makes the same box 0.82 as tall on the CJK side.
        var normalizedCjk = OnnxOcrEngine.NormalizeBlocks([new("ゲーム設定", new Rect(10, 10, 240, 30))], useCjkRenderMetrics: true)[0];
        Assert.True(normalizedCjk.Bounds.Height / latin.Bounds.Height < 0.88);
    }

    /// <summary>
    /// A block that is itself both scripts has no single glyph body to measure, so it compares on
    /// the detection box like any other cross-script pairing.
    /// </summary>
    [Fact]
    public void MixedScriptBlock_UsesLayoutBoundsForSizeComparison()
    {
        var mixed = new OcrTextBlock("甲Glossaries", new Rect(10, 10, 240, 30)).AsDetected();
        var latin = new OcrTextBlock("Glossaries", new Rect(10, 50, 200, 30)).AsDetected();

        Assert.Equal(OcrLayoutScript.Mixed, mixed.LayoutScript);
        Assert.Null(mixed.LayoutGlyphHeight);
        Assert.Equal(1.0, OcrTextBlockGrouper.TextSizeRatio(mixed, latin), precision: 9);
    }

    /// <summary>
    /// The Latin-dominant case the same rule covers: a line of Japanese prose carrying Western
    /// terms must not be sized as though every glyph in it were full-width.
    /// </summary>
    [Fact]
    public void MixedScriptBlock_DoesNotUseCjkGlyphMetricForLatinDominantText()
    {
        var box = new Rect(10, 10, 600, 30);
        var mixed = new OcrTextBlock(
            "2005 年から CSS, HTML, JavaScript のドキュメントを作成しています。", box).AsDetected();
        var cjk = new OcrTextBlock("技術文書を書いています", box).AsDetected();

        Assert.Null(mixed.LayoutGlyphHeight);
        Assert.NotNull(cjk.LayoutGlyphHeight);
        Assert.NotEqual(cjk.LayoutGlyphHeight, mixed.LayoutGlyphHeight);

        // Same box, so the comparison is 1.0 — not the CJK glyph estimate measured off one of them.
        Assert.Equal(1.0, OcrTextBlockGrouper.TextSizeRatio(mixed, cjk), precision: 9);
    }

    /// <summary>
    /// 「OPTIONS／ゲーム設定」and 「Back／戻る」: each label is joined to its own translation
    /// candidate line, and the two pairs stay apart from each other.
    /// </summary>
    [Fact]
    public void Mixed_EnglishJapanese_DoesNotCrossMergeParagraphs()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("OPTIONS", new Rect(10, 10, 240, 30)),
            new("ゲーム設定", new Rect(10, 190, 240, 30)),
            new("Back", new Rect(10, 370, 140, 30)),
            new("戻る", new Rect(10, 550, 140, 30)),
        };

        var grouped = GroupDetected(blocks);

        Assert.Equal(4, grouped.Count);
    }
}
