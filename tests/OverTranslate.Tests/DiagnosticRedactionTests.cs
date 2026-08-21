using System.Text.Json.Nodes;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

// The settings file goes into a zip the user is expected to hand to a stranger — a forum thread, an
// issue, eventually an upload. A credential that survives that trip is the one mistake this feature
// can make that the user cannot undo, so the rule that decides what gets hidden is pinned here
// rather than left to be re-read out of the implementation.
public class DiagnosticRedactionTests
{
    [Fact]
    public void ApiKeys_AreReplacedByTheirLength()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"ApiKey":"secret-value","OpenAiApiKey":"sk-0123456789"}""");

        var obj = JsonNode.Parse(json)!.AsObject();

        // The length rather than a blank: whether a key is set at all, and whether it is the length
        // a key of that kind should be, answers a good share of the reports this file is collected
        // for — and neither question can be asked of an empty string.
        Assert.Equal("<redacted:12>", (string?)obj["ApiKey"]);
        Assert.Equal("<redacted:13>", (string?)obj["OpenAiApiKey"]);
    }

    [Fact]
    public void UnsetKey_StaysEmptyRatherThanLookingSet()
    {
        var json = DiagnosticBundleService.RedactSettings("""{"ApiKey":""}""");

        // "<redacted:0>" would say a key was hidden when there was none, and send whoever reads the
        // bundle looking for an authentication problem that cannot exist.
        Assert.Equal("", (string?)JsonNode.Parse(json)!.AsObject()["ApiKey"]);
    }

    [Fact]
    public void HotkeyFields_SurviveRedaction()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"HotkeyVirtualKey":65,"HotkeyDisplay":"Ctrl+Alt+A"}""");

        var obj = JsonNode.Parse(json)!.AsObject();

        // The reason the rule matches "ApiKey" and not "Key". Which key a shortcut is bound to is
        // one of the more useful things in this file, and every hotkey field ends in Key.
        Assert.Equal(65, (int?)obj["HotkeyVirtualKey"]);
        Assert.Equal("Ctrl+Alt+A", (string?)obj["HotkeyDisplay"]);
    }

    [Fact]
    public void NestedRealtimeSettings_AreRedactedToo()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"Realtime":{"OpenAiApiKey":"sk-abcdef","TargetLanguage":"JA"}}""");

        var realtime = JsonNode.Parse(json)!.AsObject()["Realtime"]!.AsObject();

        // Realtime keeps its own copy of the provider settings, so a walk that only looked at the
        // top level would leak the key from the half of the app that has two of them.
        Assert.Equal("<redacted:9>", (string?)realtime["OpenAiApiKey"]);
        Assert.Equal("JA", (string?)realtime["TargetLanguage"]);
    }

    [Fact]
    public void BaseUrl_KeepsTheAddressAndDropsTheCredentials()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"OpenAiBaseUrl":"https://user:pw@example.com/v1?token=abc"}""");

        var url = (string?)JsonNode.Parse(json)!.AsObject()["OpenAiBaseUrl"];

        // The host is most of the diagnosis when an OpenAI-compatible provider misbehaves, so it
        // stays; the user info and query string are where self-hosted front ends put tokens.
        Assert.NotNull(url);
        Assert.Contains("example.com/v1", url);
        Assert.DoesNotContain("pw", url);
        Assert.DoesNotContain("abc", url);
    }

    [Fact]
    public void PlainBaseUrl_IsLeftAlone()
    {
        var json = DiagnosticBundleService.RedactSettings(
            """{"OpenAiBaseUrl":"http://localhost:11434/v1"}""");

        // Pointed at a local model or at a hosted one is the first thing to establish, and rewriting
        // an address that had nothing to hide only invites doubt about what else was rewritten.
        Assert.Equal(
            "http://localhost:11434/v1",
            (string?)JsonNode.Parse(json)!.AsObject()["OpenAiBaseUrl"]);
    }

    [Fact]
    public void FileThatIsNotJson_IsReturnedUnchanged()
    {
        const string broken = "{ this was truncated by a power cut";

        // A file in this shape cannot hold a key in a field we would recognise, and its shape is
        // itself the bug being reported — so it goes into the bundle exactly as found.
        Assert.Equal(broken, DiagnosticBundleService.RedactSettings(broken));
    }
}
