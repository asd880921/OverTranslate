using System.Collections.Generic;
using System.Linq;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class TranslationRequestChunksTests
{
    private static string Sentences(string body, int count) =>
        string.Concat(Enumerable.Repeat($"{body}. ", count));

    /// <summary>
    /// The ordinary case, which is nearly every block: nothing to split, and the text goes up
    /// exactly as it was.
    /// </summary>
    [Fact]
    public void LeavesATextThatFitsAlone()
    {
        var text = "On June 11, 2026, PaddleOCR released PP-OCRv6.";

        var chunk = Assert.Single(TranslationRequestChunks.Split(text));
        Assert.Equal(text, chunk);
    }

    /// <summary>
    /// The invariant that matters most here, because the fault this replaced produced text that was
    /// not in the source: the pieces are the original, in order, with nothing added or lost.
    /// </summary>
    [Fact]
    public void ThePiecesAreExactlyTheOriginal()
    {
        var text = Sentences("The medium tier achieves higher recognition and detection rates", 40);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void NoPieceIsLongerThanTheEndpointsAccept()
    {
        var text = Sentences("PP-OCRv6 improves recognition in specialized scenarios", 40);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, TranslationRequestChunks.MaxCharacters));
    }

    /// <summary>
    /// Cut at sentence ends, so each piece is whole thoughts. A sentence split down the middle is
    /// translated as two half-thoughts, which is the cost this is trying not to pay.
    /// </summary>
    [Fact]
    public void CutsAtSentenceEnds()
    {
        var text = Sentences("Built on the newly designed unified backbone", 40);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.All(chunks.SkipLast(1), chunk => Assert.EndsWith(". ", chunk));
    }

    /// <summary>
    /// A full stop inside a number is not a sentence end. "1.40s vs 7.30s" must not be cut in two.
    /// </summary>
    [Fact]
    public void DoesNotCutInsideANumber()
    {
        var text = Sentences("It runs in 1.40s versus 7.30s on the same processor", 40);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.All(chunks, chunk => Assert.DoesNotContain("1.\n", chunk));
        Assert.All(chunks.SkipLast(1), chunk => Assert.EndsWith(". ", chunk));
    }

    /// <summary>
    /// CJK needs no space after its full stop, because nothing else uses those marks.
    /// </summary>
    [Fact]
    public void CutsChineseAtItsOwnFullStops()
    {
        var text = string.Concat(Enumerable.Repeat("這是一個沒有空格的句子。", 120));

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks.SkipLast(1), chunk => Assert.EndsWith("。", chunk));
    }

    /// <summary>
    /// With no sentence to end, a word boundary will do — better a whole word than a whole limit.
    /// </summary>
    [Fact]
    public void FallsBackToWordBoundaries()
    {
        var text = string.Concat(Enumerable.Repeat("word ", 400));

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks.SkipLast(1), chunk => Assert.EndsWith(" ", chunk));
    }

    /// <summary>
    /// And with neither, the limit itself — but never through the middle of a character.
    /// </summary>
    [Fact]
    public void SplitsUnbrokenTextWithoutBreakingACharacter()
    {
        var text = string.Concat(Enumerable.Repeat("🙂", 700));

        var chunks = TranslationRequestChunks.Split(text);

        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk =>
        {
            Assert.False(char.IsLowSurrogate(chunk[0]));
            Assert.False(char.IsHighSurrogate(chunk[^1]));
        });
    }

    [Fact]
    public void JoinsTranslatedPiecesWithASingleSpaceBetweenWords()
    {
        Assert.Equal(
            "第一段。 第二段。",
            TranslationRequestChunks.Join(["第一段。 ", " 第二段。"]));
    }

    [Fact]
    public void JoinIgnoresPiecesThatCameBackEmpty()
    {
        Assert.Equal("only this", TranslationRequestChunks.Join(["", "  ", "only this"]));
    }
}
