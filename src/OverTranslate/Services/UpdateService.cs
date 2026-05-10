using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace OverTranslate.Services;

public record UpdateInfo(Version LatestVersion, string ReleaseUrl);

public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/asd880921/OverTranslate/releases/latest";
    private static readonly HttpClient Http = new();

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("OverTranslate");
        Http.Timeout = TimeSpan.FromSeconds(10);
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var release = await Http.GetFromJsonAsync<GitHubRelease>(ApiUrl);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return null;

            var tag = release.TagName.TrimStart('v');
            if (!Version.TryParse(tag, out var latest)) return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null) return null;

            // Normalize both to 3 components to avoid -1 Revision mismatch
            var latest3  = new Version(latest.Major,  latest.Minor,  Math.Max(0, latest.Build));
            var current3 = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (latest3 <= current3) return null;

            var url = string.IsNullOrWhiteSpace(release.HtmlUrl)
                ? "https://github.com/asd880921/OverTranslate/releases/latest"
                : release.HtmlUrl;
            return new UpdateInfo(latest3, url);
        }
        catch
        {
            return null;
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }
    }
}
