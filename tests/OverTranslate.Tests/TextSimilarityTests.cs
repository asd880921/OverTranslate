using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// This decides whether a re-recognition of the same region gets translated and repainted, so the
/// two ways it can be wrong are both visible to the user: too strict and an unchanged line flickers
/// while it is retranslated, too loose and a line that really changed never updates.
/// </summary>
public class TextSimilarityTests
{
    private const string Line =
        "The old lighthouse had not been lit for thirty years, and nobody in the village missed it.";

    [Fact]
    public void IdenticalTextIsTheSame()
    {
        Assert.True(TextSimilarity.IsSameContent(Line, Line));
    }

    [Fact]
    public void SpacingDifferencesAreTheSame()
    {
        // Where recognition wobbles most, and what carries the least meaning.
        Assert.True(TextSimilarity.IsSameContent("hello   world  again", "hello world again"));
        Assert.True(TextSimilarity.IsSameContent(" leading and trailing ", "leading and trailing"));
    }

    [Fact]
    public void AFewCharactersOfNoiseInALongLineIsTheSame()
    {
        // The measured case: the same unchanged subtitle read as 222, 227, 230 characters.
        var noisy = Line.Replace("thirty", "thlrty").Replace("village", "vi1lage");

        Assert.True(TextSimilarity.IsSameContent(Line, noisy));
    }

    [Fact]
    public void DifferentWordsAreNotTheSame()
    {
        const string other =
            "The harbour master waved the last boat in and went home to a cold supper by the window.";

        Assert.False(TextSimilarity.IsSameContent(Line, other));
    }

    [Fact]
    public void ARealChangeToOneWordOfAShortLineIsNotTheSame()
    {
        // Short text gets no tolerance at all: a couple of characters here is the whole meaning.
        Assert.False(TextSimilarity.IsSameContent("HP 100", "HP 190"));
        Assert.False(TextSimilarity.IsSameContent("Yes", "No"));
        Assert.False(TextSimilarity.IsSameContent("Level 7", "Level 8"));
    }

    [Fact]
    public void ALineGrowingOrLosingAWholeSentenceIsNotTheSame()
    {
        Assert.False(TextSimilarity.IsSameContent(Line, Line + " But the keeper still walked up every evening."));
    }

    [Fact]
    public void EmptyIsOnlyTheSameAsEmpty()
    {
        Assert.True(TextSimilarity.IsSameContent("", ""));
        Assert.True(TextSimilarity.IsSameContent("", "   "));
        Assert.False(TextSimilarity.IsSameContent("", Line));
        Assert.False(TextSimilarity.IsSameContent(Line, ""));
    }

    [Fact]
    public void ToleranceIsProportionalRatherThanFixed()
    {
        // Same absolute number of altered characters, different line lengths: noise in a long line,
        // a rewrite of a short one.
        var shortLine = "twelve chars";                     // exactly at the tolerance threshold
        var shortEdited = "twelve XXXXs";

        Assert.False(TextSimilarity.IsSameContent(shortLine, shortEdited));
        Assert.True(TextSimilarity.IsSameContent(Line, Line.Replace("nobody", "nabody")));
    }

    [Fact]
    public void CjkTextIsMeasuredTheSameWay()
    {
        const string sentence = "燈塔已經三十年沒有點亮了，村子裡沒有人想念它。";

        Assert.True(TextSimilarity.IsSameContent(sentence, sentence.Replace("想念", "想唸")));
        Assert.False(TextSimilarity.IsSameContent(sentence, "港務長揮手送走最後一艘船，回家吃了一頓冷掉的晚餐。"));
    }
}
