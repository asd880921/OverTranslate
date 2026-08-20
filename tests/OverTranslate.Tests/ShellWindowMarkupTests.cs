using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The facts about the shell's nav rail markup that read like decoration and are not.
/// </summary>
/// <inheritdoc cref="TranslationPageMarkupTests"/>
public class ShellWindowMarkupTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Window() => XDocument.Load(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "Shell", "ShellWindow.xaml"));

    private static XElement Row(string name) => Window()
        .Descendants()
        .Single(e => (string?)e.Attribute(X + "Name") == name);

    /// <summary>
    /// Both 快速工具 rows are switched off for the length of a realtime session, and their tooltips
    /// are then the only place that reason exists — the rail has no room for a note beside them.
    /// WPF withholds tooltips from disabled controls unless asked.
    /// </summary>
    [Theory]
    [InlineData("CaptureBtn")]
    [InlineData("QuickLookupBtn")]
    public void A_quick_tool_still_explains_itself_while_disabled(string name)
    {
        Assert.Equal("True", (string?)Row(name).Attribute("ToolTipService.ShowOnDisabled"));
    }

    /// <summary>
    /// The shortcut is docked before the label, so the label is what trims when the rail is dragged
    /// to its minimum width. A shortcut trimmed to "Ctrl+Al..." names no key the user can press.
    /// </summary>
    [Theory]
    [InlineData("CaptureBtn", "CaptureHotkeyText")]
    [InlineData("QuickLookupBtn", "QuickLookupHotkeyText")]
    public void A_quick_tool_trims_its_name_rather_than_its_shortcut(string row, string hotkey)
    {
        var children = Row(row).Elements().Single().Elements().ToList();

        var shortcutAt = children.FindIndex(e => (string?)e.Attribute(X + "Name") == hotkey);
        var labelAt = children.FindIndex(e => (string?)e.Attribute("TextTrimming") is not null);

        Assert.True(shortcutAt >= 0 && labelAt > shortcutAt);
    }

    /// <summary>
    /// The drag area and the strip the user sees have to be the same strip: WindowChrome's caption
    /// height is what makes the title bar draggable, and it knows nothing about the row the markup
    /// draws there.
    /// </summary>
    [Fact]
    public void The_draggable_caption_covers_exactly_the_title_row()
    {
        var doc = Window();

        var chrome = doc.Descendants().Single(e => e.Name.LocalName == "WindowChrome");
        var titleRow = doc.Descendants()
            .First(e => e.Name.LocalName == "RowDefinition");

        Assert.Equal((string?)titleRow.Attribute("Height"), (string?)chrome.Attribute("CaptionHeight"));
    }

    /// <summary>
    /// Everything inside the caption strip is drag surface unless it says otherwise, so without
    /// this the window's own buttons would move the window instead of pressing.
    /// </summary>
    [Fact]
    public void The_caption_buttons_are_clickable_rather_than_drag_surface()
    {
        var buttons = Row("MinimizeBtn").Parent!;

        Assert.Equal("True", (string?)buttons.Attribute(
            XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation") + "IsHitTestVisibleInChrome")
            ?? (string?)buttons.Attribute("WindowChrome.IsHitTestVisibleInChrome"));
    }

    /// <summary>
    /// The rail can be dragged away entirely, and the splitter is the only way back. In the rail's
    /// own column it would sit at x=0 and reach off the window, so a collapsed rail could never be
    /// brought back.
    /// </summary>
    [Fact]
    public void The_splitter_stays_reachable_with_the_rail_collapsed()
    {
        var splitter = Row("SidebarSplitter");

        Assert.Equal("1", (string?)splitter.Attribute("Grid.Column"));
        Assert.Equal("Left", (string?)splitter.Attribute("HorizontalAlignment"));
    }
}
