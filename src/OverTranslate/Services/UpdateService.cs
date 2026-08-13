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

    // How many times the faked check has been asked; see OVERTRANSLATE_FAKE_UPDATE_AFTER.
    private static int _fakeCheckCount;

    public static async Task<UpdateInfo?> CheckAsync()
    {
        var fake = CreateFakeUpdate();
        if (fake is not null)
            return fake;

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

    /// <summary>
    /// Pretends a release exists, when OVERTRANSLATE_FAKE_UPDATE names a version. Returns null —
    /// meaning "check for real" — when it does not.
    /// </summary>
    /// <remarks>
    /// Everything the update notification does now — the startup dialog, 跳過此版本, the nav rail's
    /// row, the periodic re-check — is decided before a single byte is downloaded, and none of it
    /// can be exercised without a release to react to. Without this hook the only ways to see any of
    /// it are publishing to GitHub, which pushes the release at real users, or installing a private
    /// build, which cannot show the "no update yet" side at all. It also unblocks running from the
    /// IDE, where <see cref="UpdateManager.IsInstalled"/> is false and the real check therefore
    /// always answers null.
    ///
    /// An environment variable rather than a setting or a #if, matching OVERTRANSLATE_CHANNEL below:
    /// it does nothing whatsoever unless deliberately set, it needs no build of its own, and it can
    /// be pointed at a Release build. Downloading is the one thing it cannot fake — there is no
    /// package behind the version it invents, so 立即更新 will fail and report so. That path is
    /// unchanged by this work; test it against a real release.
    ///
    /// OVERTRANSLATE_FAKE_UPDATE_AFTER holds the release back for that many checks, which is the
    /// only way to see the half that matters most: a release that did not exist at launch has to
    /// reach the nav rail of an already-open window without a dialog appearing. Left unset, the
    /// release is there from the first check and the startup dialog is what you get.
    /// </remarks>
    private static UpdateInfo? CreateFakeUpdate()
    {
        var version = Environment.GetEnvironmentVariable("OVERTRANSLATE_FAKE_UPDATE");
        if (string.IsNullOrWhiteSpace(version))
            return null;

        if (!SemanticVersion.TryParse(version, out var parsed))
            return null;

        var checksSoFar = Interlocked.Increment(ref _fakeCheckCount);
        if (int.TryParse(Environment.GetEnvironmentVariable("OVERTRANSLATE_FAKE_UPDATE_AFTER"), out var after)
            && checksSoFar <= after)
        {
            return null;
        }

        var asset = new VelopackAsset
        {
            PackageId = "OverTranslate",
            Version = parsed,
            Type = VelopackAssetType.Full,
            FileName = $"OverTranslate-{parsed}-full.nupkg"
        };

        return new UpdateInfo(
            parsed.ToString(),
            CreateManager(),
            new VelopackUpdateInfo(asset, false, null!, []));
    }

    /// <remarks>
    /// OVERTRANSLATE_UPDATE_SOURCE overrides where releases are fetched from, so the download and
    /// apply half of an update can be exercised on one machine. It is the missing counterpart to
    /// <see cref="CreateFakeUpdate"/>: that one invents a version with no package behind it, so
    /// 立即更新 necessarily fails there. Point this at a `vpk pack --outputDir` folder holding two
    /// versions and the whole thing runs for real — check, download, apply, restart — against a feed
    /// nobody else can see.
    ///
    /// The alternative was publishing a GitHub Release to test against, which is not a test: this app
    /// updates from GitHub Releases, so every such release ships to every user on the channel. There
    /// is no draft or staging release Velopack can read.
    ///
    /// Anything <see cref="UpdateManager"/> accepts works — a local directory, or an http(s) feed.
    /// The channel still applies: the source must contain releases.{channel}.json. Unset, which is
    /// the only state a user's machine is ever in, this does nothing and GitHub is used as before.
    /// </remarks>
    private static UpdateManager CreateManager()
    {
        // 設 OVERTRANSLATE_CHANNEL=beta → 訂閱 beta 先行版管線；未設 → 穩定版 (win)。
        var envChannel = Environment.GetEnvironmentVariable("OVERTRANSLATE_CHANNEL");
        var isBeta = string.Equals(envChannel, BetaChannel, StringComparison.OrdinalIgnoreCase);
        var channel = isBeta ? BetaChannel : StableChannel;
        var options = new UpdateOptions { ExplicitChannel = channel };

        var localSource = Environment.GetEnvironmentVariable("OVERTRANSLATE_UPDATE_SOURCE");
        if (!string.IsNullOrWhiteSpace(localSource))
            return new UpdateManager(localSource, options);

        // beta 的 GitHub Release 會標記為 pre-release，需 prerelease:true 才找得到。
        var source = new GithubSource(GitHubRepoUrl, null, prerelease: isBeta);
        return new UpdateManager(source, options);
    }
}
