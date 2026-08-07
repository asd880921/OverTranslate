using System.Drawing;
using System.Drawing.Imaging;
using NLog;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// The continuous half of realtime translation: polls each watched region, and only when its pixels
/// have actually settled on something new does it pay for recognition and translation.
/// </summary>
/// <remarks>
/// This is a monitoring loop that may run for hours next to a game, so every stage exists to avoid
/// work rather than to do it:
/// <list type="bullet">
/// <item>a poll grabs a small rectangle and hashes it — an idle region costs only that;</item>
/// <item><see cref="RealtimeRegionState"/> decides when a changed frame is worth reading, so text
/// that is fading in or scrolling is recognised once rather than at every intermediate frame,
/// without ever waiting forever for a still frame that moving content will never produce;</item>
/// <item>recognised text identical to what is already on screen ends the pass without a network
/// call, which is the common case when only a background pixel moved;</item>
/// <item>a per-session cache means a line of dialogue that comes back (a repeated subtitle, a menu
/// the user reopens) is never translated twice.</item>
/// </list>
/// The OCR and translation services are handed in rather than created: they are the same instances
/// the screenshot flow uses, so the two features share one loaded ONNX runtime and one inference
/// queue instead of competing for the CPU with a second copy of each.
/// </remarks>
public sealed class RealtimeTranslationSession
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Fast enough that a subtitle appears to update as it changes, slow enough that the grab+hash
    // of a few small regions stays invisible in Task Manager.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Bounded so a long session on scrolling content cannot grow the cache without limit. Cleared
    // wholesale rather than evicted one by one: at this size the loss is one extra translation for
    // lines that are still on screen, and an LRU here would cost more to maintain than it saves.
    private const int TranslationCacheLimit = 400;

    private readonly OcrService _ocr;
    private readonly TranslationService _translation;

    private readonly Dictionary<string, string> _translationCache = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // A grab that keeps failing produces no work and no error, which is indistinguishable from a
    // region that simply has nothing in it. Reported once per session so a locked screen does not
    // fill the log at four lines a second.
    private bool _grabFailureReported;

    public RealtimeTranslationSession(OcrService ocr, TranslationService translation)
    {
        _ocr = ocr;
        _translation = translation;
    }

    /// <summary>Fresh lines for one region. Raised on a background thread.</summary>
    public event EventHandler<RealtimeRegionUpdate>? RegionUpdated;

    /// <summary>
    /// Raised when a pass fails in a way the user has to know about (a missing API key, an engine
    /// that is down). Raised on a background thread, and at most once per distinct message so a
    /// failure that repeats every poll does not turn into a stream of notifications.
    /// </summary>
    public event EventHandler<string>? Failed;

    /// <summary>True while <see cref="RunAsync"/> is doing something more than hashing pixels.</summary>
    public event EventHandler<bool>? BusyChanged;

    public void Start(IReadOnlyList<RealtimeRegion> regions, string sourceLanguage, string targetLanguage)
    {
        Stop();

        var cts = new CancellationTokenSource();
        _cts = cts;
        _grabFailureReported = false;
        // The whole loop is background work: the grab is a BitBlt, recognition is CPU-bound, and
        // translation is I/O. None of it belongs on the dispatcher, which has an interface to keep
        // responsive for as long as this runs.
        _loop = Task.Run(() => RunAsync([.. regions], sourceLanguage, targetLanguage, cts.Token), cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _loop = null;
        // Kept across a stop/edit/start cycle on purpose: the user usually comes back to the same
        // content, and re-translating lines we already have would be a visible pause for nothing.
    }

    private async Task RunAsync(
        List<RealtimeRegion> regions,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken token)
    {
        var states = regions.ToDictionary(region => region.Id, _ => new RealtimeRegionState());
        string? lastReportedFailure = null;

        Log.Info(
            "Realtime session started: {Count} region(s), {Src}->{Tgt}",
            regions.Count, sourceLanguage, targetLanguage);

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (var region in regions)
                {
                    token.ThrowIfCancellationRequested();

                    var state = states[region.Id];
                    using var frame = GrabRegion(region.Bounds);
                    if (frame is null) continue;

                    var signature = FrameSignature.Compute(frame);
                    if (!state.Observe(signature)) continue;

                    try
                    {
                        BusyChanged?.Invoke(this, true);
                        await ProcessRegionAsync(
                            region, frame, state, signature, sourceLanguage, targetLanguage, token);
                        lastReportedFailure = null;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One region failing must not end the session — the engine may be briefly
                        // unavailable, and the next poll is only 250ms away.
                        Log.Warn(ex, "Realtime pass failed for region {Region}", region.Id);
                        var message = DescribeFailure(ex);
                        if (message != lastReportedFailure)
                        {
                            lastReportedFailure = message;
                            Failed?.Invoke(this, message);
                        }
                    }
                    finally
                    {
                        BusyChanged?.Invoke(this, false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Realtime session stopped");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Realtime session ended unexpectedly");
            Failed?.Invoke(this, $"即時翻譯已中止：{ex.Message}");
        }
        finally
        {
            BusyChanged?.Invoke(this, false);
        }
    }

    private async Task ProcessRegionAsync(
        RealtimeRegion region,
        Bitmap frame,
        RealtimeRegionState state,
        ulong signature,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken token)
    {
        var recognized = await _ocr.RecognizeAsync(frame, sourceLanguage, token);
        token.ThrowIfCancellationRequested();

        var sourceText = string.Join('\n', recognized.Select(block => block.Text));

        // The pixels moved but the words did not — a cursor blinked, a background scrolled, a video
        // played on behind a caption. Record the frame so it is not examined again, and go no
        // further: this is the case that keeps a session over moving content off the network.
        if (sourceText == state.RenderedText)
        {
            state.MarkRendered(signature, sourceText);
            return;
        }

        if (recognized.Count == 0)
        {
            state.MarkRendered(signature, sourceText);
            RegionUpdated?.Invoke(this, new RealtimeRegionUpdate(region.Id, []));
            return;
        }

        var translated = await TranslateAsync(recognized, sourceLanguage, targetLanguage, token);
        token.ThrowIfCancellationRequested();

        state.MarkRendered(signature, sourceText);
        RegionUpdated?.Invoke(this, new RealtimeRegionUpdate(region.Id, translated));
    }

    /// <summary>
    /// Translates only the lines the session has not seen before, then reassembles the full result
    /// in the original order so the overlay always receives every line it has to draw.
    /// </summary>
    private async Task<List<TranslatedBlock>> TranslateAsync(
        List<OcrTextBlock> blocks,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken token)
    {
        // The service is part of the key: it cannot change mid-session today, but a cache that
        // silently outlived a change of engine would serve the old engine's wording forever.
        var cacheKeyPrefix = $"{SettingsService.Instance.Current.Provider}|{sourceLanguage}|{targetLanguage}|";
        var missing = blocks
            .Where(block => !_translationCache.ContainsKey(cacheKeyPrefix + block.Text))
            .ToList();

        if (missing.Count > 0)
        {
            var apiKey = SettingsService.Instance.Current.ApiKey;
            if (_translation.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("缺少 API Key，請先在設定中輸入。");

            var (results, _) = await _translation.TranslateAsync(
                missing, sourceLanguage, targetLanguage, apiKey, cancellationToken: token);

            if (_translationCache.Count > TranslationCacheLimit)
                _translationCache.Clear();

            // Providers answer in request order; pair defensively anyway so a short reply degrades
            // to an untranslated line rather than throwing away the whole pass.
            for (int i = 0; i < missing.Count && i < results.Count; i++)
                _translationCache[cacheKeyPrefix + missing[i].Text] = results[i].TranslatedText;
        }

        return blocks
            .Select(block => new TranslatedBlock(
                block.Text,
                _translationCache.GetValueOrDefault(cacheKeyPrefix + block.Text, block.Text),
                block.Bounds,
                block.SourceLineBounds,
                block.SourceGlyphHeight))
            .ToList();
    }

    /// <summary>
    /// Copies one region straight off the desktop. The overlay windows are excluded from capture
    /// (see <see cref="WindowCaptureShield"/>), so what comes back is the application underneath and
    /// never the translation this loop drew over it a moment ago.
    /// </summary>
    private Bitmap? GrabRegion(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch (Exception ex)
        {
            // A grab can fail transiently — a secure desktop (UAC prompt, lock screen) is the usual
            // cause. Skipping this poll is the whole recovery; the next one is 250ms away.
            if (_grabFailureReported)
                Log.Debug(ex, "Realtime screen grab failed for {Bounds}", bounds);
            else
            {
                _grabFailureReported = true;
                Log.Warn(ex, "Realtime screen grab failed for {Bounds}; further failures logged at Debug", bounds);
            }
            bitmap?.Dispose();
            return null;
        }
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        NotSupportedException => ex.Message,
        _ => $"翻譯暫時失敗，將持續重試：{ex.Message}"
    };

}
