using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Both hints name a key the user can rebind or clear in 設定. Without one, the settings-page line
/// disappears while the clickable refresh button still keeps a useful action-only tooltip.
/// </summary>
public class RealtimeRefreshHintTests
{
    [Fact]
    public void The_control_tooltip_names_the_normalized_bound_shortcut()
    {
        var hint = RealtimeRefreshHint.ForControlTooltip("  Ctrl+Alt+A  ");

        Assert.Equal("刷新當前畫面翻譯內容 (Ctrl+Alt+A)", hint);
    }

    [Fact]
    public void The_page_hint_names_the_bound_shortcut()
    {
        var hint = RealtimeRefreshHint.ForSettingsPage("Ctrl+Shift+R");

        Assert.Contains("Ctrl+Shift+R", hint);
    }

    // The settings file is hand-editable and the shortcut can be cleared from 設定, so all three of
    // these can really arrive.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Page_hint_is_hidden_and_control_tooltip_still_names_the_action_without_a_shortcut(string? hotkey)
    {
        Assert.Equal("刷新當前畫面翻譯內容", RealtimeRefreshHint.ForControlTooltip(hotkey));
        Assert.Equal("", RealtimeRefreshHint.ForSettingsPage(hotkey));
    }
}
