using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The facts about 取詞翻譯's markup that read like decoration and are not.
/// </summary>
/// <inheritdoc cref="TranslationPageMarkupTests"/>
public class QuickLookupWindowMarkupTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Window() => XDocument.Load(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "QuickLookup", "QuickLookupWindow.xaml"));

    /// <summary>
    /// 朗讀原文 is switched off until the engine has said what language the original was in, and its
    /// tooltip is then the only place that reason exists — the popup has no room for a note beside
    /// the button the way 文字翻譯 does. WPF withholds tooltips from disabled controls unless asked.
    /// </summary>
    [Fact]
    public void The_source_speaker_still_explains_itself_while_disabled()
    {
        var speaker = Window()
            .Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "SrcTtsBtn");

        Assert.Equal("True", (string?)speaker.Attribute("ToolTipService.ShowOnDisabled"));
    }

    /// <summary>
    /// The popup is placed against the pointer and closes when the pointer leaves, so it has to be
    /// able to sit over a full-screen application and must never take a taskbar button — a window in
    /// the taskbar that vanishes a second later is a button the user cannot hit.
    /// </summary>
    [Fact]
    public void The_popup_floats_over_everything_without_joining_the_taskbar()
    {
        var window = Window().Root!;

        Assert.Equal("True", (string?)window.Attribute("Topmost"));
        Assert.Equal("False", (string?)window.Attribute("ShowInTaskbar"));
    }

    /// <summary>
    /// Height follows the content because the popup has three sizes — a bare box, a translation, the
    /// settings panel — and the one thing it must not do is reserve room for the tallest of them
    /// over somebody else's screen.
    /// </summary>
    [Fact]
    public void The_popup_is_only_as_tall_as_what_it_is_showing()
    {
        Assert.Equal("Height", (string?)Window().Root!.Attribute("SizeToContent"));
    }
}
