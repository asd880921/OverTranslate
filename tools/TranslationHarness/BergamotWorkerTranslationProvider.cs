using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Providers;

namespace OverTranslate.TranslationHarness;

public sealed class BergamotWorkerTranslationProvider :
    ITranslationProvider, IBenchmarkProcessResources, IDisposable
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(60);
    private readonly Process _worker;
    private readonly Task<string> _stderr;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly TimeSpan _requestTimeout;
    private ProcessResourceSnapshot _lastProcessResources;
    private long _nextRequestId;
    private string? _terminalFailure;
    private bool _disposed;

    public BergamotWorkerTranslationProvider(
        string nativeLibraryPath,
        string modelConfigPath,
        string? pivotModelConfigPath,
        TimeSpan requestTimeout)
    {
        BergamotDeploymentValidator.Validate(
            nativeLibraryPath, modelConfigPath, pivotModelConfigPath);
        _requestTimeout = requestTimeout;
        _worker = StartWorker(nativeLibraryPath, modelConfigPath, pivotModelConfigPath);
        _stderr = _worker.StandardError.ReadToEndAsync();

        try
        {
            var ready = ReadMessageAsync(InitializationTimeout, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (ready.Type != "ready")
                throw new InvalidOperationException(
                    ready.Error ?? "Bergamot worker returned an invalid initialization response.");
        }
        catch
        {
            StopWorker();
            throw;
        }
    }

    public bool RequiresApiKey => false;

    public ProcessResourceSnapshot CaptureProcessResources()
    {
        if (_worker.HasExited) return _lastProcessResources;
        try
        {
            _worker.Refresh();
            _lastProcessResources = new ProcessResourceSnapshot(
                _worker.WorkingSet64, _worker.TotalProcessorTime);
        }
        catch (InvalidOperationException) when (_worker.HasExited)
        {
        }
        return _lastProcessResources;
    }

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks,
        string sourceLang,
        string targetLang,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requests.WaitAsync(cancellationToken);
        try
        {
            EnsureWorkerIsRunning("before translation");
            var requestId = Interlocked.Increment(ref _nextRequestId);
            var request = new BergamotWorkerHost.WorkerMessage(
                "translate", requestId, blocks.Select(block => block.Text).ToArray(),
                sourceLang, targetLang);
            await _worker.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
            await _worker.StandardInput.FlushAsync(cancellationToken);

            BergamotWorkerHost.WorkerMessage response;
            try
            {
                response = await ReadMessageAsync(_requestTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _terminalFailure = "was terminated after a timeout or cancellation";
                StopWorker();
                throw;
            }
            catch (TimeoutException)
            {
                _terminalFailure = "was terminated after a timeout";
                StopWorker();
                throw;
            }

            if (response.Type == "error")
                throw new InvalidOperationException(
                    response.Error ?? "Bergamot worker reported an unknown translation error.");
            if (response.Type != "result" || response.Id != requestId || response.Translations is null)
                throw new InvalidDataException("Bergamot worker returned an invalid response.");
            if (response.Translations.Length != blocks.Count)
                throw new InvalidDataException("Bergamot worker returned an unexpected response count.");

            var translated = blocks.Zip(response.Translations, (block, translation) =>
                new TranslatedBlock(
                    block.Text, translation, block.Bounds,
                    block.SourceLineBounds, block.SourceGlyphHeight)).ToList();
            return (translated, sourceLang);
        }
        finally
        {
            _requests.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWorker();
        _worker.Dispose();
        _requests.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<BergamotWorkerHost.WorkerMessage> ReadMessageAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        string? line;
        try
        {
            line = await _worker.StandardOutput.ReadLineAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Bergamot worker did not respond within {timeout.TotalSeconds:F0} seconds.");
        }

        if (line is null)
            throw new InvalidOperationException(await DescribeUnexpectedExitAsync());
        return JsonSerializer.Deserialize<BergamotWorkerHost.WorkerMessage>(line)
               ?? throw new InvalidDataException("Bergamot worker returned empty JSON.");
    }

    private void EnsureWorkerIsRunning(string stage)
    {
        if (_worker.HasExited)
            throw new InvalidOperationException(
                _terminalFailure is null
                    ? $"Bergamot worker exited unexpectedly {stage} with code {_worker.ExitCode}. " +
                      "The native runtime or model may be damaged or incompatible."
                    : $"Bergamot worker {_terminalFailure}; create a new provider before retrying.");
    }

    private async Task<string> DescribeUnexpectedExitAsync()
    {
        await _worker.WaitForExitAsync();
        var stderr = (await _stderr).Trim();
        var detail = stderr.Length == 0 ? string.Empty : $" Native detail: {stderr}";
        return $"Bergamot worker exited unexpectedly with code {_worker.ExitCode}. " +
               $"The native runtime or model may be missing, damaged, or incompatible.{detail}";
    }

    private void StopWorker()
    {
        if (!_worker.HasExited)
            _worker.Kill(entireProcessTree: true);
    }

    private static Process StartWorker(
        string nativeLibraryPath,
        string modelConfigPath,
        string? pivotModelConfigPath)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Unable to locate the harness executable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Unable to locate the harness assembly."));
        startInfo.ArgumentList.Add(BergamotWorkerHost.Command);
        startInfo.ArgumentList.Add("--native-library");
        startInfo.ArgumentList.Add(Path.GetFullPath(nativeLibraryPath));
        startInfo.ArgumentList.Add("--model-config");
        startInfo.ArgumentList.Add(Path.GetFullPath(modelConfigPath));
        if (pivotModelConfigPath is not null)
        {
            startInfo.ArgumentList.Add("--pivot-model-config");
            startInfo.ArgumentList.Add(Path.GetFullPath(pivotModelConfigPath));
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Unable to start the Bergamot worker process.");
    }
}
