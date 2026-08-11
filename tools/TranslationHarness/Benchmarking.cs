using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.VisualBasic.Devices;
using OverTranslate.Services;
using OverTranslate.Services.Providers;

namespace OverTranslate.TranslationHarness;

public sealed record BenchmarkOptions(
    int Runs,
    int WarmupRuns,
    IReadOnlyList<int> BatchSizes,
    TimeSpan Timeout,
    string HardwareProfile,
    double? DurationSeconds = null);

public sealed record EnvironmentSnapshot(
    string HardwareProfile,
    string Cpu,
    int LogicalProcessors,
    ulong TotalPhysicalMemoryBytes,
    string OperatingSystem,
    string Framework,
    string ProcessArchitecture,
    bool Sse2,
    bool Avx2,
    bool Avx512F);

public sealed record TranslationOutput(
    string CaseId,
    string SourceText,
    string ReferenceTranslation,
    string CandidateTranslation);

public sealed record BenchmarkResult(
    string SourceLanguage,
    string TargetLanguage,
    int BatchSize,
    int CaseCount,
    int RequestCount,
    int SuccessfulRequests,
    int FailedRequests,
    bool FirstTranslationSucceeded,
    double FirstTranslationMs,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double MaxMs,
    double MeanCpuPercent,
    double ElapsedSeconds,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PeakWorkingSetBytes,
    IReadOnlyList<TranslationOutput> Outputs,
    IReadOnlyList<string> Errors);

public sealed record BenchmarkReport(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string Provider,
    double ProviderInitializationMs,
    string CorpusId,
    string CorpusVersion,
    string CorpusSha256,
    EnvironmentSnapshot Environment,
    BenchmarkOptions Options,
    IReadOnlyList<BenchmarkResult> Results);

public static class BenchmarkRunner
{
    public static async Task<BenchmarkReport> RunAsync(
        TranslationCorpus corpus,
        string corpusPath,
        string providerName,
        ITranslationProvider provider,
        BenchmarkOptions options,
        double providerInitializationMs = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var results = new List<BenchmarkResult>();

        foreach (var direction in corpus.Cases.GroupBy(
                     item => (item.SourceLanguage.ToUpperInvariant(), item.TargetLanguage.ToUpperInvariant())))
        {
            foreach (var batchSize in options.BatchSizes)
            {
                results.Add(await RunDirectionAsync(
                    direction.ToList(), direction.Key.Item1, direction.Key.Item2, batchSize,
                    provider, options, cancellationToken));
            }
        }

        await using var corpusStream = File.OpenRead(corpusPath);
        var corpusHash = Convert.ToHexString(await SHA256.HashDataAsync(corpusStream, cancellationToken));

        return new BenchmarkReport(
            3,
            DateTimeOffset.UtcNow,
            providerName,
            providerInitializationMs,
            corpus.CorpusId,
            corpus.CorpusVersion,
            corpusHash,
            CaptureEnvironment(options.HardwareProfile),
            options,
            results);
    }

    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        if (percentile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));

        var ordered = values.OrderBy(value => value).ToArray();
        var index = percentile * (ordered.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return ordered[lower];
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
    }

    private static async Task<BenchmarkResult> RunDirectionAsync(
        IReadOnlyList<TranslationCase> cases,
        string sourceLanguage,
        string targetLanguage,
        int batchSize,
        ITranslationProvider provider,
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var batches = cases.Chunk(batchSize).Select(chunk => chunk.ToArray()).ToArray();
        var first = await TryTranslateBatchAsync(
            batches[0], sourceLanguage, targetLanguage, provider, options.Timeout, cancellationToken);

        for (var warmup = 0; warmup < options.WarmupRuns; warmup++)
            foreach (var batch in batches)
                await TryTranslateBatchAsync(
                    batch, sourceLanguage, targetLanguage, provider, options.Timeout, cancellationToken);

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var cpuBefore = process.TotalProcessorTime;
        var wallClock = Stopwatch.StartNew();
        var peakWorkingSet = workingSetBefore;
        var latencies = new List<double>();
        var successfulRequests = 0;
        IReadOnlyList<TranslationOutput> outputs = [];
        var errors = new List<string>();

        using var samplerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampler = SampleWorkingSetAsync(process, value => peakWorkingSet = Math.Max(peakWorkingSet, value),
            samplerCancellation.Token);

        try
        {
            var run = 0;
            do
            {
                var captured = new List<TranslationOutput>();
                foreach (var batch in batches)
                {
                    var measured = await TryTranslateBatchAsync(
                        batch, sourceLanguage, targetLanguage, provider, options.Timeout, cancellationToken);
                    latencies.Add(measured.Elapsed.TotalMilliseconds);
                    if (!measured.Succeeded)
                    {
                        errors.Add($"run={run + 1} batch={Array.IndexOf(batches, batch) + 1}: {measured.Error}");
                        continue;
                    }

                    successfulRequests++;

                    if (run == 0)
                    {
                        captured.AddRange(batch.Zip(measured.Translations, (item, translation) =>
                            new TranslationOutput(
                                item.Id, item.SourceText, item.ReferenceTranslation, translation)));
                    }
                }

                if (run == 0) outputs = captured;
                run++;
            } while (options.DurationSeconds is { } durationSeconds
                         ? wallClock.Elapsed.TotalSeconds < durationSeconds
                         : run < options.Runs);
        }
        finally
        {
            wallClock.Stop();
            samplerCancellation.Cancel();
            await sampler;
        }

        process.Refresh();
        var workingSetAfter = process.WorkingSet64;
        peakWorkingSet = Math.Max(peakWorkingSet, workingSetAfter);
        var cpuDelta = process.TotalProcessorTime - cpuBefore;
        var cpuPercent = wallClock.Elapsed.TotalMilliseconds <= 0
            ? 0
            : cpuDelta.TotalMilliseconds / wallClock.Elapsed.TotalMilliseconds /
              Environment.ProcessorCount * 100;

        return new BenchmarkResult(
            sourceLanguage,
            targetLanguage,
            batchSize,
            cases.Count,
            latencies.Count,
            successfulRequests,
            latencies.Count - successfulRequests,
            first.Succeeded,
            first.Elapsed.TotalMilliseconds,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.90),
            Percentile(latencies, 0.95),
            latencies.Max(),
            cpuPercent,
            wallClock.Elapsed.TotalSeconds,
            workingSetBefore,
            workingSetAfter,
            peakWorkingSet,
            outputs,
            errors);
    }

    private static async Task<BatchAttempt> TryTranslateBatchAsync(
        IReadOnlyList<TranslationCase> batch,
        string sourceLanguage,
        string targetLanguage,
        ITranslationProvider provider,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var blocks = batch.Select((item, index) =>
            new OcrTextBlock(item.SourceText, new System.Windows.Rect(0, index * 30, 100, 20))).ToList();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (translated, _) = await provider.TranslateAsync(
                blocks, sourceLanguage, targetLanguage, "", timeoutCancellation.Token);
            stopwatch.Stop();

            if (translated.Count != blocks.Count)
                return new BatchAttempt(
                    stopwatch.Elapsed, false, [],
                    $"Provider returned {translated.Count} blocks for a batch of {blocks.Count}.");

            return new BatchAttempt(
                stopwatch.Elapsed, true, translated.Select(item => item.TranslatedText).ToArray(), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new BatchAttempt(stopwatch.Elapsed, false, [], $"Timed out after {timeout}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new BatchAttempt(
                stopwatch.Elapsed, false, [], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task SampleWorkingSetAsync(
        Process process,
        Action<long> observe,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                process.Refresh();
                observe(process.WorkingSet64);
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static EnvironmentSnapshot CaptureEnvironment(string hardwareProfile)
    {
        var cpu = Registry.LocalMachine
            .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")
            ?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unknown";

        return new EnvironmentSnapshot(
            hardwareProfile,
            cpu,
            Environment.ProcessorCount,
            new ComputerInfo().TotalPhysicalMemory,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Sse2.IsSupported,
            Avx2.IsSupported,
            Avx512F.IsSupported);
    }

    private static void ValidateOptions(BenchmarkOptions options)
    {
        if (options.Runs < 1) throw new ArgumentOutOfRangeException(nameof(options.Runs));
        if (options.WarmupRuns < 0) throw new ArgumentOutOfRangeException(nameof(options.WarmupRuns));
        if (options.BatchSizes.Count == 0 || options.BatchSizes.Any(size => size < 1))
            throw new ArgumentException("Batch sizes must all be positive.", nameof(options.BatchSizes));
        if (options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.Timeout));
        if (options.DurationSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.DurationSeconds));
        if (string.IsNullOrWhiteSpace(options.HardwareProfile))
            throw new ArgumentException("A hardware profile name is required.", nameof(options.HardwareProfile));
    }

    private sealed record BatchAttempt(
        TimeSpan Elapsed,
        bool Succeeded,
        IReadOnlyList<string> Translations,
        string? Error);
}
