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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// The real bounds of a documentation site's navigation bar. The detector returned all eleven
    /// entries correctly and separately; joining them handed the translator one string reading
    /// "Home Installation Quick Start PP-OCRv6 …" and drew it as a single bubble.
    /// </summary>
    [Fact]
    public void KeepsNavigationEntriesSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Home", new Rect(3, 18, 56, 25)),
            new("Installation", new Rect(75, 20, 87, 21)),
            new("Quick Start", new Rect(178, 20, 87, 24)),
            new("PP-OCRv6", new Rect(280, 20, 79, 21)),
            new("PP-StructureV3", new Rect(375, 20, 115, 21)),
            new("PP-ChatOCRv4", new Rect(506, 20, 113, 23)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(6, grouped.Count);
    }

    /// <summary>
    /// A row of toolbar buttons, which is the same shape at a smaller size — measured at 0.73 of a
    /// line apart, against the 0.38 the widest real word gap reaches.
    /// </summary>
    [Fact]
    public void KeepsToolbarButtonsSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("重新翻譯", new Rect(10, 100, 88, 22)),
            new("顯示原文", new Rect(114, 100, 88, 22)),
            new("截圖", new Rect(218, 100, 44, 22)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(3, grouped.Count);
    }

    /// <summary>
    /// Two cards side by side, sharing a row exactly because they are laid out to. Their headings
    /// are the same size and sit on the same baseline; only the space between them says they are
    /// two things.
    /// </summary>
    [Fact]
    public void KeepsCardsThatShareARowSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Intelligent document", new Rect(786, 137, 173, 28)),
            new("Certificate information", new Rect(989, 137, 188, 26)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// What none of that may cost: a row holding one line the detector split, where every gap is a
    /// word gap. Same geometry as the per-word test above, read as a whole row.
    /// </summary>
    [Fact]
    public void StillRebuildsALineTheDetectorSplitIntoWords()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("This is", new Rect(100, 40, 96, 30)),
            new("a very", new Rect(206, 40, 88, 30)),
            new("long sentence", new Rect(304, 41, 184, 29)),
        };

        var merged = Assert.Single(OcrTextBlockGrouper.Group(blocks));
        Assert.Equal("This is a very long sentence", merged.Text);
    }

    /// <summary>
    /// Two boxes cannot say which kind of space sits between them, so the fixed threshold decides —
    /// and at 0.13 of a line this is a word gap by any reading of it.
    /// </summary>
    [Fact]
    public void JoinsTwoBoxesAWordApartWithNothingElseToGoOn()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Hello", new Rect(10, 10, 70, 30)),
            new("World", new Rect(84, 10, 70, 30)),
        };

        var merged = Assert.Single(OcrTextBlockGrouper.Group(blocks));
        Assert.Equal("Hello World", merged.Text);
    }

    [Fact]
    public void KeepsSeparatePhrasesOnTheSameLineWhenGapIsLarge()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("左側", new Rect(10, 10, 42, 24)),
            new("右側", new Rect(140, 10, 42, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Single(grouped);
    }

    /// <summary>
    /// Similar widths are the ordinary shape of a paragraph, not evidence against one: every line
    /// but the last stops at the same wrap boundary. Set solid and sharing a left edge, these are
    /// one block of text.
    /// </summary>
    [Fact]
    public void GroupsSimilarWidthLinesThatAreSetSolid()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家最新整理", new Rect(10, 40, 210, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(2, merged.Lines.Count);
    }

    /// <summary>
    /// And what keeps that from swallowing anything that merely lines up: the same two lines spaced
    /// as separate items are. Nothing but the leading differs.
    /// </summary>
    [Fact]
    public void KeepsSimilarWidthLinesSeparateWhenTheyAreSpacedApart()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏夜市美食攻略", new Rect(10, 10, 220, 24)),
            new("食尚玩家最新整理", new Rect(10, 51, 210, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var merged = Assert.Single(OcrTextBlockGrouper.Group(blocks));
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

        Assert.Equal(2, OcrTextBlockGrouper.Group(blocks).Count);
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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var merged = Assert.Single(OcrTextBlockGrouper.Group(blocks));
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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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
                new Rect(111, 78, 566, 30), SourceGlyphHeight: 16),
            new("magazine", new Rect(112, 108, 105, 26), SourceGlyphHeight: 15),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(
            "Shots deal more damage for each bullet remaining in the magazine", merged.Text);
    }

    /// <summary>
    /// And the case that keeps the swap honest: boxes near enough in height to pass the old test,
    /// holding text of visibly different sizes. A save-slot stamp over a character name did this —
    /// boxes 0.83 apart, letters 0.58. Reading the glyphs is what tells them apart.
    /// </summary>
    [Fact]
    public void KeepsLinesOfDifferentTextSizesApartEvenWhenTheirBoxesMatch()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("2026/07/24 23:29 AUTO SAVE", new Rect(40, 40, 300, 35), SourceGlyphHeight: 20),
            new("Narmaya", new Rect(41, 79, 90, 29), SourceGlyphHeight: 11.6),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

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

        var grouped = OcrTextBlockGrouper.Group(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(2, merged.Lines.Count);
    }

    /// <summary>
    /// What the tolerance above must not let through: two boxes sharing a row overlap almost
    /// completely, and joining them as though the second wrapped from the first would read a button
    /// as the continuation of its neighbour. Far enough apart that the same-line merge does not take
    /// them first, so this reaches the next-line test the way the real ones did.
    /// </summary>
    /// <summary>
    /// The shape this whole path exists for: a dialogue line wrapped across three rows, none of
    /// which is much shorter than the one above it. Split, the translator sees three fragments and
    /// none of them carries the sentence the others needed.
    /// </summary>
    [Fact]
    public void GroupsAWrappedSubtitleWhoseLinesAreAllAboutAsLong()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("I never thought", new Rect(10, 100, 250, 30)),
            new("you would actually", new Rect(10, 133, 290, 30)),
            new("come back here.", new Rect(10, 166, 240, 30)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal("I never thought you would actually come back here.", merged.Text);
    }

    /// <summary>
    /// The same, in a script that fits six characters where English needs twenty, and without the
    /// punctuation to lean on. Japanese subtitles carry neither, so geometry has to answer alone.
    /// </summary>
    [Fact]
    public void GroupsAWrappedJapaneseSubtitleWithNoPunctuation()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("そんなことを", new Rect(400, 620, 180, 30)),
            new("言われても", new Rect(400, 653, 150, 30)),
            new("困るんだけど", new Rect(400, 686, 180, 30)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal(3, merged.Lines.Count);
    }

    /// <summary>
    /// Centred subtitles move their left edge by half of whatever the line lost, so measuring the
    /// left edge alone read these as unrelated columns and translated half a sentence twice.
    /// </summary>
    [Fact]
    public void GroupsACentredSubtitleWhoseLinesShareNoLeftEdge()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("I don't know", new Rect(200, 620, 200, 30)),
            new("what you're talking about.", new Rect(90, 653, 420, 30)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        var merged = Assert.Single(grouped);
        Assert.Equal("I don't know what you're talking about.", merged.Text);
    }

    /// <summary>
    /// A menu is aligned and evenly sized, and the only thing saying its entries are not a
    /// paragraph is that they were spaced on purpose. Merging them would hand the translator
    /// "New Game Settings Exit" and crush three bubbles into one.
    /// </summary>
    [Fact]
    public void KeepsMenuEntriesSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            // Spaced tightly enough to clear the general gap tolerance, so it is the leading rule
            // and not that one being asked the question. The pitch is the one measured on real
            // menus — 1.42 detection boxes between entries, against the 1.1 to 1.2 a paragraph
            // sets — because a number picked to sit just outside the threshold would stop
            // testing the shape and start testing the constant.
            new("New Game", new Rect(10, 100, 140, 30)),
            new("Settings", new Rect(10, 152, 130, 30)),
            new("Exit", new Rect(10, 204, 70, 30)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(3, grouped.Count);
    }

    /// <summary>
    /// The same menu read as Latin, where the detection box is left untrimmed and so the same
    /// entries sit a smaller fraction of a box apart.
    /// </summary>
    /// <remarks>
    /// Worth its own case because the leading rule divides by a box whose height depends on the
    /// script: a fraction that reads as a paragraph in one reads as a menu in the other, and
    /// getting that backwards is invisible in a corpus of one language.
    /// </remarks>
    [Fact]
    public void KeepsLatinMenuEntriesSeparate()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("New Game", new Rect(10, 100, 140, 30), null, SourceGlyphHeight: 20),
            new("Settings", new Rect(10, 145, 130, 30), null, SourceGlyphHeight: 20),
            new("Exit", new Rect(10, 190, 70, 30), null, SourceGlyphHeight: 20),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(3, grouped.Count);
    }

    /// <summary>
    /// Two columns of a stat panel, and what must never happen to them: no cell may be read as
    /// continuing the cell <em>beside</em> it. That is the failure this shape was written for —
    /// "Attack" reaching across to "Defense", or "120" to "98" — and neither column can see the
    /// other whatever order the blocks arrive in.
    /// </summary>
    /// <remarks>
    /// A label and the value under it are a different question, and this no longer asserts that they
    /// stay apart, because they no longer do: "Defense" over "98" is 0.43 of a line apart and
    /// aligned, which is the shape of wrapped text and is admitted as such. It used to be refused,
    /// but not by any rule here — the two columns interleave when sorted top to bottom, so "98" was
    /// only ever compared against "120" beside it and never against the label above it. Once a line
    /// is compared with the line genuinely above it (which is the whole of the multi-column fix),
    /// nothing in the geometry tells this pair from a two-line caption. Measured on nine real pages
    /// in three languages that is the cheaper error by a wide margin: the change makes 49 joins that
    /// were not being made, 38 of them wrapped sentences and 11 of them stacked labels like this
    /// one. It is the trade <see cref="OcrTextBlockGrouper"/> already states for stacked labels.
    /// </remarks>
    [Fact]
    public void KeepsTwoColumnStatsOutOfTheColumnBesideThem()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Attack", new Rect(10, 100, 100, 28)),
            new("Defense", new Rect(300, 100, 120, 28)),
            new("120", new Rect(10, 140, 60, 28)),
            new("98", new Rect(300, 140, 50, 28)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.All(grouped, group => Assert.DoesNotContain("Attack Defense", group.Text));
        Assert.All(grouped, group => Assert.DoesNotContain("120 98", group.Text));
        Assert.All(grouped, group => Assert.DoesNotContain("Attack 98", group.Text));
    }

    /// <summary>
    /// A list whose entries carry a byline set under them in smaller type, which is the shape of a
    /// news or forum front page. Entry titles are two rows apart, so a scan that looks past the
    /// group opened most recently can reach over the byline and read title 2 as the continuation of
    /// title 1 — measured on a Hacker News front page at 1600x1000, where twenty-two consecutive
    /// titles were chained into one "sentence" that way. A line continues the line directly above
    /// it, and a line standing in the space between two others says they are not that pair.
    /// </summary>
    [Fact]
    public void DoesNotReachOverALineToContinueTheOneAboveIt()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Fine, I'll build my own text editor", new Rect(10, 100, 400, 24)),
            new("55 points by gurjeet 3 hours ago", new Rect(10, 124, 200, 8)),
            new("Forgotten History of Small Nuclear", new Rect(10, 132, 380, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(3, grouped.Count);
    }

    /// <summary>
    /// Two lines of one wrapped label whose boxes overlap, which the detector's unclip expansion
    /// makes the ordinary case, with a taller box beside them reaching past both. There is no space
    /// between overlapping lines for anything to stand in, so nothing is in the way — but a span
    /// read from bottom to top is inverted, and a neighbour crossing it satisfies neither end and
    /// reads as though it were inside. Measured on a product architecture diagram, that refused
    /// every wrapped label on the page: 47 lines in, 47 groups out, and "AI financial" over
    /// "report analysis" never reached a verdict at all.
    /// </summary>
    [Fact]
    public void JoinsAWrappedLabelWhoseBoxesOverlapWithATallerBoxBesideIt()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("AI financial", new Rect(10, 100, 300, 30)),
            new("report analysis", new Rect(12, 128, 280, 30)),
            new("Apps", new Rect(330, 90, 150, 90)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
        Assert.Contains(grouped, group => group.Text == "AI financial report analysis");
    }

    /// <summary>
    /// A heading over the box it labels, with the geometry of a wrapped sentence: the heading fills
    /// its column, the line under it is shorter, and they are aligned. The real bounds and colours
    /// of "High Performance Serving" over "by just 1 command" on a product architecture diagram —
    /// blue on grey over black on white. Three more cells in the same row kept their heading and
    /// content apart, and this one merged, because its heading's words happened to be 1.42 times
    /// the width of its content and the rule that admits a shorter following line asks nothing else.
    /// </summary>
    [Fact]
    public void KeepsAHeadingOffTheContentBelowItWhenTheCaptureSaysTheyAreDifferentComponents()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("High Performance Serving", new Rect(1060, 630, 330, 30)),
            new("by just 1 command", new Rect(1090, 675, 232, 30)),
        };

        var appearance = new StubAppearance
        {
            [new Rect(1060, 630, 330, 30)] = (Background: Rgb(240, 240, 240), Foreground: Rgb(41, 50, 225)),
            [new Rect(1090, 675, 232, 30)] = (Background: Rgb(255, 255, 255), Foreground: Rgb(0, 0, 0)),
        };

        Assert.Equal(2, OcrTextBlockGrouper.Group(blocks, null, appearance).Count);
    }

    /// <summary>
    /// The case a leading threshold would have cost, and the reason colour was used instead. Real
    /// bounds of a Korean subtitle that wraps: its normalised leading is 0.71, because Hangul boxes
    /// sit tight on glyphs with no ascenders or descenders, so the same wrap measures looser than it
    /// would in Latin. One surface, one ink, so nothing visual argues against it and it stays whole.
    /// </summary>
    [Fact]
    public void StillGroupsAWrappedSubtitleWhoseLeadingIsLooseButWhoseInkIsUnchanged()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("그랑 사이퍼의 갑판에 있는 연습용 더미를 조사하면", new Rect(354, 427, 564, 30)),
            new("플레이할 수 있습니다", new Rect(355, 478, 248, 29)),
        };

        var appearance = new StubAppearance
        {
            [new Rect(354, 427, 564, 30)] = (Background: Rgb(18, 18, 20), Foreground: Rgb(255, 255, 255)),
            [new Rect(355, 478, 248, 29)] = (Background: Rgb(18, 18, 20), Foreground: Rgb(255, 255, 255)),
        };

        var merged = Assert.Single(OcrTextBlockGrouper.Group(blocks, null, appearance));
        Assert.Equal(2, merged.Lines.Count);
    }

    /// <summary>
    /// Wikipedia prose whose first line ends in a link, so the two lines' dominant ink differs by 68
    /// in CIELAB while they are one sentence over one white page. Colour is negative evidence about
    /// components, not about ink: without the background having changed too, a different ink means
    /// a link, a bold word or a highlighted term, and none of those ends a paragraph.
    /// </summary>
    [Fact]
    public void StillGroupsAParagraphWhoseLineEndsInALink()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("... that composer Marta Canales donated", new Rect(700, 500, 400, 22)),
            new("her pieces to a Carmelite monastery?", new Rect(700, 523, 230, 22)),
        };

        var appearance = new StubAppearance
        {
            [new Rect(700, 500, 400, 22)] = (Background: Rgb(255, 255, 255), Foreground: Rgb(51, 102, 204)),
            [new Rect(700, 523, 230, 22)] = (Background: Rgb(255, 255, 255), Foreground: Rgb(32, 33, 34)),
        };

        Assert.Single(OcrTextBlockGrouper.Group(blocks, null, appearance));
    }

    /// <summary>
    /// And the mirror of it: a poster's two lines over a photograph, where the surface behind each
    /// line is a different part of the picture — 99.7 apart — while both are set in the same white.
    /// The surface alone would have refused this one.
    /// </summary>
    [Fact]
    public void StillGroupsTwoLinesSetOverAPhotograph()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("誰かのために", new Rect(1082, 263, 123, 27)),
            new("あなたができること", new Rect(1083, 290, 162, 27)),
        };

        var appearance = new StubAppearance
        {
            [new Rect(1082, 263, 123, 27)] = (Background: Rgb(214, 186, 160), Foreground: Rgb(20, 20, 20)),
            [new Rect(1083, 290, 162, 27)] = (Background: Rgb(96, 74, 58), Foreground: Rgb(20, 20, 20)),
        };

        Assert.Single(OcrTextBlockGrouper.Group(blocks, null, appearance));
    }

    private static System.Windows.Media.Color Rgb(byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromRgb(r, g, b);

    /// <summary>
    /// The colours of a capture, given directly. The grouper asks an interface rather than a bitmap
    /// so that a test about what two lines look like can say what they look like, instead of
    /// rendering a picture and hoping it samples back the way it was drawn.
    /// </summary>
    private sealed class StubAppearance : IBlockAppearanceSource
    {
        private readonly Dictionary<Rect, BlockAppearance> _colors = [];

        public (System.Windows.Media.Color Background, System.Windows.Media.Color Foreground) this[Rect bounds]
        {
            set => _colors[bounds] = new BlockAppearance(value.Background, value.Foreground);
        }

        public BlockAppearance For(Rect bounds) => _colors[bounds];
    }

    /// <summary>
    /// And what that must not cost: the byline is only in the way of the lines it actually stands
    /// between. A second column beside a wrapped line is at the same height as it without being
    /// between anything, so only the width the two lines share is examined.
    /// </summary>
    [Fact]
    public void StillJoinsAWrappedLineWithABlockBesideItAtTheSameHeight()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("そんなことを言われても本当に", new Rect(10, 100, 280, 24)),
            new("困るんだけどまあ仕方ないか", new Rect(10, 128, 260, 24)),
            new("ログイン", new Rect(600, 114, 90, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
        Assert.Contains(grouped, group => group.Lines.Count == 2);
    }

    /// <summary>
    /// Taking whichever edge two lines share made the alignment test looser, and this is the shape
    /// that must not get through it: two blocks side by side on one row, centred on the same point
    /// because one sits above a wider one. They are a menu, not a sentence, and only the vertical
    /// gap stands between them — a row-mate is a whole line height above where a continuation would
    /// be, so it is refused before alignment is ever consulted.
    /// </summary>
    [Fact]
    public void KeepsSideBySideBlocksApartEvenWhenTheyShareACentre()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("SAVE", new Rect(600, 400, 90, 28)),
            new("LOAD GAME", new Rect(580, 402, 130, 28)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void KeepsBoxesThatShareARowOutOfTheNextLineJoin()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("PICK UP", new Rect(10, 360, 67, 25)),
            new("HOLD TO SALVAGE", new Rect(20, 361, 160, 25)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// One card's wrapped description, with nothing else on the screen. The control for the two
    /// tests below: the same card, the same geometry, and on its own it joins.
    /// </summary>
    [Fact]
    public void GroupsAWrappedCardDescriptionOnAPageOfOneColumn()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Google 翻譯是一個方便的工具，讓文", new Rect(10, 100, 300, 24)),
            new("字翻譯變得簡單。多語言支援", new Rect(10, 128, 280, 24)),
        };

        var merged = Assert.Single(OcrTextBlockGrouper.Group(blocks));
        Assert.Equal(2, merged.Lines.Count);
    }

    /// <summary>
    /// The same card with a second one beside it, which is the whole of the difference. Sorted top
    /// to bottom, two columns interleave — left first line, right first line, left second line,
    /// right second line — so the line above a continuation is never the one immediately before it
    /// in that order. Reported from a page of cards: descriptions that merge when the page holds
    /// one card stop merging once it holds several.
    /// </summary>
    [Fact]
    public void GroupsTheSameDescriptionWhenAnotherColumnSitsBesideIt()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Google 翻譯是一個方便的工具，讓文", new Rect(10, 100, 300, 24)),
            new("現在地區：阿富汗、阿爾巴尼亞、阿爾", new Rect(400, 100, 300, 24)),
            new("字翻譯變得簡單。多語言支援", new Rect(10, 128, 280, 24)),
            new("及利亞、屬薩摩亞、安道爾", new Rect(400, 128, 280, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(2, grouped.Count);
        Assert.All(grouped, group => Assert.Equal(2, group.Lines.Count));
    }

    /// <summary>
    /// Three columns, so the line a continuation belongs to is two places back rather than one.
    /// Nothing about the fix may depend on how many columns the page happens to have.
    /// </summary>
    [Fact]
    public void GroupsWrappedDescriptionsAcrossThreeColumns()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("そんなことを言われても困る", new Rect(10, 100, 260, 24)),
            new("翻譯是一項免費服務，能快速", new Rect(300, 100, 260, 24)),
            new("現在地區：阿富汗、阿爾巴尼", new Rect(590, 100, 260, 24)),
            new("んだけどまあ仕方ない", new Rect(10, 128, 200, 24)),
            new("將日文的單字和片語翻譯", new Rect(300, 128, 220, 24)),
            new("亞、阿爾及利亞、安道爾", new Rect(590, 128, 220, 24)),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks);

        Assert.Equal(3, grouped.Count);
        Assert.All(grouped, group => Assert.Equal(2, group.Lines.Count));
    }
}
