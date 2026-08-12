using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OverTranslate.Services.LocalNmt;

public sealed record BergamotWorkerOptions(
    string WorkerExecutablePath,
    string NativeLibraryPath,
    string ModelRoot,
    TimeSpan InitializationTimeout,
    TimeSpan RequestTimeout)
{
    public static BergamotWorkerOptions Create(
        string workerExecutablePath,
        string nativeLibraryPath,
        string modelRoot) => new(
            Path.GetFullPath(workerExecutablePath),
            Path.GetFullPath(nativeLibraryPath),
            Path.GetFullPath(modelRoot),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(30));
}

/// <summary>Starts one crash-isolated Bergamot worker for each resolved direct or pivot route.</summary>
public sealed class BergamotWorkerSessionFactory(BergamotWorkerOptions options) : ILocalModelSessionFactory
{
    public async Task<ILocalModelSession> CreateAsync(
        LocalTranslationRoute route,
        CancellationToken cancellationToken = default)
    {
        if (route.Models.Count is < 1 or > 2)
            throw new NotSupportedException(
                $"Bergamot worker supports direct or one-pivot routes, not {route.Models.Count} models.");

        var workerPath = RequireFile(options.WorkerExecutablePath, "Bergamot worker executable");
        var nativePath = RequireFile(options.NativeLibraryPath, "Bergamot native library");
        var configs = route.Models.Select(model => RequireFile(
            Path.Combine(options.ModelRoot, model.ModelId, "config.yml"),
            $"model config for {model.ModelId}")).ToArray();

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = options.ModelRoot,
        };
        startInfo.ArgumentList.Add("--bergamot-worker");
        startInfo.ArgumentList.Add("--native-library");
        startInfo.ArgumentList.Add(nativePath);
        startInfo.ArgumentList.Add("--model-config");
        startInfo.ArgumentList.Add(configs[0]);
        if (configs.Length == 2)
        {
            startInfo.ArgumentList.Add("--pivot-model-config");
            startInfo.ArgumentList.Add(configs[1]);
        }

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Unable to start the Bergamot worker process.");
        var session = new BergamotWorkerSession(process, options.RequestTimeout);
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

    private static string RequireFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException($"The {description} was not found.", fullPath);
    }

    private sealed class BergamotWorkerSession : ILocalModelSession
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly SemaphoreSlim _requests = new(1, 1);
        private readonly TimeSpan _requestTimeout;
        private long _nextRequestId;
        private bool _disposed;

        public BergamotWorkerSession(Process process, TimeSpan requestTimeout)
        {
            _process = process;
            _requestTimeout = requestTimeout;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var ready = await ReadMessageAsync(timeout, cancellationToken);
            if (ready.Type != "ready")
                throw new InvalidOperationException(
                    ready.Error ?? "Bergamot worker returned an invalid initialization response.");
        }

        public async Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _requests.WaitAsync(cancellationToken);
            try
            {
                EnsureRunning();
                var requestId = Interlocked.Increment(ref _nextRequestId);
                var request = new WorkerMessage("translate", requestId, texts.ToArray());
                await _process.StandardInput.WriteLineAsync(
                    JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);

                WorkerMessage response;
                try
                {
                    response = await ReadMessageAsync(_requestTimeout, cancellationToken);
                }
                catch
                {
                    StopWorker();
                    throw;
                }

                if (response.Type == "error")
                    throw new InvalidOperationException(
                        response.Error ?? "Bergamot worker reported an unknown translation error.");
                if (response.Type != "result" || response.Id != requestId || response.Translations is null)
                    throw new InvalidDataException("Bergamot worker returned an invalid response.");
                return response.Translations;
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
            _process.Dispose();
            _requests.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task<WorkerMessage> ReadMessageAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync(timeoutCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Bergamot worker did not respond within {timeout.TotalSeconds:F0} seconds.");
            }

            if (line is null) throw new InvalidOperationException(await DescribeExitAsync());
            return JsonSerializer.Deserialize<WorkerMessage>(line)
                   ?? throw new InvalidDataException("Bergamot worker returned empty JSON.");
        }

        private void EnsureRunning()
        {
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"Bergamot worker exited unexpectedly with code {_process.ExitCode}; " +
                    "the runtime or model may be damaged or incompatible.");
        }

        private async Task<string> DescribeExitAsync()
        {
            await _process.WaitForExitAsync();
            var detail = (await _stderr).Trim();
            return $"Bergamot worker exited unexpectedly with code {_process.ExitCode}." +
                   (detail.Length == 0 ? string.Empty : $" Native detail: {detail}");
        }

        private void StopWorker()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
    }

    private sealed record WorkerMessage(
        string Type,
        long Id = 0,
        string[]? Texts = null,
        string? SourceLanguage = null,
        string? TargetLanguage = null,
        string[]? Translations = null,
        string? Error = null);
}
