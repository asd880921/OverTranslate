using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

/// <param name="Model">
/// The model to ask for, or empty for <see cref="OpenAiCompatibleProvider.DefaultModel"/>.
/// </param>
/// <param name="PromptAuto">
/// The user's own instruction for 自動 source, or empty to use the built-in one.
/// </param>
/// <param name="PromptExplicit">
/// The user's own instruction for a chosen source language, or empty to use the built-in one.
/// </param>
/// <param name="SendTemperature">
/// Whether the request carries a temperature at all. Off leaves the field out entirely rather than
/// sending a default: a server that rejects the field rejects any value in it, so there is no number
/// that means "never mind".
/// </param>
public sealed record OpenAiCompatibleOptions(
    string BaseUrl,
    string Model,
    string ApiKey = "",
    string PromptAuto = "",
    string PromptExplicit = "",
    bool SendTemperature = true,
    double Temperature = 0);

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
            SettingsService.Instance.Current.OpenAi.TemplateFor(automatic: true),
            SettingsService.Instance.Current.OpenAi.TemplateFor(automatic: false),
            SettingsService.Instance.Current.OpenAiTemperatureEnabled,
            SettingsService.Instance.Current.OpenAiTemperature));
    }

    /// <summary>
    /// The model asked for when the settings page's model box is left empty.
    /// </summary>
    /// <remarks>
    /// A working default rather than an error: the shipped base URL points at a local Ollama, and a
    /// translation-only model is what this provider is for. Named here rather than stored in the
    /// settings file for the same reason the prompt is — see <see cref="DefaultPromptTemplate"/>.
    /// </remarks>
    internal const string DefaultModel = "translategemma:4b";

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
        if (model.Length == 0) model = DefaultModel;
        var configuredApiKey = options.ApiKey.Trim();

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
                    options.SendTemperature ? options.Temperature : null,
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

    /// <param name="temperature">The temperature to ask for, or null to leave the field out.</param>
    private async Task<string> TranslateOneAsync(
        string text,
        string prompt,
        string apiKey,
        Uri endpoint,
        string model,
        double? temperature,
        CancellationToken cancellationToken)
    {
        // A dictionary rather than an anonymous type because one field is conditional: a server that
        // refuses temperature refuses every value of it, so the only way to say nothing is to send
        // no such field.
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = prompt },
                new { role = "user", content = text },
            },
        };
        if (temperature is { } value) payload["temperature"] = value;
        payload["stream"] = false;

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

    /// <summary>
    /// The server asked when the settings page's address box is left empty: a local Ollama on its
    /// own default port, which is what the setup guide this page links to leaves running.
    /// </summary>
    internal const string DefaultBaseUrl = "http://localhost:11434/v1";

    internal static Uri BuildEndpoint(string baseUrl)
    {
        baseUrl = baseUrl.Trim();
        if (baseUrl.Length == 0) baseUrl = DefaultBaseUrl;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
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
    internal const string SourcePlaceholder = "{source_name}";

    /// <summary>The placeholder a template uses for the language being translated into.</summary>
    internal const string TargetPlaceholder = "{target_name}";

    /// <summary>
    /// What the name placeholders were called before the tags gained their own.
    /// </summary>
    /// <remarks>
    /// Still substituted, and deliberately not advertised in the settings hint: a template someone
    /// wrote before this change is sitting in their settings file, and dropping these would send the
    /// model a literal "{source}" instead of a language. Nothing writes them any more, so the pair
    /// only ever shrinks.
    /// </remarks>
    internal const string LegacySourcePlaceholder = "{source}";

    /// <inheritdoc cref="LegacySourcePlaceholder"/>
    internal const string LegacyTargetPlaceholder = "{target}";

    /// <summary>
    /// The placeholders for the language tags — <c>ja</c> rather than <c>Japanese</c>.
    /// </summary>
    /// <remarks>
    /// Separate from the name rather than fused into it, so a template can put the two wherever its
    /// model expects them: TranslateGemma wants "Japanese (ja)", another model may want the tag
    /// alone, and this application cannot know which. The built-in templates compose the pair
    /// themselves — see <see cref="DefaultPromptTemplate"/> — so the shipped wording is unaffected by
    /// the split.
    ///
    /// The names keep meaning only the name, which is also what they meant before the tags existed:
    /// a template someone wrote back then still reads the way they wrote it.
    /// </remarks>
    internal const string SourceCodePlaceholder = "{source_code}";

    /// <inheritdoc cref="SourceCodePlaceholder"/>
    internal const string TargetCodePlaceholder = "{target_code}";

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
        var template = custom.Length > 0 ? custom : DefaultPromptTemplate(automatic);

        return Fill(template, sourceLang, targetLang, automatic);
    }

    /// <summary>
    /// The built-in template for one case, unfilled — the form the settings panel shows as the
    /// prompt in use, so the placeholders are visible rather than described in prose elsewhere.
    /// </summary>
    /// <remarks>
    /// One wording per case, in English, whatever the interface language is. This string is fed to
    /// a model rather than read by one: the language it is written in is a variable on the
    /// translation path, and English is what was actually measured against the model this ships
    /// against. Five interface languages used to mean five built-in wordings — five to keep in step
    /// and four nobody had run.
    ///
    /// A user who wants their own, in their own language or in anyone else's, writes one: the
    /// settings panel keeps a named list of them and falls back to this when none is picked. See
    /// <see cref="Models.OpenAiSettings"/>.
    ///
    /// The explicit wording follows TranslateGemma's own documented shape — "You are a professional
    /// Japanese (ja) to French (fr) translator." — with the placeholders where its documentation
    /// puts the languages. The automatic one is that same sentence with every mention of a source
    /// language removed rather than replaced by "any language": 自動 has no source to name, so its
    /// template carries no source placeholder at all and the two read as one instruction either way.
    /// </remarks>
    internal static string DefaultPromptTemplate(bool automatic) => automatic
        ? $"You are a professional translator into {TargetPlaceholder} ({TargetCodePlaceholder}). " +
          "Your goal is to accurately convey the meaning and nuances of the original text while " +
          $"adhering to {TargetPlaceholder} grammar, vocabulary, and cultural sensitivities. " +
          $"Produce only the {TargetPlaceholder} translation, without any additional explanations " +
          $"or commentary. Please translate the following text into {TargetPlaceholder}:"
        : $"You are a professional {SourcePlaceholder} ({SourceCodePlaceholder}) to " +
          $"{TargetPlaceholder} ({TargetCodePlaceholder}) translator. Your goal is to accurately " +
          $"convey the meaning and nuances of the original {SourcePlaceholder} text while adhering " +
          $"to {TargetPlaceholder} grammar, vocabulary, and cultural sensitivities. Produce only " +
          $"the {TargetPlaceholder} translation, without any additional explanations or commentary. " +
          $"Please translate the following {SourcePlaceholder} text into {TargetPlaceholder}:";

    /// <summary>
    /// Substitutes the language placeholders, naming every language in English so a filled built-in
    /// template reads as one sentence.
    /// </summary>
    /// <remarks>
    /// English regardless of the interface language, and regardless of what the user wrote their own
    /// template in. One placeholder resolves one way — the alternative was <c>{target_name}</c>
    /// meaning "日文" in one prompt and "Japanese" in the next, which is invisible from the panel
    /// that shows the prompt and impossible to write against. It also matches the language tags
    /// beside it, which have only ever been the model's own spelling.
    /// </remarks>
    /// <remarks>
    /// The code placeholders are replaced before the name ones purely for readability; the two sets
    /// cannot collide, because <c>{source}</c> is not a prefix of <c>{source_code}</c> once the
    /// closing brace is counted.
    ///
    /// 自動 has no tag to give, so <see cref="SourceCodePlaceholder"/> empties out there. A template
    /// that wrote its own brackets around it is left with an empty pair, which is the cost of letting
    /// templates place the tag themselves — the built-in automatic template names no source at all
    /// and so never shows it.
    /// </remarks>
    private static string Fill(string template, string sourceLang, string targetLang, bool automatic)
    {
        // Nothing to name when the source is 自動. The built-in automatic template names no source
        // at all, so this only ever reaches a template the user wrote one into themselves.
        var source = automatic
            ? "any language"
            : LanguageData.GetSourceDisplayName(sourceLang, inEnglish: true);

        var target = LanguageData.GetTargetDisplayName(targetLang, inEnglish: true);

        return template
            .Replace(SourceCodePlaceholder, automatic ? "" : LanguageData.GetModelLanguageTag(sourceLang),
                StringComparison.OrdinalIgnoreCase)
            .Replace(TargetCodePlaceholder, LanguageData.GetModelLanguageTag(targetLang),
                StringComparison.OrdinalIgnoreCase)
            .Replace(SourcePlaceholder, source, StringComparison.OrdinalIgnoreCase)
            .Replace(TargetPlaceholder, target, StringComparison.OrdinalIgnoreCase)
            .Replace(LegacySourcePlaceholder, source, StringComparison.OrdinalIgnoreCase)
            .Replace(LegacyTargetPlaceholder, target, StringComparison.OrdinalIgnoreCase);
    }

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
