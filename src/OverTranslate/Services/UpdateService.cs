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
    private const string StableChannel = "win";
    private const string BetaChannel = "beta";

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

    /// <param name="onApplying">
    /// Raised once the download has genuinely finished, before the (blocking, progress-less) apply
    /// step begins. Callers must not rely on <paramref name="onProgress"/> reaching 100 to detect
    /// this: Velopack budgets its progress across download/extract/delta-merge phases and regularly
    /// stops reporting partway (a delta update commonly ends around 70), which would otherwise leave
    /// the UI reading "downloading" while the update is already being applied. Awaited, so the
    /// caller can repaint before ApplyUpdatesAndRestart takes over the thread and closes the app.
    /// </param>
    public static async Task DownloadAndApplyAsync(
        UpdateInfo info, Action<int>? onProgress = null, Func<Task>? onApplying = null)
    {
        await info.Manager.DownloadUpdatesAsync(info.VelopackInfo, onProgress);

        if (onApplying is not null)
            await onApplying();

        info.Manager.ApplyUpdatesAndRestart(info.VelopackInfo);
    }

    private static UpdateManager CreateManager()
    {
        // 設 OVERTRANSLATE_CHANNEL=beta → 訂閱 beta 先行版管線；未設 → 穩定版 (win)。
        var envChannel = Environment.GetEnvironmentVariable("OVERTRANSLATE_CHANNEL");
        var isBeta = string.Equals(envChannel, BetaChannel, StringComparison.OrdinalIgnoreCase);
        var channel = isBeta ? BetaChannel : StableChannel;

        // beta 的 GitHub Release 會標記為 pre-release，需 prerelease:true 才找得到。
        var source = new GithubSource(GitHubRepoUrl, null, prerelease: isBeta);
        return new UpdateManager(source, new UpdateOptions { ExplicitChannel = channel });
    }
}
