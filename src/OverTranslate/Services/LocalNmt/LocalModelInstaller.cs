using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace OverTranslate.Services.LocalNmt;

public sealed record LocalModelInstallProgress(
    string ModelId,
    string FileName,
    int CompletedFiles,
    int TotalFiles);

/// <summary>Downloads, verifies and atomically publishes one immutable model version.</summary>
public sealed class LocalModelInstaller(HttpClient http, string modelRoot)
{
    private readonly string _modelRoot = Path.GetFullPath(modelRoot);

    public string GetInstallDirectory(LocalModelDescriptor model) =>
        Path.Combine(_modelRoot, model.ModelId, model.Version);

    public async Task<bool> IsInstalledAsync(
        LocalModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        var directory = GetInstallDirectory(model);
        if (!File.Exists(Path.Combine(directory, "config.yml"))) return false;

        foreach (var artifact in model.Artifacts)
        {
            var path = Path.Combine(directory, artifact.FileName);
            if (!await MatchesAsync(path, artifact, cancellationToken)) return false;
        }

        return true;
    }

    public async Task<string> InstallAsync(
        LocalModelDescriptor model,
        IProgress<LocalModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await IsInstalledAsync(model, cancellationToken)) return GetInstallDirectory(model);

        var modelDirectory = Path.Combine(_modelRoot, model.ModelId);
        Directory.CreateDirectory(modelDirectory);
        var finalDirectory = GetInstallDirectory(model);
        var stagingDirectory = Path.Combine(
            modelDirectory, $".{model.Version}.{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            for (var index = 0; index < model.Artifacts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = model.Artifacts[index];
                EnsureSafeFileName(artifact.FileName);
                var destination = Path.Combine(stagingDirectory, artifact.FileName);
                await DownloadAndExpandAsync(artifact, destination, cancellationToken);
                if (!await MatchesAsync(destination, artifact, cancellationToken))
                    throw new InvalidDataException(
                        $"Downloaded model artifact failed verification: {artifact.FileName}.");
                progress?.Report(new LocalModelInstallProgress(
                    model.ModelId, artifact.FileName, index + 1, model.Artifacts.Count));
            }

            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, "config.yml"),
                BuildConfig(model, finalDirectory),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            try
            {
                Directory.Move(stagingDirectory, finalDirectory);
            }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                if (!await IsInstalledAsync(model, cancellationToken)) throw;
            }

            return finalDirectory;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private async Task DownloadAndExpandAsync(
        LocalModelArtifact artifact,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            artifact.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var compressed = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await gzip.CopyToAsync(output, cancellationToken);
    }

    private static async Task<bool> MatchesAsync(
        string path,
        LocalModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != artifact.UncompressedSize) return false;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return hash.Equals(artifact.UncompressedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildConfig(LocalModelDescriptor model, string directory)
    {
        var modelArtifact = model.Artifacts.Single(artifact => artifact.Role == LocalModelArtifactRole.Model);
        var vocabularies = model.Artifacts.Where(
            artifact => artifact.Role == LocalModelArtifactRole.Vocabulary).ToArray();
        var shortlist = model.Artifacts.Single(
            artifact => artifact.Role == LocalModelArtifactRole.LexicalShortlist);
        if (vocabularies.Length is < 1 or > 2)
            throw new InvalidDataException($"Model {model.ModelId} has an invalid vocabulary count.");

        string PathOf(LocalModelArtifact artifact) =>
            Path.Combine(directory, artifact.FileName).Replace('\\', '/');
        var sourceVocab = PathOf(vocabularies[0]);
        var targetVocab = PathOf(vocabularies.Length == 1 ? vocabularies[0] : vocabularies[1]);

        return string.Join("\r\n",
        [
            "models:",
            $"  - {PathOf(modelArtifact)}",
            "vocabs:",
            $"  - {sourceVocab}",
            $"  - {targetVocab}",
            "shortlist:",
            $"  - {PathOf(shortlist)}",
            "  - false",
            "beam-size: 1",
            "normalize: 1.0",
            "word-penalty: 0",
            "max-length-break: 128",
            "mini-batch-words: 1024",
            "workspace: 128",
            "max-length-factor: 2.0",
            "skip-cost: true",
            "cpu-threads: 0",
            "quiet: true",
            "quiet-translation: true",
            "gemm-precision: int8shiftAlphaAll",
            "alignment: soft",
            "ssplit-mode: paragraph",
            "",
        ]);
    }

    private static void EnsureSafeFileName(string fileName)
    {
        if (!Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal) ||
            fileName is "." or "..")
            throw new InvalidDataException($"Unsafe model artifact name: {fileName}.");
    }
}
