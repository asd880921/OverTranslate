using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace OverTranslate.Services.LocalNmt;

public sealed record LlamaCppOptions(
    string ServerExecutablePath,
    string ModelRoot,
    TimeSpan InitializationTimeout,
    TimeSpan RequestTimeout)
{
    public static LlamaCppOptions Create(string serverExecutablePath, string modelRoot) => new(
        Path.GetFullPath(serverExecutablePath),
        Path.GetFullPath(modelRoot),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(30));
}

/// <summary>Runs one isolated llama.cpp server for each active Hy-MT2 language direction.</summary>
public sealed class LlamaCppSessionFactory(LlamaCppOptions options) : ILocalModelSessionFactory
{
    public async Task<ILocalModelSession> CreateAsync(
        LocalTranslationRoute route,
        CancellationToken cancellationToken = default)
    {
        if (route.Models.Count != 1)
            throw new NotSupportedException("Hy-MT2 routes must use exactly one multilingual model.");

        var serverPath = RequireFile(options.ServerExecutablePath, "llama.cpp server executable");
        var model = route.Models[0];
        var modelArtifact = model.Artifacts.Single(
            artifact => artifact.Role == LocalModelArtifactRole.Model);
        var modelPath = RequireFile(
            Path.Combine(options.ModelRoot, model.ModelId, model.Version, modelArtifact.FileName),
            "Hy-MT2 GGUF model");
        var port = ReserveLoopbackPort();
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverPath)!,
        };
        foreach (var argument in new[]
                 {
                     "-m", modelPath,
                     "--host", IPAddress.Loopback.ToString(),
                     "--port", port.ToString(),
                     "-c", "2048",
                     "-t", "4",
                     "--parallel", "1",
                     "--jinja",
                 })
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Unable to start the llama.cpp worker process.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        var session = new LlamaCppSession(
            process,
            new Uri($"http://127.0.0.1:{port}/"),
            route.SourceLanguage,
            route.TargetLanguage,
            options.RequestTimeout);
        try
        {
            await session.WaitUntilReadyAsync(options.InitializationTimeout, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string RequireFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException($"The {description} was not found.", fullPath);
    }

    private sealed class LlamaCppSession : ILocalModelSession
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _requests = new(1, 1);
        private readonly string _sourceLanguage;
        private readonly string _targetLanguage;
        private bool _disposed;

        public LlamaCppSession(
            Process process,
            Uri baseAddress,
            string sourceLanguage,
            string targetLanguage,
            TimeSpan requestTimeout)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            _http = new HttpClient { BaseAddress = baseAddress, Timeout = requestTimeout };
            _sourceLanguage = sourceLanguage;
            _targetLanguage = targetLanguage;
        }

        public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            while (true)
            {
                EnsureRunning();
                try
                {
                    using var response = await _http.GetAsync("health", timeoutCancellation.Token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, timeoutCancellation.Token);
            }
        }

        public async Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _requests.WaitAsync(cancellationToken);
            try
            {
                var translations = new string[texts.Count];
                for (var index = 0; index < texts.Count; index++)
                    translations[index] = await TranslateOneAsync(texts[index], cancellationToken);
                return translations;
            }
            finally
            {
                _requests.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            StopWorker();
            _http.Dispose();
            _process.Dispose();
            _requests.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task<string> TranslateOneAsync(string text, CancellationToken cancellationToken)
        {
            EnsureRunning();
            var targetInstruction = _targetLanguage == "ZH-HANT"
                ? "繁體中文（台灣）。必須使用繁體中文字，禁止輸出簡體字"
                : LanguageName(_targetLanguage);
            var prompt =
                $"將以下{LanguageName(_sourceLanguage)}文字翻譯為{targetInstruction}。" +
                $"只需要輸出翻譯後的結果，不要額外解釋：\n\n{text}";
            var request = new
            {
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.7,
                top_p = 0.6,
                top_k = 20,
                repeat_penalty = 1.05,
                seed = 47,
                max_tokens = 512,
                stream = false,
            };
            using var response = await _http.PostAsJsonAsync(
                "v1/chat/completions", request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Hy-MT2 worker returned {(int)response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString()?.Trim()
                   ?? throw new InvalidDataException("Hy-MT2 worker returned an empty translation.");
        }

        private void EnsureRunning()
        {
            if (!_process.HasExited) return;
            var detail = _stderr.IsCompletedSuccessfully ? _stderr.Result.Trim() : string.Empty;
            throw new InvalidOperationException(
                $"llama.cpp worker exited unexpectedly with code {_process.ExitCode}." +
                (detail.Length == 0 ? string.Empty : $" Native detail: {detail}"));
        }

        private void StopWorker()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }

        private static string LanguageName(string language) => language switch
        {
            "EN" => "英語",
            "JA" => "日語",
            "KO" => "韓語",
            "ZH-HANT" => "繁體中文（台灣）",
            _ => language,
        };
    }
}
