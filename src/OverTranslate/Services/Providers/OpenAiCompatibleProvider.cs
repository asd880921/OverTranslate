using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

public sealed record OpenAiCompatibleOptions(string BaseUrl, string Model);

/// <summary>
/// Translates through the OpenAI-compatible Chat Completions contract while preserving each OCR
/// block's ordering and bounds.
/// </summary>
public sealed class OpenAiCompatibleProvider : ITranslationProvider
{
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly bool UseBatchTranslation = false;
    private static readonly Regex ThinkingBlock = new(
        @"<think(?:\s[^>]*)?>.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex BatchMarker = new(
        @"(?m)^\s*\[__OT_(\d{4,})__\]\s*",
        RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly Func<OpenAiCompatibleOptions> _options;

    public OpenAiCompatibleProvider(
        HttpClient? http = null,
        Func<OpenAiCompatibleOptions>? options = null)
    {
        _http = http ?? DefaultHttp;
        _options = options ?? (() => new OpenAiCompatibleOptions(
            SettingsService.Instance.Current.OpenAiBaseUrl,
            SettingsService.Instance.Current.OpenAiModel));
    }

    // Local OpenAI-compatible servers commonly accept an empty or dummy key. Endpoint and model
    // validation happens when translating, where the UI can show an actionable error.
    public bool RequiresApiKey => false;

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks,
        string sourceLang,
        string targetLang,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0) return ([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options();
        var endpoint = BuildEndpoint(options.BaseUrl);
        var model = options.Model.Trim();
        if (model.Length == 0)
            throw new InvalidOperationException("尚未設定 OpenAI Compatible 的模型名稱。");

        if (UseBatchTranslation)
        {
            var batchTranslations = await TranslateBatchAsync(
                blocks, sourceLang, targetLang, apiKey, endpoint, model, cancellationToken);
            var batchResults = new List<TranslatedBlock>(batchTranslations.Count);
            foreach (var (id, translation) in batchTranslations.OrderBy(item => item.Key))
            {
                var block = blocks[id];
                batchResults.Add(new TranslatedBlock(
                    block.Text,
                    translation,
                    block.Bounds,
                    block.Lines,
                    block.SourceGlyphHeight));
            }

            var batchDetected = LanguageData.IsAutomaticSource(sourceLang)
                ? ""
                : sourceLang.ToUpperInvariant();
            return (batchResults, batchDetected);
        }

        var tasks = blocks.Select(block => TranslateOneAsync(
            block.Text, sourceLang, targetLang, apiKey, endpoint, model, cancellationToken));
        var translations = await Task.WhenAll(tasks);

        var results = new List<TranslatedBlock>(blocks.Count);
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            results.Add(new TranslatedBlock(
                block.Text,
                translations[index],
                block.Bounds,
                block.Lines,
                block.SourceGlyphHeight));
        }

        var detected = LanguageData.IsAutomaticSource(sourceLang) ? "" : sourceLang.ToUpperInvariant();
        return (results, detected);
    }

    private async Task<string> TranslateOneAsync(
        string text,
        string sourceLang,
        string targetLang,
        string apiKey,
        Uri endpoint,
        string model,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = BuildSinglePrompt(sourceLang, targetLang) },
                new { role = "user", content = text },
            },
            stream = false,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var trimmedKey = apiKey.Trim();
        if (trimmedKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"OpenAI Compatible API 回傳 {(int)response.StatusCode}：{ReadError(json)}",
                null,
                response.StatusCode);

        string content;
        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");
            content = ReadContent(message);
        }
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidOperationException("OpenAI Compatible API 回應格式無法解析。", ex);
        }

        var translated = StripThinking(content);
        if (translated.Length == 0)
            throw new InvalidOperationException("OpenAI Compatible API 未回傳譯文。");
        return translated;
    }

    private async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(
        IReadOnlyList<OcrTextBlock> blocks,
        string sourceLang,
        string targetLang,
        string apiKey,
        Uri endpoint,
        string model,
        CancellationToken cancellationToken)
    {
        var batch = string.Join("\n", blocks.Select(
            (block, id) => $"[__OT_{id:D4}__] {block.Text.Replace("\r", " ").Replace("\n", " ")}"));
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "user", content = $"{BuildPrompt(sourceLang, targetLang)}\n\n{batch}" },
            },
            stream = false,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var trimmedKey = apiKey.Trim();
        if (trimmedKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"OpenAI Compatible API 回傳 {(int)response.StatusCode}：{ReadError(json)}",
                null,
                response.StatusCode);

        string content;
        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");
            content = ReadContent(message);
        }
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidOperationException("OpenAI Compatible API 回應格式無法解析。", ex);
        }

        return ParseBatchTranslations(content, blocks.Count);
    }

    internal static Uri BuildEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("OpenAI Compatible API 位址必須是有效的 HTTP 或 HTTPS 網址。");

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path;
            return builder.Uri;
        }

        if (path.Length == 0)
            path = "/v1";
        builder.Path = $"{path}/chat/completions";
        return builder.Uri;
    }

    internal static string BuildPrompt(string sourceLang, string targetLang)
    {
        var source = LanguageData.IsAutomaticSource(sourceLang)
            ? "自動偵測的語言"
            : LanguageData.GetSourceName(sourceLang);
        var target = targetLang.Equals("ZH-HANT", StringComparison.OrdinalIgnoreCase)
            ? "台灣繁體中文"
            : LanguageData.GetTargetName(targetLang);

        return $"將以下內容從({source})翻譯成({target})。保留每個 [__OT_0000__] 格式的標記、順序及換行，不得合併。" +
               "不要思考或加入其他文字，只回傳自然、人性化的翻譯結果。";
    }

    internal static string BuildSinglePrompt(string sourceLang, string targetLang)
    {
        var source = LanguageData.IsAutomaticSource(sourceLang)
            ? "自動偵測的語言"
            : LanguageData.GetSourceName(sourceLang);
        var target = targetLang.Equals("ZH-HANT", StringComparison.OrdinalIgnoreCase)
            ? "台灣繁體中文"
            : LanguageData.GetTargetName(targetLang);

        return $"將使用者文字從({source})翻譯成({target})。" +
               "不要思考或加入額外文字，只回傳自然、人性化的翻譯結果。";
    }

    internal static string StripThinking(string value) => ThinkingBlock.Replace(value, "").Trim();

    internal static IReadOnlyDictionary<int, string> ParseBatchTranslations(
        string content,
        int expectedCount)
    {
        var cleaned = StripThinking(content);
        var matches = BatchMarker.Matches(cleaned);
        var translations = new Dictionary<int, string>();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!int.TryParse(match.Groups[1].Value, out var id) ||
                id < 0 || id >= expectedCount || translations.ContainsKey(id))
                continue;

            var end = index + 1 < matches.Count ? matches[index + 1].Index : cleaned.Length;
            var translation = cleaned[match.Index..end]
                .Remove(0, match.Length)
                .Trim();
            if (translation.EndsWith("```", StringComparison.Ordinal))
                translation = translation[..^3].TrimEnd();
            if (translation.Length > 0)
                translations[id] = translation;
        }

        return translations.Count > 0
            ? translations
            : throw new InvalidOperationException("OpenAI Compatible API 回應中找不到可用的批次譯文標記。");
    }

    private static string ReadContent(JsonElement message)
    {
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content.EnumerateArray().Select(part =>
                part.TryGetProperty("text", out var text) ? text.GetString() : ""));
        }

        return "";
    }

    private static string ReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                return message.GetString() ?? "未知錯誤";
        }
        catch (JsonException)
        {
            // Non-JSON proxies and local servers are common; return a bounded response below.
        }

        var compact = json.Trim();
        if (compact.Length == 0) return "未提供錯誤內容";
        return compact.Length <= 300 ? compact : compact[..300] + "…";
    }
}
