using System.Net.Http;
using System.Text.Json;

namespace OverTranslate.Services.Providers;

public class DeepLProvider : ITranslationProvider
{
    private readonly HttpClient _http = new();

    public bool RequiresApiKey => true;

    private static string GetEndpoint(string apiKey) =>
        apiKey.TrimEnd().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey)
    {
        if (blocks.Count == 0) return ([], "");

        var content = new List<KeyValuePair<string, string>>();
        foreach (var b in blocks)
            content.Add(new("text", b.Text));

        if (sourceLang != "auto")
            content.Add(new("source_lang", sourceLang.ToUpper()));

        content.Add(new("target_lang", targetLang.ToUpper()));

        using var request = new HttpRequestMessage(HttpMethod.Post, GetEndpoint(apiKey));
        request.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");
        request.Content = new FormUrlEncodedContent(content);

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var translations = doc.RootElement.GetProperty("translations");

        var langVotes  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var results    = new List<TranslatedBlock>();
        int arrayLen   = translations.GetArrayLength();
        for (int i = 0; i < blocks.Count; i++)
        {
            // Guard against DeepL returning fewer translations than blocks requested
            // (can happen when inputs are empty/whitespace-only or filtered server-side).
            if (i >= arrayLen)
            {
                results.Add(new TranslatedBlock(blocks[i].Text, "", blocks[i].Bounds, blocks[i].Lines, blocks[i].SourceGlyphHeight));
                continue;
            }
            var t = translations[i];
            if (t.TryGetProperty("detected_source_language", out var det))
            {
                var lang = (det.GetString() ?? "").ToUpperInvariant();
                if (!string.IsNullOrEmpty(lang))
                    langVotes[lang] = langVotes.GetValueOrDefault(lang) + 1;
            }
            var translated = t.GetProperty("text").GetString() ?? "";
            results.Add(new TranslatedBlock(blocks[i].Text, translated, blocks[i].Bounds, blocks[i].Lines, blocks[i].SourceGlyphHeight));
        }

        string detectedLang = langVotes.Count > 0
            ? langVotes.MaxBy(kv => kv.Value).Key
            : "";

        return (results, detectedLang);
    }
}
