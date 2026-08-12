using System.IO;
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
            Assert.Equal(
                "model payload",
                await File.ReadAllTextAsync(Path.Combine(installed, "model.bin")));
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

    [Fact]
    public async Task Install_CancelledBeforeDownloadLeavesNoPublishedOrPartialVersion()
    {
        var fixture = CreateFixture();
        try
        {
            var installer = new LocalModelInstaller(fixture.Http, fixture.Root);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                installer.InstallAsync(fixture.Model, cancellationToken: cancellation.Token));

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
        };
        if (corruptHash) artifacts[0] = artifacts[0] with { UncompressedSha256 = new string('0', 64) };
        foreach (var artifact in artifacts)
            files[artifact.DownloadUri] = "model payload"u8.ToArray();
        var client = new HttpClient(new DictionaryHandler(files));
        var model = new LocalModelDescriptor("test-model", "v1", "EN", "ZH-HANT", artifacts);
        return new Fixture(root, client, model);
    }

    private static LocalModelArtifact Artifact(
        LocalModelArtifactRole role,
        string name,
        byte[] content) => new(
            role,
            new Uri($"https://models.test/{name}"),
            name,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

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
