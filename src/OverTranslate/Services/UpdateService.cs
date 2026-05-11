using Velopack;
using Velopack.Sources;
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace OverTranslate.Services;

public sealed record UpdateInfo(
    string LatestVersion,
    UpdateManager Manager,
    VelopackUpdateInfo VelopackInfo);

public static class UpdateService
{
    private const string GitHubRepoUrl = "https://github.com/asd880921/OverTranslate";

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var manager = CreateManager();
            // In-app updates are only available for the installed Velopack build.
            if (!manager.IsInstalled)
                return null;

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return null;

            return new UpdateInfo(
                update.TargetFullRelease.Version.ToString(),
                manager,
                update);
        }
        catch
        {
            return null;
        }
    }

    public static async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        await info.Manager.DownloadUpdatesAsync(info.VelopackInfo);
        info.Manager.ApplyUpdatesAndRestart(info.VelopackInfo);
    }

    private static UpdateManager CreateManager() =>
        new(new GithubSource(GitHubRepoUrl, null, false));
}
