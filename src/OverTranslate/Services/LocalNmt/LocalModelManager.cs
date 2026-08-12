namespace OverTranslate.Services.LocalNmt;

public sealed record LocalModelStatus(int InstalledModels, int TotalModels, long InstalledBytes)
{
    public bool IsComplete => InstalledModels == TotalModels;
}

public sealed record LocalModelManagerProgress(
    int CompletedFiles,
    int TotalFiles,
    string ModelId,
    string FileName);

/// <summary>Application-facing model management; views never handle URLs, hashes or files.</summary>
public sealed class LocalModelManager(LocalModelCatalog catalog, LocalModelInstaller installer)
{
    public long TotalUncompressedBytes => catalog.Models
        .SelectMany(model => model.Artifacts)
        .Sum(artifact => artifact.UncompressedSize);

    public async Task<LocalModelStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var installed = 0;
        long bytes = 0;
        foreach (var model in catalog.Models)
        {
            if (!await installer.IsInstalledAsync(model, cancellationToken)) continue;
            installed++;
            bytes += model.Artifacts.Sum(artifact => artifact.UncompressedSize);
        }
        return new LocalModelStatus(installed, catalog.Models.Count, bytes);
    }

    public async Task InstallAllAsync(
        IProgress<LocalModelManagerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var totalFiles = catalog.Models.Sum(model => model.Artifacts.Count);
        var completedFiles = 0;
        foreach (var model in catalog.Models)
        {
            var filesBeforeModel = completedFiles;
            var modelProgress = new Progress<LocalModelInstallProgress>(item =>
                progress?.Report(new LocalModelManagerProgress(
                    filesBeforeModel + item.CompletedFiles,
                    totalFiles,
                    item.ModelId,
                    item.FileName)));
            await installer.InstallAsync(model, modelProgress, cancellationToken);
            completedFiles += model.Artifacts.Count;
        }
    }
}
