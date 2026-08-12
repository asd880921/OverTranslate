using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using OverTranslate.Services.LocalNmt;
using Xunit;

namespace OverTranslate.Tests;

public class LocalModelInstallerTests
{
    [Fact]
    public async Task Install_VerifiesArtifactsAndPublishesCompleteVersionAtomically()
    {
        var fixture = CreateFixture();
        try
        {
            var installer = new LocalModelInstaller(fixture.Http, fixture.Root);

            var installed = await installer.InstallAsync(fixture.Model);

            Assert.True(await installer.IsInstalledAsync(fixture.Model));
            Assert.Equal(installed, installer.GetInstallDirectory(fixture.Model));
            var config = await File.ReadAllTextAsync(Path.Combine(installed, "config.yml"));
            Assert.Contains("beam-size: 1", config);
            Assert.Contains("model.bin", config);
            Assert.DoesNotContain(".partial", config);
            Assert.Empty(Directory.GetDirectories(
                Path.Combine(fixture.Root, fixture.Model.ModelId), "*.partial"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Install_HashMismatchLeavesNoPublishedOrPartialVersion()
    {
        var fixture = CreateFixture(corruptHash: true);
        try
        {
            var installer = new LocalModelInstaller(fixture.Http, fixture.Root);

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(fixture.Model));

            Assert.False(Directory.Exists(installer.GetInstallDirectory(fixture.Model)));
            Assert.Empty(Directory.GetDirectories(
                Path.Combine(fixture.Root, fixture.Model.ModelId), "*.partial"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static Fixture CreateFixture(bool corruptHash = false)
    {
        var root = Directory.CreateTempSubdirectory("overtranslate-model-test-").FullName;
        var files = new Dictionary<Uri, byte[]>();
        var artifacts = new[]
        {
            Artifact(LocalModelArtifactRole.Model, "model.bin", "model payload"u8.ToArray()),
            Artifact(LocalModelArtifactRole.Vocabulary, "vocab.spm", "vocabulary"u8.ToArray()),
            Artifact(LocalModelArtifactRole.LexicalShortlist, "lex.bin", "shortlist"u8.ToArray()),
        };
        if (corruptHash) artifacts[0] = artifacts[0] with { UncompressedSha256 = new string('0', 64) };
        foreach (var artifact in artifacts)
            files[artifact.DownloadUri] = Compress(artifact.FileName switch
            {
                "model.bin" => "model payload"u8.ToArray(),
                "vocab.spm" => "vocabulary"u8.ToArray(),
                _ => "shortlist"u8.ToArray(),
            });
        var client = new HttpClient(new DictionaryHandler(files));
        var model = new LocalModelDescriptor("test-model", "v1", "EN", "ZH-HANT", artifacts);
        return new Fixture(root, client, model);
    }

    private static LocalModelArtifact Artifact(
        LocalModelArtifactRole role,
        string name,
        byte[] content) => new(
            role,
            new Uri($"https://models.test/{name}.gz"),
            name,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    private static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(content);
        return output.ToArray();
    }

    private sealed record Fixture(string Root, HttpClient Http, LocalModelDescriptor Model) : IDisposable
    {
        public void Dispose()
        {
            Http.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class DictionaryHandler(IReadOnlyDictionary<Uri, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null && files.TryGetValue(request.RequestUri, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
