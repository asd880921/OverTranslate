using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The 標準 / 漫畫・文章 switch on the capture toolbar: its product copy, and the wiring in the
/// markup that decides which mode a press means.
/// </summary>
/// <remarks>
/// The tooltips are product copy, written to be read by someone deciding which half to press and
/// signed off as written. They are the kind of string a later edit improves the wording of without
/// anyone noticing the meaning moved — "重新排列文字" in particular says something specific and
/// bounded here (a group's lines are joined into one paragraph and re-broken, see design.md §1.3),
/// and is one small rewrite away from promising to reorder panels. Pinned verbatim for that reason.
/// </remarks>
public class CaptureLayoutModeToolbarTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string TraditionalChinese(string key)
    {
        var path = Path.Combine(StringsParityTests.ProjectDirectory(), "Resources", "Strings.zh-Hant.xaml");
        return XDocument.Load(path).Root!
            .Elements()
            .Single(element => (string?)element.Attribute(X + "Key") == key)
            .Value;
    }

    [Fact]
    public void TheLabelsAreTheOnesTheProductCopySays()
    {
        Assert.Equal("標準", TraditionalChinese("S.Toolbar.LayoutModeStandard"));

        // An interpunct, not a slash: ／ is too wide for a two-character half of a segmented switch.
        Assert.Equal("漫畫・文章", TraditionalChinese("S.Toolbar.LayoutModeComic"));
    }

    [Fact]
    public void TheStandardTooltipIsTheProductCopyVerbatim()
    {
        Assert.Equal(
            "保留原有排列，適合一般介面、遊戲 UI 與多欄內容。",
            TraditionalChinese("S.Toolbar.LayoutModeStandardHint"));
    }

    [Fact]
    public void TheComicTooltipIsTheProductCopyVerbatim()
    {
        Assert.Equal(
            "依閱讀順序重新排列文字，並適度放寬文字合併判斷，適合漫畫、文章與連續內容。",
            TraditionalChinese("S.Toolbar.LayoutModeComicHint"));
    }

    /// <summary>
    /// The two halves are one radio group, and it is not the direction switch's group.
    /// </summary>
    /// <remarks>
    /// Two segmented switches now sit side by side on the same bar. Sharing a GroupName would make
    /// them one four-way control: choosing 直排 would silently un-choose 漫畫・文章, and the failure
    /// would look like the mode not being remembered rather than like a name collision.
    /// </remarks>
    [Fact]
    public void TheTwoSwitchesOnTheBarAreSeparateGroups()
    {
        var path = Path.Combine(
            StringsParityTests.ProjectDirectory(), "Views", "Capture", "ToolbarWindow.xaml");
        var groups = XDocument.Load(path)
            .Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .ToDictionary(
                element => (string)element.Attribute(X + "Name")!,
                element => (string)element.Attribute("GroupName")!);

        Assert.Equal(groups["StandardModeSeg"], groups["ComicModeSeg"]);
        Assert.Equal(groups["HorizontalSeg"], groups["VerticalSeg"]);
        Assert.NotEqual(groups["StandardModeSeg"], groups["HorizontalSeg"]);
    }
}
