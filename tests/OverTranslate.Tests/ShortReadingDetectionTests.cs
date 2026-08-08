using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Rejecting a real line here costs a subtitle; letting a false one through puts a single glyph on
/// screen where a correct translation was, so both directions are held down by measured readings.
/// </summary>
public class ShortReadingDetectionTests
{
    [Theory]
    // The three false readings from the 2026-08-09 session, each off a frame with no subtitle on it
    // at all, and the single characters the collapse work recorded before that.
    [InlineData("A")]
    [InlineData("M")]
    [InlineData("?")]
    [InlineData("2")]
    public void OneCharacterIsSceneryRatherThanText(string text)
    {
        Assert.True(ShortReadingDetection.IsTooShort(text));
    }

    [Theory]
    // The two real rescues from that same session, and the shortest subtitles in the fixtures.
    [InlineData("It's a bit too vague.")]
    [InlineData("It might just...")]
    [InlineData("Yes.")]
    [InlineData("Ok")]
    public void RealLinesAreKept(string text)
    {
        Assert.False(ShortReadingDetection.IsTooShort(text));
    }

    [Theory]
    // A box holding nothing but space is the same nothing as an empty one; padding must not buy a
    // reading its way past the floor.
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  A  ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void WhitespaceDoesNotCountTowardsLength(string? text)
    {
        Assert.True(ShortReadingDetection.IsTooShort(text));
    }

    [Fact]
    public void TwoCharactersIsTheFloorAndSurvives()
    {
        // CJK earns its place here: two characters is a whole sentence in the languages this reads,
        // so the floor cannot be raised without cost even though every measured false reading was
        // one character.
        Assert.False(ShortReadingDetection.IsTooShort("はい"));
        Assert.False(ShortReadingDetection.IsTooShort("好的"));
    }
}
