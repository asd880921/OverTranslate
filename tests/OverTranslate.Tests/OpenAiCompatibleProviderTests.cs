using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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

    [Theory]
    [InlineData("ZH-HANT", "繁體中文")]
    [InlineData("ZH-HANS", "簡體中文")]
    public void BuildPrompt_UsesChineseForChineseTargets(string targetCode, string targetName)
    {
        var prompt = OpenAiCompatibleProvider.BuildPrompt("AUTO", targetCode);

        Assert.Contains($"從(各種語言)翻譯成({targetName})", prompt);
        Assert.Contains("只回傳自然、人性化的翻譯結果", prompt);
        Assert.DoesNotContain("JSON", prompt);
        Assert.True(prompt.Length <= 80);
    }

    // 自動 keeps the same "from … to …" skeleton as a chosen source language rather than
    // restructuring the sentence: a translation-only model stopped half-way through a Japanese line
    // when handed the restructured wording, and finished it under this one.
    [Theory]
    [InlineData("ZH-HANT")]
    [InlineData("EN-US")]
    public void BuildPrompt_NamesTheSourceEvenWhenItIsAutomatic(string targetCode)
    {
        var automatic = OpenAiCompatibleProvider.BuildPrompt("AUTO", targetCode);
        var chosen = OpenAiCompatibleProvider.BuildPrompt("JA", targetCode);

        var skeleton = chosen.Split('(')[0];
        Assert.StartsWith(skeleton, automatic);
    }

    [Theory]
    [InlineData("EN", "JA", "English", "Japanese")]
    [InlineData("JA", "KO", "Japanese", "Korean")]
    public void BuildPrompt_UsesEnglishForOtherTargets(
        string sourceCode,
        string targetCode,
        string sourceName,
        string targetName)
    {
        var prompt = OpenAiCompatibleProvider.BuildPrompt(sourceCode, targetCode);

        Assert.Contains($"from ({sourceName}) to ({targetName})", prompt);
        Assert.Contains("Return only a natural, human-sounding translation", prompt);
        Assert.DoesNotContain("只回傳", prompt);
    }

    [Fact]
    public void BuildPrompt_UsesEnglishForAutomaticSourceWhenTargetIsNotChinese()
    {
        var prompt = OpenAiCompatibleProvider.BuildPrompt("AUTO", "EN-US");

        Assert.Contains("from (any language) to (English)", prompt);
        Assert.Contains("Return only a natural, human-sounding translation", prompt);
    }

    // The custom prompt belongs to the case it was written for: editing one must not change what
    // the other case sends, which is the whole reason two of them are stored.
    [Fact]
    public void BuildPrompt_PrefersTheCustomPromptForTheCaseInHand()
    {
        var automatic = OpenAiCompatibleProvider.BuildPrompt(
            "AUTO", "ZH-HANT", customAuto: "自動用：翻成{target}", customExplicit: "指定用：{source}→{target}");
        var chosen = OpenAiCompatibleProvider.BuildPrompt(
            "JA", "ZH-HANT", customAuto: "自動用：翻成{target}", customExplicit: "指定用：{source}→{target}");

        Assert.Equal("自動用：翻成繁體中文", automatic);
        Assert.Equal("指定用：日語→繁體中文", chosen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildPrompt_FallsBackToTheBuiltInWhenTheCustomOneIsBlank(string custom)
    {
        var prompt = OpenAiCompatibleProvider.BuildPrompt("JA", "ZH-HANT", customExplicit: custom);

        Assert.Equal(OpenAiCompatibleProvider.BuildPrompt("JA", "ZH-HANT"), prompt);
    }

    // A template written for a chosen source language, left in place after switching to 自動, would
    // otherwise send the model a literal "{source}".
    [Fact]
    public void BuildPrompt_FillsTheSourcePlaceholderEvenWhenTheSourceIsAutomatic()
    {
        var prompt = OpenAiCompatibleProvider.BuildPrompt(
            "AUTO", "ZH-HANT", customAuto: "從({source})翻譯成({target})");

        Assert.Equal("從(各種語言)翻譯成(繁體中文)", prompt);
        Assert.DoesNotContain("{source}", prompt);
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
    public async Task TranslateAsync_SendsOneRequestPerBlockAndPreservesOrderAndBounds()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions(
                "http://localhost:1234/v1", "test-model", "secret-key"));
        var blocks = new List<OcrTextBlock>
        {
            new("first", new Rect(1, 2, 30, 40)),
            new("second", new Rect(5, 6, 70, 80)),
        };

        var (translated, detected) = await provider.TranslateAsync(
            blocks, "EN", "ZH-HANT", "ignored-provider-key");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, handler.MaxConcurrentRequests);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("http://localhost:1234/v1/chat/completions", request.Url);
            Assert.Equal("Bearer secret-key", request.Authorization);
            using var payload = JsonDocument.Parse(request.Body);
            Assert.Equal("test-model", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(0, payload.RootElement.GetProperty("temperature").GetInt32());
            Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
        });
        Assert.Equal(["translated:first", "translated:second"],
            translated.Select(block => block.TranslatedText));
        Assert.Equal(blocks[0].Bounds, translated[0].Bounds);
        Assert.Equal(blocks[1].Bounds, translated[1].Bounds);
        Assert.Equal("EN", detected);
    }

    [Fact]
    public async Task TranslateAsync_LimitsIndependentRequestsToEightAtATime()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("http://localhost:1234/v1", "test-model"));
        var blocks = Enumerable.Range(0, 23)
            .Select(index => new OcrTextBlock($"block-{index:D2}", new Rect(index, 0, 10, 10)))
            .ToList();

        var (translated, _) = await provider.TranslateAsync(
            blocks, "EN", "ZH-HANT", "");

        Assert.Equal(23, handler.Requests.Count);
        Assert.Equal(8, handler.MaxConcurrentRequests);
        Assert.Equal(blocks.Select(block => $"translated:{block.Text}"),
            translated.Select(block => block.TranslatedText));
        Assert.Equal(blocks.Select(block => block.Bounds),
            translated.Select(block => block.Bounds));
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
            """{"choices":[{"message":{"content":[{"type":"text","text":"陣列格式譯文"}]}}]}""";
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

    private sealed record RecordedRequest(string Url, string? Authorization, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maxConcurrentRequests;

        public ConcurrentBag<RecordedRequest> Requests { get; } = [];
        public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var activeRequests = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(activeRequests);
            try
            {
                await Task.Delay(10, cancellationToken);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Requests.Add(new RecordedRequest(
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.Authorization?.ToString(),
                    body));

                using var payload = JsonDocument.Parse(body);
                var userText = payload.RootElement.GetProperty("messages")[1]
                    .GetProperty("content").GetString();
                var response = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                role = "assistant",
                                content = $"<think>hidden</think>translated:{userText}",
                            },
                        },
                    },
                });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maxConcurrentRequests);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maxConcurrentRequests, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
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
