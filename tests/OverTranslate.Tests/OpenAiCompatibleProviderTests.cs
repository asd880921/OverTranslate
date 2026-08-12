using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class OpenAiCompatibleProviderTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:1234", "http://localhost:1234/v1/chat/completions")]
    [InlineData("https://example.test/custom/chat/completions", "https://example.test/custom/chat/completions")]
    public void BuildEndpoint_AcceptsBaseOrFullChatCompletionsUrl(string input, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleProvider.BuildEndpoint(input).AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:1234")]
    [InlineData("ftp://example.test/v1")]
    public void BuildEndpoint_RejectsInvalidUrl(string input)
    {
        Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleProvider.BuildEndpoint(input));
    }

    [Fact]
    public void BuildPrompt_UsesShortPlainTextMarkerInstructions()
    {
        var prompt = OpenAiCompatibleProvider.BuildPrompt("AUTO", "ZH-HANT");

        Assert.Contains("從自動偵測的語言翻譯成台灣繁體中文", prompt);
        Assert.Contains("[__OT_0000__]", prompt);
        Assert.Contains("不要思考或加入其他文字", prompt);
        Assert.Contains("自然、人性化", prompt);
        Assert.DoesNotContain("JSON", prompt);
    }

    [Fact]
    public void BuildPrompt_UsesNamedSourceAndDoesNotApplyChineseRuleToEnglishTarget()
    {
        var prompt = OpenAiCompatibleProvider.BuildPrompt("JA", "EN-US");

        Assert.Contains("從日語翻譯成英語", prompt);
        Assert.DoesNotContain("台灣繁體中文", prompt);
    }

    [Theory]
    [InlineData("<think>internal reasoning</think>\n正確譯文", "正確譯文")]
    [InlineData("<THINK mode=\"deep\">hidden</THINK>Visible", "Visible")]
    [InlineData("保留正常的譯文", "保留正常的譯文")]
    public void StripThinking_RemovesCommonThinkingBlocks(string response, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleProvider.StripThinking(response));
    }

    [Fact]
    public async Task TranslateAsync_SendsAllBlocksInOneChatCompletionAndPreservesOrderAndBounds()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("http://localhost:1234/v1", "test-model"));
        var blocks = new List<OcrTextBlock>
        {
            new("first", new Rect(1, 2, 30, 40)),
            new("second", new Rect(5, 6, 70, 80)),
        };

        var (translated, detected) = await provider.TranslateAsync(
            blocks, "EN", "ZH-HANT", "secret-key");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:1234/v1/chat/completions", request.Url);
        Assert.Equal("Bearer secret-key", request.Authorization);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("test-model", payload.RootElement.GetProperty("model").GetString());
        Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
        var messages = payload.RootElement.GetProperty("messages");
        var message = Assert.Single(messages.EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var batchText = message.GetProperty("content").GetString();
        Assert.Contains("[__OT_0000__] first", batchText);
        Assert.Contains("[__OT_0001__] second", batchText);
        Assert.Equal(["translated:first", "translated:second"],
            translated.Select(block => block.TranslatedText));
        Assert.Equal(blocks[0].Bounds, translated[0].Bounds);
        Assert.Equal(blocks[1].Bounds, translated[1].Bounds);
        Assert.Equal("EN", detected);
    }

    [Fact]
    public async Task TranslateAsync_LeavesAuthorizationHeaderOutWhenKeyIsEmpty()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("http://localhost:11434/v1", "local-model"));

        await provider.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "AUTO", "ZH-HANT", "");

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task TranslateAsync_RejectsMissingModelBeforeSendingRequest()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("http://localhost:1234/v1", " "));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranslateAsync(
                [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", ""));

        Assert.Contains("模型名稱", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TranslateAsync_ReadsTextContentPartsFromCompatibleServers()
    {
        const string response =
            """{"choices":[{"message":{"content":[{"type":"text","text":"[__OT_0000__] 陣列格式譯文"}]}}]}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, response));
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("https://example.test/v1", "test-model"));

        var (translated, _) = await provider.TranslateAsync(
            [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "key");

        Assert.Equal("陣列格式譯文", Assert.Single(translated).TranslatedText);
    }

    [Fact]
    public async Task TranslateAsync_SurfacesCompatibleApiErrorMessage()
    {
        const string response = """{"error":{"message":"model not found"}}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.BadRequest, response));
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("https://example.test/v1", "missing-model"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateAsync(
                [new OcrTextBlock("hello", new Rect())], "EN", "ZH-HANT", "key"));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("model not found", error.Message);
    }

    [Fact]
    public void ParseBatchTranslations_UsesMarkersAndAcceptsThinkingAndMarkdownFence()
    {
        const string response =
            "<think>hidden</think>```text\n" +
            "[__OT_0001__] 第二段\n" +
            "[__OT_0000__] 第一段\n" +
            "```";

        var translations = OpenAiCompatibleProvider.ParseBatchTranslations(response, 2);

        Assert.Equal("第一段", translations[0]);
        Assert.Equal("第二段", translations[1]);
    }

    [Fact]
    public void ParseBatchTranslations_KeepsMultilineTextUntilTheNextMarker()
    {
        const string response =
            "[__OT_0000__] 第一行\n第二行包含 JSON：{\"ok\":true}\n" +
            "[__OT_0001__] 第二段";

        var translations = OpenAiCompatibleProvider.ParseBatchTranslations(response, 2);

        Assert.Equal("第一行\n第二行包含 JSON：{\"ok\":true}", translations[0]);
        Assert.Equal("第二段", translations[1]);
    }

    [Fact]
    public void ParseBatchTranslations_ReturnsAvailableIdsAndIgnoresMissingExtraEmptyAndDuplicateIds()
    {
        const string response =
            "[__OT_0000__] 第一段\n" +
            "[__OT_0002__] 第三段\n" +
            "[__OT_0002__] 重複內容\n" +
            "[__OT_0003__]   \n" +
            "[__OT_9999__] 額外內容";

        var translations = OpenAiCompatibleProvider.ParseBatchTranslations(response, 4);

        Assert.Equal(2, translations.Count);
        Assert.Equal("第一段", translations[0]);
        Assert.Equal("第三段", translations[2]);
        Assert.False(translations.ContainsKey(1));
        Assert.False(translations.ContainsKey(3));
    }

    [Fact]
    public async Task TranslateAsync_OnlyReturnsBlocksForIdsTheModelProvided()
    {
        const string response =
            """{"choices":[{"message":{"content":"[__OT_0001__] 第二段"}}]}""";
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, response));
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("https://example.test/v1", "test-model"));
        var blocks = new List<OcrTextBlock>
        {
            new("first", new Rect(1, 2, 3, 4)),
            new("second", new Rect(5, 6, 7, 8)),
        };

        var (translated, _) = await provider.TranslateAsync(blocks, "EN", "ZH-HANT", "key");

        var result = Assert.Single(translated);
        Assert.Equal("second", result.OriginalText);
        Assert.Equal("第二段", result.TranslatedText);
        Assert.Equal(blocks[1].Bounds, result.Bounds);
    }

    [Fact]
    public void ParseBatchTranslations_RejectsResponseWithoutAnyUsableMarker()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OpenAiCompatibleProvider.ParseBatchTranslations("沒有可用標記", 2));
    }

    private sealed record RecordedRequest(string Url, string? Authorization, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                body));

            using var payload = JsonDocument.Parse(body);
            var batchText = payload.RootElement.GetProperty("messages")[0]
                .GetProperty("content").GetString()!;
            var translations = string.Join("\n", Regex.Matches(
                    batchText,
                    @"(?m)^\[__OT_(\d{4})__\] (.*)$")
                .Select(match =>
                    $"[__OT_{match.Groups[1].Value}__] translated:{match.Groups[2].Value}"));
            var response = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = $"<think>hidden</think>{translations}",
                        },
                    },
                },
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            };
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });
    }
}
