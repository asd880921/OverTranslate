using System.Text;
using System.Text.Json;
using OverTranslate.TranslationHarness;
using Xunit;

namespace TranslationHarness.Tests;

public sealed class CorpusLoaderTests
{
    [Fact]
    public async Task LoadAsync_AcceptsVersionedDeidentifiedCorpus()
    {
        var path = await WriteCorpusAsync(new TranslationCorpus(
            1, "test", "1.0.0", true, null,
            [new TranslationCase("one", "subtitle", "EN", "ZH-HANT", "Hello", "你好") ]));

        try
        {
            var corpus = await CorpusLoader.LoadAsync(path);

            Assert.Equal("test", corpus.CorpusId);
            Assert.Single(corpus.Cases);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsCorpusThatIsNotMarkedDeidentified()
    {
        var path = await WriteCorpusAsync(new TranslationCorpus(
            1, "test", "1.0.0", false, null,
            [new TranslationCase("one", "subtitle", "EN", "ZH-HANT", "Hello", "你好") ]));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => CorpusLoader.LoadAsync(path));

            Assert.Contains("deidentified must be true", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsDuplicateCaseIds()
    {
        var item = new TranslationCase("duplicate", "ui", "EN", "ZH-HANT", "Retry", "重試");
        var path = await WriteCorpusAsync(new TranslationCorpus(
            1, "test", "1.0.0", true, null, [item, item]));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => CorpusLoader.LoadAsync(path));

            Assert.Contains("Duplicate case id", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteCorpusAsync(TranslationCorpus corpus)
    {
        var path = Path.Combine(Path.GetTempPath(), $"translation-corpus-{Guid.NewGuid():N}.json");
        var json = JsonSerializer.Serialize(corpus, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        return path;
    }
}
