using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using NLog;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// The continuous half of realtime translation: watches each region on its own loop and pays for
/// recognition and translation only when what it is watching has actually changed.
/// </summary>
/// <remarks>
/// This is a monitoring loop that may run for hours next to a game, so every stage exists to avoid
/// work rather than to do it:
/// <list type="bullet">
/// <item>a poll grabs a small rectangle and summarises it — an idle region costs only that;</item>
/// <item><see cref="RealtimeRegionState"/> decides when a changed frame is worth reading, so text
/// that is fading in or scrolling is recognised once rather than at every intermediate frame,
/// without ever waiting forever for a still frame that moving content will never produce;</item>
/// <item>recognised text that says the same thing as what is already on screen ends the pass without
/// a network call — see <see cref="TextSimilarity"/>, which is what keeps recognition's own jitter
/// from being mistaken for the words having changed;</item>
/// <item>a per-session cache means a line of dialogue that comes back (a repeated subtitle, a menu
/// the user reopens) is never translated twice.</item>
/// </list>
///
/// Each region runs on its own loop, and no loop ever queues for the recogniser — a busy engine
/// means this poll is skipped, not that this region waits. Both follow from what "realtime" costs
/// when it is not true: a shared loop makes every region wait out the slowest one, and a queued pass
/// answers a frame that has already been replaced while delaying the frame that replaced it. The
/// screenshot flow makes the opposite trade, because its user is waiting for that one result and it
/// will not come round again.
///
/// The OCR and translation services are handed in rather than created: they are the same instances
/// the screenshot flow uses, so the two features share one loaded ONNX runtime and one bounded pool
/// of inference slots instead of competing for the CPU with a second copy of each.
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

    // Concurrent, because every region's loop shares it: two regions showing the same line of
    // dialogue should cost one translation, not one each.
    private readonly ConcurrentDictionary<string, string> _translationCache = new();
    private CancellationTokenSource? _cts;

    // How many region loops are mid-pass. Only drives the busy indicator, so it is deliberately not
    // synchronised beyond being interlocked.
    private int _busyRegions;

    // A grab that keeps failing produces no work and no error, which is indistinguishable from a
    // region that simply has nothing in it. Reported once per session so a locked screen does not
    // fill the log at four lines a second.
    private int _grabFailureReported;

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
        Interlocked.Exchange(ref _grabFailureReported, 0);
        Interlocked.Exchange(ref _busyRegions, 0);

        Log.Info(
            "Realtime session started: {Count} region(s), {Src}->{Tgt}",
            regions.Count, sourceLanguage, targetLanguage);

        // One loop per region rather than one loop over the regions. Sharing a loop made every
        // region wait out the slowest one: with three regions and a half-second recognition apiece,
        // the third could not update more than about twice a second no matter how little had
        // changed in it. All of this is background work anyway — the grab is a BitBlt, recognition
        // is CPU-bound, translation is I/O — so none of it belongs on the dispatcher, which has an
        // interface to keep responsive for as long as this runs.
        foreach (var region in regions)
        {
            var watched = region;
            _ = Task.Run(() => RunRegionAsync(watched, sourceLanguage, targetLanguage, cts.Token), cts.Token);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        // The cache is kept across a stop/edit/start cycle on purpose: the user usually comes back
        // to the same content, and re-translating lines we already have would be a visible pause
        // for nothing.
    }

    private async Task RunRegionAsync(
        RealtimeRegion region,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken token)
    {
        var state = new RealtimeRegionState();
        string? lastReportedFailure = null;

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                token.ThrowIfCancellationRequested();

                using var frame = GrabRegion(region.Bounds);
                if (frame is null) continue;

                // Closes over this poll's frame, so the policy can summarise the text strips or the
                // whole region without ever seeing the bitmap.
                FrameFingerprint Capture(IReadOnlyList<Rectangle>? areas) =>
                    FrameFingerprint.Capture(frame, areas);

                if (!state.Observe(Capture)) continue;

                try
                {
                    SetBusy(true);
                    var ran = await ProcessRegionAsync(
                        region, frame, state, Capture, sourceLanguage, targetLanguage, token);

                    // Skipped for want of an inference slot. Nothing has been recorded as rendered,
                    // so the same change is still pending and the next poll tries again — which is
                    // the whole point of not queueing.
                    if (ran) lastReportedFailure = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One failed pass must not end the region — the engine may be briefly
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
                    SetBusy(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Realtime region {Region} stopped", region.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Realtime region {Region} ended unexpectedly", region.Id);
            Failed?.Invoke(this, $"即時翻譯已中止：{ex.Message}");
        }
    }

    // The indicator is on while any region is mid-pass, so it tracks a count rather than a flag.
    private void SetBusy(bool busy)
    {
        var count = busy
            ? Interlocked.Increment(ref _busyRegions)
            : Interlocked.Decrement(ref _busyRegions);

        BusyChanged?.Invoke(this, count > 0);
    }

    /// <returns>False when the pass was skipped because the recogniser had no free slot.</returns>
    private async Task<bool> ProcessRegionAsync(
        RealtimeRegion region,
        Bitmap frame,
        RealtimeRegionState state,
        Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken token)
    {
        // Split timings, because "the overlay feels slow" has three quite different causes —
        // recognition, the translation endpoint, and how long this loop waited before starting —
        // and they are indistinguishable from the outside.
        var started = Stopwatch.GetTimestamp();

        // Try, not wait: a queued pass would be reading a frame that has already been replaced, and
        // would hold this region's loop shut while it did. Skipping costs one poll.
        var recognized = await _ocr.TryRecognizeAsync(frame, sourceLanguage, token);
        if (recognized is null) return false;

        var afterOcr = Stopwatch.GetTimestamp();
        token.ThrowIfCancellationRequested();

        var sourceText = string.Join('\n', recognized.Select(block => block.Text));
        var textBounds = ToTextBounds(recognized);

        // The pixels moved but the words did not — a cursor blinked, a background scrolled, a video
        // played on behind a caption. Record the frame so it is not examined again, and go no
        // further: this is the case that keeps a session over moving content off the network.
        if (recognized.Count == 0)
        {
            // Checked before the "same text" shortcut below, because both are the empty string once
            // the region has genuinely gone quiet and only this branch counts that towards clearing.
            state.MarkRendered(textBounds, capture, sourceText);
            if (state.ShouldClearOverlay)
                RegionUpdated?.Invoke(this, new RealtimeRegionUpdate(region.Id, []));
            return true;
        }

        // Close enough to what is already on screen to be the same words read twice. The strips are
        // still updated — they may have shifted a pixel — but the anchor text deliberately is not:
        // holding it at what was actually rendered stops a line from drifting away one tolerated
        // character at a time, which comparing against the previous frame instead would allow.
        if (TextSimilarity.IsSameContent(sourceText, state.RenderedText))
        {
            state.MarkRendered(textBounds, capture, state.RenderedText);
            return true;
        }

        var translated = await TranslateAsync(recognized, sourceLanguage, targetLanguage, token);
        var afterTranslate = Stopwatch.GetTimestamp();
        token.ThrowIfCancellationRequested();

        state.MarkRendered(textBounds, capture, sourceText);
        RegionUpdated?.Invoke(this, new RealtimeRegionUpdate(region.Id, translated));

        Log.Info(
            "Realtime pass region={Region} ocr={Ocr}ms translate={Translate}ms lines={Lines}",
            region.Id,
            (int)Stopwatch.GetElapsedTime(started, afterOcr).TotalMilliseconds,
            (int)Stopwatch.GetElapsedTime(afterOcr, afterTranslate).TotalMilliseconds,
            translated.Count);

        return true;
    }

    /// <summary>
    /// The recognised lines as pixel rectangles in region coordinates, which is what the change
    /// detector watches from here on. Rounded outwards so a box does not lose the row of pixels its
    /// glyphs actually end on.
    /// </summary>
    private static List<Rectangle> ToTextBounds(IReadOnlyList<OcrTextBlock> blocks) =>
    [
        .. blocks.Select(block => Rectangle.FromLTRB(
            (int)Math.Floor(block.Bounds.Left),
            (int)Math.Floor(block.Bounds.Top),
            (int)Math.Ceiling(block.Bounds.Right),
            (int)Math.Ceiling(block.Bounds.Bottom)))
    ];

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
            if (Interlocked.Exchange(ref _grabFailureReported, 1) == 0)
                Log.Warn(ex, "Realtime screen grab failed for {Bounds}; further failures logged at Debug", bounds);
            else
                Log.Debug(ex, "Realtime screen grab failed for {Bounds}", bounds);
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
