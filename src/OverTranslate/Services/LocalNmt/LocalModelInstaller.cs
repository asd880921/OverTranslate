using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

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
                await DownloadAsync(artifact, destination, cancellationToken);
                if (!await MatchesAsync(destination, artifact, cancellationToken))
                    throw new InvalidDataException(
                        $"Downloaded model artifact failed verification: {artifact.FileName}.");
                progress?.Report(new LocalModelInstallProgress(
                    model.ModelId, artifact.FileName, index + 1, model.Artifacts.Count));
            }

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

    private async Task DownloadAsync(
        LocalModelArtifact artifact,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            artifact.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        if (artifact.DownloadUri.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            await gzip.CopyToAsync(output, cancellationToken);
        }
        else
        {
            await input.CopyToAsync(output, cancellationToken);
        }
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

    private static void EnsureSafeFileName(string fileName)
    {
        if (!Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal) ||
            fileName is "." or "..")
            throw new InvalidDataException($"Unsafe model artifact name: {fileName}.");
    }
}
