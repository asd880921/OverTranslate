using OverTranslate.TranslationHarness;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace TranslationHarness.Tests;

public sealed class BenchmarkRunnerTests
{
    [Fact]
    public void Percentile_InterpolatesSortedValues()
    {
        double[] values = [40, 10, 30, 20];

        Assert.Equal(25, BenchmarkRunner.Percentile(values, 0.50));
        Assert.Equal(37, BenchmarkRunner.Percentile(values, 0.90), precision: 6);
        Assert.Equal(40, BenchmarkRunner.Percentile(values, 1));
    }

    [Fact]
    public void Percentile_EmptyInputReturnsZero()
    {
        Assert.Equal(0, BenchmarkRunner.Percentile([], 0.95));
    }

    [Fact]
    public async Task RunAsync_BatchesCasesAndPreservesOutputOrder()
    {
        var cases = Enumerable.Range(1, 5)
            .Select(index => new TranslationCase(
                $"case-{index}", "subtitle", "EN", "ZH-HANT", $"source-{index}", $"reference-{index}"))
            .ToArray();
        var corpus = new TranslationCorpus(1, "batch-test", "1.0.0", true, null, cases);
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "test corpus hash input");

        try
        {
            var provider = new EchoProvider();
            var report = await BenchmarkRunner.RunAsync(
                corpus,
                path,
                "echo",
                provider,
                new BenchmarkOptions(1, 0, [2], TimeSpan.FromSeconds(1), "test-machine"));

            var result = Assert.Single(report.Results);
            Assert.Equal(3, result.RequestCount);
            Assert.Equal(3, result.SuccessfulRequests);
            Assert.Equal(0, result.FailedRequests);
            Assert.Equal(cases.Select(item => item.Id), result.Outputs.Select(item => item.CaseId));
            Assert.Equal(4, provider.CallCount); // first translation plus three measured batches
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class EchoProvider : ITranslationProvider
    {
        public int CallCount { get; private set; }

        public bool RequiresApiKey => false;

        public Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
            List<OcrTextBlock> blocks,
            string sourceLang,
            string targetLang,
            string apiKey,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var translated = blocks.Select(block => new TranslatedBlock(
                block.Text, $"translated:{block.Text}", block.Bounds)).ToList();
            return Task.FromResult((translated, sourceLang));
        }
    }
}
