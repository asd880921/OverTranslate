using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

/// <param name="PromptAuto">
/// The user's own instruction for 自動 source, or empty to use the built-in one.
/// </param>
/// <param name="PromptExplicit">
/// The user's own instruction for a chosen source language, or empty to use the built-in one.
/// </param>
public sealed record OpenAiCompatibleOptions(
    string BaseUrl,
    string Model,
    string ApiKey = "",
    string PromptAuto = "",
    string PromptExplicit = "");

/// <summary>
/// Translates through the OpenAI-compatible Chat Completions contract. Each OCR block is an
/// independent request so its bounds and ordering stay aligned with the existing provider model.
/// </summary>
public sealed class OpenAiCompatibleProvider : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const int MaxConcurrentRequests = 8;
    private static readonly Regex ThinkingBlock = new(
        @"<think(?:\s[^>]*)?>.*?</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly Func<OpenAiCompatibleOptions> _options;

    public OpenAiCompatibleProvider(
        HttpClient? http = null,
        Func<OpenAiCompatibleOptions>? options = null)
    {
        _http = http ?? DefaultHttp;
        _options = options ?? (() => new OpenAiCompatibleOptions(
            SettingsService.Instance.Current.OpenAiBaseUrl,
            SettingsService.Instance.Current.OpenAiModel,
            SettingsService.Instance.Current.OpenAiApiKey,
            SettingsService.Instance.Current.OpenAiPromptAuto,
            SettingsService.Instance.Current.OpenAiPromptExplicit));
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
        var configuredApiKey = options.ApiKey.Trim();
        if (model.Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiNoModel"));

        // Counts and configuration only, so this stays in the shipped log: it is what tells a report
        // of "nothing was translated" apart from a request that never left, and names the model the
        // answer came from — with a local server the model is the variable that explains the output.
        Log.Info("OpenAI 相容翻譯：{Count} 個區塊，模型 \"{Model}\"，端點 {Endpoint}",
            blocks.Count, model, endpoint);

        // Built once for the batch: every block is sent the same instruction, and the user may have
        // written this one themselves, so it is worth resolving in one place rather than per request.
        var prompt = BuildPrompt(sourceLang, targetLang, options.PromptAuto, options.PromptExplicit);

        // The prompt and the text itself only at Debug: the text is whatever was on the user's
        // screen, the same reason OnnxOcrEngine keeps the recognised text out of the shipped log.
        Log.Debug("OpenAI 相容翻譯 prompt=\"{Prompt}\"", prompt);

        var translations = new string[blocks.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, blocks.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentRequests,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                translations[index] = await TranslateOneAsync(
                    blocks[index].Text,
                    prompt,
                    configuredApiKey,
                    endpoint,
                    model,
                    token);

                // Both sides of one block on one line: a block that came back still in its own
                // language is the shape this provider fails in, and that is only visible by reading
                // the request against the reply.
                if (Log.IsDebugEnabled)
                    Log.Debug("OpenAI 相容翻譯 index={Index} in=\"{In}\" out=\"{Out}\"",
                        index, blocks[index].Text, translations[index]);
            });

        var results = new List<TranslatedBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            results.Add(new TranslatedBlock(
                block.Text,
                translations[i],
                block.Bounds,
                block.Lines,
                block.SourceGlyphHeight));
        }

        var detected = LanguageData.IsAutomaticSource(sourceLang) ? "" : sourceLang.ToUpperInvariant();
        return (results, detected);
    }

    private async Task<string> TranslateOneAsync(
        string text,
        string prompt,
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
                new { role = "system", content = prompt },
                new { role = "user", content = text },
            },
            temperature = 0,
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
                LocalizationService.Format(
                    "S.Error.OpenAiHttp", (int)response.StatusCode, ReadError(json)),
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
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiUnparsable"), ex);
        }

        var translated = StripThinking(content);
        if (translated.Length == 0)
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiNoTranslation"));
        return translated;
    }

    internal static Uri BuildEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(LocalizationService.Get("S.Error.OpenAiBadUrl"));

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

    /// <summary>The placeholder a template uses for the language being translated out of.</summary>
    internal const string SourcePlaceholder = "{source}";

    /// <summary>The placeholder a template uses for the language being translated into.</summary>
    internal const string TargetPlaceholder = "{target}";

    /// <summary>
    /// The instruction for one batch: the user's own template when they have written one, otherwise
    /// the built-in template for this case, with the language placeholders filled in.
    /// </summary>
    /// <param name="customAuto">The user's template for 自動 source, or empty for the built-in.</param>
    /// <param name="customExplicit">
    /// The user's template for a chosen source language, or empty for the built-in.
    /// </param>
    /// <remarks>
    /// The two cases keep separate templates rather than sharing one with a blank to fill: 自動 has
    /// no source language to name, so its sentence has no <see cref="SourcePlaceholder"/> in it at
    /// all. A template that does use it while the source is 自動 still gets something readable
    /// rather than a leaked brace — see <see cref="Fill"/>.
    /// </remarks>
    internal static string BuildPrompt(
        string sourceLang,
        string targetLang,
        string customAuto = "",
        string customExplicit = "")
    {
        var automatic = LanguageData.IsAutomaticSource(sourceLang);
        var custom = (automatic ? customAuto : customExplicit).Trim();
        var template = custom.Length > 0 ? custom : DefaultPromptTemplate(automatic, targetLang);

        return Fill(template, sourceLang, targetLang, automatic);
    }

    /// <summary>
    /// The built-in template for one case, unfilled — the form the settings page shows as the
    /// starting point, so the placeholders are visible rather than described in prose elsewhere.
    /// </summary>
    /// <remarks>
    /// Written in the target's own language, which is the same nudge the instruction itself is:
    /// a model asked in Chinese to produce Chinese has one fewer way to drift. A template the user
    /// wrote replaces this wholesale, whatever language they wrote it in.
    ///
    /// Both automatic forms deliberately keep the "from … to …" skeleton of the explicit one rather
    /// than restructuring the sentence. Translation-only models are trained on that shape, and one
    /// measurably stopped half-way through a Japanese line when handed the restructured wording.
    /// </remarks>
    internal static string DefaultPromptTemplate(bool automatic, string targetLang) =>
        UsesChinesePrompt(targetLang)
            ? (automatic
                ? $"從(各種語言)翻譯成({TargetPlaceholder})。" +
                  "不要思考或加入額外文字，只回傳自然、人性化的翻譯結果。"
                : $"從({SourcePlaceholder})翻譯成({TargetPlaceholder})。" +
                  "不要思考或加入額外文字，只回傳自然、人性化的翻譯結果。")
            : (automatic
                ? $"Translate the input text from (any language) to ({TargetPlaceholder}). " +
                  "Do not think or add extra text. Return only a natural, human-sounding translation."
                : $"Translate the input text from ({SourcePlaceholder}) to ({TargetPlaceholder}). " +
                  "Do not think or add extra text. Return only a natural, human-sounding translation.");

    private static bool UsesChinesePrompt(string targetLang) =>
        targetLang.Trim().ToUpperInvariant() is "ZH-HANS" or "ZH-HANT";

    /// <summary>
    /// Substitutes the language placeholders. Which language the names themselves are written in
    /// follows the target, so a filled built-in template reads as one sentence; a custom template
    /// written in another language gets the same names, since there is no way to know what language
    /// the user's prose is in.
    /// </summary>
    private static string Fill(string template, string sourceLang, string targetLang, bool automatic)
    {
        var chinese = UsesChinesePrompt(targetLang);

        var target = chinese
            ? LanguageData.GetTargetName(targetLang)
            : GetEnglishLanguageName(LanguageData.TargetLanguages, targetLang);

        // Nothing to name when the source is 自動, so a template that asks for one anyway is given
        // the same words the built-in automatic template uses in that position.
        var source = automatic
            ? (chinese ? "各種語言" : "any language")
            : chinese
                ? LanguageData.GetSourceName(sourceLang)
                : GetEnglishLanguageName(LanguageData.SourceLanguages, sourceLang);

        return template
            .Replace(SourcePlaceholder, source, StringComparison.OrdinalIgnoreCase)
            .Replace(TargetPlaceholder, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEnglishLanguageName(IEnumerable<LangItem> languages, string code) =>
        languages.FirstOrDefault(language =>
            language.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.English ?? code;

    internal static string StripThinking(string value) => ThinkingBlock.Replace(value, "").Trim();

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
                return message.GetString() ?? LocalizationService.Get("S.Error.UnknownError");
        }
        catch (JsonException)
        {
            // Non-JSON proxies and local servers are common; return a bounded response below.
        }

        var compact = json.Trim();
        if (compact.Length == 0) return LocalizationService.Get("S.Error.NoErrorContent");
        return compact.Length <= 300 ? compact : compact[..300] + "…";
    }
}
