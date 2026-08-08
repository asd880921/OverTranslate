using OverTranslate.Services.Realtime;
using Xunit;
using Color = System.Windows.Media.Color;

namespace OverTranslate.Tests;

/// <summary>
/// The settings file is hand-editable, so every one of these is a value that can really arrive.
/// </summary>
public class RealtimeSubtitleColorsTests
{
    [Theory]
    [InlineData("#FF8800", 0xFF, 0x88, 0x00)]
    [InlineData("#ff8800", 0xFF, 0x88, 0x00)]   // lower case
    [InlineData("ff8800", 0xFF, 0x88, 0x00)]    // no leading hash
    [InlineData("  #FF8800  ", 0xFF, 0x88, 0x00)]
    public void Text_reads_a_six_digit_colour(string hex, byte r, byte g, byte b)
    {
        var color = RealtimeSubtitleColors.Text(hex);

        Assert.Equal(Color.FromRgb(r, g, b), color);
    }

    [Fact]
    public void Text_is_always_opaque()
    {
        Assert.Equal(0xFF, RealtimeSubtitleColors.Text("#123456").A);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#FFF")]            // three-digit shorthand is not accepted
    [InlineData("#GGGGGG")]         // not hex
    [InlineData("rgb(1,2,3)")]
    [InlineData("#FF88000000")]     // too long for even an ARGB value
    public void An_unreadable_value_falls_back_to_the_default(string? hex)
    {
        // The point is that a typo costs the user their colour, not their subtitles.
        Assert.Equal(RealtimeSubtitleColors.Text(RealtimeSubtitleColors.DefaultText),
                     RealtimeSubtitleColors.Text(hex));
        Assert.Equal(RealtimeSubtitleColors.Scrim(RealtimeSubtitleColors.DefaultScrim),
                     RealtimeSubtitleColors.Scrim(hex));
    }

    [Fact]
    public void An_eight_digit_value_keeps_its_rgb_and_loses_its_alpha()
    {
        var color = RealtimeSubtitleColors.Text("#40FF8800");

        Assert.Equal(Color.FromRgb(0xFF, 0x88, 0x00), color);
    }

    [Fact]
    public void Scrim_always_carries_the_fixed_alpha()
    {
        // Whatever the user picked, the band has to stay sheer enough to see through and opaque
        // enough to hide the line underneath — that is not theirs to set.
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#1E3A5F", "not a colour" })
            Assert.Equal(RealtimeSubtitleColors.ScrimAlpha, RealtimeSubtitleColors.Scrim(hex).A);
    }

    [Fact]
    public void Format_round_trips_through_Text()
    {
        var original = Color.FromRgb(0x1E, 0x90, 0xD5);

        var formatted = RealtimeSubtitleColors.Format(original);

        Assert.Equal("#1E90D5", formatted);
        Assert.Equal(original, RealtimeSubtitleColors.Text(formatted));
    }

    [Fact]
    public void Format_ignores_alpha_so_a_scrim_colour_survives_a_save_and_reload()
    {
        // Round-tripping the scrim is how the picker writes back what it was shown; if Format kept
        // the alpha, each save would add another set of digits for Parse to strip.
        var stored = RealtimeSubtitleColors.Format(RealtimeSubtitleColors.Scrim("#1E3A5F"));

        Assert.Equal("#1E3A5F", stored);
        Assert.Equal(RealtimeSubtitleColors.Scrim("#1E3A5F"), RealtimeSubtitleColors.Scrim(stored));
    }
}
