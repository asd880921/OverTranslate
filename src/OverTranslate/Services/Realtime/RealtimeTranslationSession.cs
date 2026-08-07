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
/// Each region runs on its own loop, no loop ever queues for the recogniser — a busy engine means
/// this poll is skipped, not that this region waits — and no loop waits for a translation it asked
/// for (see <see cref="RegionTranslationPump"/>). All three follow from what "realtime" costs when
/// it is not true: a shared loop makes every region wait out the slowest one, a queued pass answers
/// a frame that has already been replaced while delaying the frame that replaced it, and a loop
/// waiting on the network is a loop not watching the screen. The screenshot flow makes the opposite
/// trade, because its user is waiting for that one result and it will not come round again.
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

    // How many of a region's translations may be in flight at once. More than one because a slow
    // answer must not delay the line after it, and only a few because a provider that has stopped
    // answering altogether would otherwise accumulate work for as long as the session runs.
    private const int MaxConcurrentTranslations = 3;

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

        // A watched region is idle between lines, and the recogniser cannot tell that from a tray
        // icon nobody has touched all afternoon. Told explicitly, it stops releasing the model that
        // the next line is about to need.
        _ocr.SetKeepWarm(true);

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
        // Back to releasing the model after a period of inactivity, which is the right rule again
        // the moment nothing is watching the screen.
        _ocr.SetKeepWarm(false);
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
        var pump = new RegionTranslationPump(this, region, sourceLanguage, targetLanguage, token);

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            var lastScan = Stopwatch.GetTimestamp();
            var skippedPolls = 0;

            while (await timer.WaitForNextTickAsync(token))
            {
                token.ThrowIfCancellationRequested();

                // A translation that never reached the screen leaves this region recorded as showing
                // words it does not show, and the pixels will not change again on their own — so the
                // retry has to be asked for. Applied here, where the state is only ever touched by
                // this one loop.
                if (pump.TakeRetryRequest()) state.Invalidate();

                using var frame = GrabRegion(region.Bounds);
                if (frame is null) continue;

                // Closes over this poll's frame, so the policy can summarise the text strips or the
                // whole region without ever seeing the bitmap.
                FrameFingerprint Capture(IReadOnlyList<Rectangle>? areas) =>
                    FrameFingerprint.Capture(frame, areas);

                if (!state.Observe(Capture))
                {
                    skippedPolls++;
                    continue;
                }

                try
                {
                    SetBusy(true);
                    var reading = await ReadRegionAsync(
                        region, frame, state, Capture, sourceLanguage, pump, token);

                    // One line per look at the region, because every question about this feature
                    // being late or missing a line comes down to two things the outside cannot see:
                    // how long it had been since the region was last examined, and which of the ways
                    // out of a pass was taken. Counts and lengths only — the words themselves stay at
                    // Debug, where LogBlocks keeps them.
                    Log.Info(
                        "Realtime read region={Region} skipped={Skipped} since={Since}ms ocr={Ocr}ms " +
                        "lines={Lines} chars={Chars} shown={Shown} -> {Outcome}",
                        region.Id,
                        skippedPolls,
                        (int)Stopwatch.GetElapsedTime(lastScan).TotalMilliseconds,
                        reading.OcrMs,
                        reading.Lines,
                        reading.SourceLength,
                        reading.RenderedLength,
                        reading.Outcome);

                    lastScan = Stopwatch.GetTimestamp();
                    skippedPolls = 0;

                    // Skipped for want of an inference slot. Nothing has been recorded as rendered,
                    // so the same change is still pending and the next poll tries again — which is
                    // the whole point of not queueing.
                    if (reading.Outcome != PassOutcome.NoSlot) pump.ClearFailure();
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
                    pump.Report(DescribeFailure(ex));
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

    /// <summary>Which way out of a pass was taken. Recorded because they are indistinguishable from
    /// the outside, and "the line never appeared" has a different cause in every one of them.</summary>
    private enum PassOutcome
    {
        /// <summary>The recogniser was busy; nothing was read and the next poll will try again.</summary>
        NoSlot,
        /// <summary>Read, and no text survived recognition. The overlay keeps what it has for now.</summary>
        Empty,
        /// <summary>Read as empty often enough to believe it, so the overlay was emptied.</summary>
        Cleared,
        /// <summary>Read, and the words are the ones already on screen — see TextSimilarity.</summary>
        Unchanged,
        /// <summary>Read, the words are new, and they have been handed to the pump.</summary>
        Translating,
    }

    /// <param name="SourceLength">Characters read this pass.</param>
    /// <param name="RenderedLength">Characters the overlay was already showing, so a pass judged
    /// Unchanged can be told apart from a genuinely new line that the tolerance swallowed.</param>
    private readonly record struct PassReading(
        PassOutcome Outcome, int OcrMs, int Lines, int SourceLength, int RenderedLength);

    /// <summary>
    /// Reads one frame and decides what the region now shows. Everything here is bounded work the
    /// loop can afford to wait for; the translation, which is not, is handed to the pump.
    /// </summary>
    private async Task<PassReading> ReadRegionAsync(
        RealtimeRegion region,
        Bitmap frame,
        RealtimeRegionState state,
        Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture,
        string sourceLanguage,
        RegionTranslationPump pump,
        CancellationToken token)
    {
        // Split timings, because "the overlay feels slow" has three quite different causes —
        // recognition, the translation endpoint, and how long this loop waited before starting —
        // and they are indistinguishable from the outside. The pump logs the other half.
        var started = Stopwatch.GetTimestamp();

        // Try, not wait: a queued pass would be reading a frame that has already been replaced, and
        // would hold this region's loop shut while it did. Skipping costs one poll.
        var recognized = await _ocr.TryRecognizeAsync(frame, sourceLanguage, token);
        if (recognized is null)
            return new PassReading(PassOutcome.NoSlot, 0, 0, 0, state.RenderedText.Length);

        var ocrMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        token.ThrowIfCancellationRequested();

        var sourceText = string.Join('\n', recognized.Select(block => block.Text));
        var textBounds = ToTextBounds(recognized);

        // Claimed before anything can be drawn, so that a translation coming back out of order can
        // be told it has been overtaken. Every route to the screen goes through the pump holding it.
        var pass = pump.NextPass();

        // The pixels moved but the words did not — a cursor blinked, a background scrolled, a video
        // played on behind a caption. Record the frame so it is not examined again, and go no
        // further: this is the case that keeps a session over moving content off the network.
        if (recognized.Count == 0)
        {
            // Checked before the "same text" shortcut below, because both are the empty string once
            // the region has genuinely gone quiet and only this branch counts that towards clearing.
            var shownBefore = state.RenderedText.Length;
            state.MarkRendered(textBounds, capture, sourceText);

            var cleared = state.ShouldClearOverlay;
            if (cleared) pump.Publish(pass, []);

            return new PassReading(
                cleared ? PassOutcome.Cleared : PassOutcome.Empty, ocrMs, 0, 0, shownBefore);
        }

        // Close enough to what is already on screen to be the same words read twice. The strips are
        // still updated — they may have shifted a pixel — but the anchor text deliberately is not:
        // holding it at what was actually rendered stops a line from drifting away one tolerated
        // character at a time, which comparing against the previous frame instead would allow.
        if (TextSimilarity.IsSameContent(sourceText, state.RenderedText))
        {
            var shownBefore = state.RenderedText.Length;
            state.MarkRendered(textBounds, capture, state.RenderedText);
            return new PassReading(
                PassOutcome.Unchanged, ocrMs, recognized.Count, sourceText.Length, shownBefore);
        }

        // Recorded as shown before it has been translated, and deliberately: the frame has been
        // read and the words are known, so holding this region's state open until the network
        // answers would only stop the region being watched. A translation that never arrives asks
        // for this record to be undone — see RegionTranslationPump.
        var shown = state.RenderedText.Length;
        state.MarkRendered(textBounds, capture, sourceText);
        pump.Post(pass, recognized, ocrMs);

        return new PassReading(
            PassOutcome.Translating, ocrMs, recognized.Count, sourceText.Length, shown);
    }

    private void RaiseRegionUpdated(RealtimeRegionUpdate update) => RegionUpdated?.Invoke(this, update);

    private void RaiseFailed(string message) => Failed?.Invoke(this, message);

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

    /// <summary>
    /// One region's translations, run off its poll loop.
    /// </summary>
    /// <remarks>
    /// Translation is the one stage whose duration this application does not control. Measured over
    /// 639 passes it answered in 82ms at the median, 1816ms at the 99th percentile and 3125ms at
    /// worst. Awaiting that inside the poll loop stopped the region being looked at for the whole
    /// time — <see cref="PeriodicTimer"/> drops the ticks that pass while it is not being awaited —
    /// so one slow answer blinded the region for a dozen polls, and a line that appeared and went in
    /// that window was never captured at all. That is what a missing sentence was: not a line
    /// misjudged, a line never seen.
    ///
    /// So a pass is posted here and the loop carries straight on to the next frame. Several may be
    /// in flight at once, because translation is I/O and a second one costs waiting rather than CPU,
    /// and they may finish out of order — hence the pass number every result carries, and the rule
    /// that a result is dropped once a later pass has reached the screen.
    /// </remarks>
    private sealed class RegionTranslationPump(
        RealtimeTranslationSession session,
        RealtimeRegion region,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken token)
    {
        private readonly SemaphoreSlim _slots = new(MaxConcurrentTranslations, MaxConcurrentTranslations);
        private readonly RealtimePublishOrder _order = new();
        private readonly object _gate = new();

        private string? _lastReportedFailure;
        private int _retryRequested;

        /// <summary>Claims the number identifying this pass in the order the region was read.</summary>
        public long NextPass() => _order.NextPass();

        /// <summary>Translates a pass's lines and draws them, both without holding up the caller.</summary>
        public void Post(long pass, List<OcrTextBlock> blocks, int ocrMs)
        {
            if (!_slots.Wait(0))
            {
                // Every slot is held by a translation that has not answered, so the provider is
                // stalled rather than merely slow. Nothing in flight can be recalled to make room,
                // so this read is lost — and asking for a retry is what stops the region sitting
                // there recorded as showing a translation that was never drawn.
                Log.Info(
                    "Realtime region {Region} dropped a read: {InFlight} translations already in flight",
                    region.Id, MaxConcurrentTranslations);
                RequestRetry();
                return;
            }

            // Not Task.Run(_, token): a token already cancelled skips the body entirely, and the
            // slot taken out above would never be given back.
            _ = Task.Run(async () =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    session.SetBusy(true);
                    var translated = await session.TranslateAsync(blocks, sourceLanguage, targetLanguage, token);
                    var translateMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                    if (Publish(pass, translated))
                        Log.Info(
                            "Realtime pass region={Region} ocr={Ocr}ms translate={Translate}ms lines={Lines}",
                            region.Id, ocrMs, translateMs, translated.Count);
                    else
                        // Worth its own line: it means the region changed again before this answer
                        // arrived, which is the shape of a provider too slow for the content.
                        Log.Info(
                            "Realtime pass region={Region} overtaken after translate={Translate}ms, not drawn",
                            region.Id, translateMs);
                }
                catch (OperationCanceledException)
                {
                    // The session is stopping; nothing to report and nothing to retry.
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Realtime translation failed for region {Region}", region.Id);
                    Report(DescribeFailure(ex));
                    RequestRetry();
                }
                finally
                {
                    session.SetBusy(false);
                    _slots.Release();
                }
            });
        }

        /// <summary>Draws a pass's lines unless a later pass has already been drawn.</summary>
        /// <returns>False when this pass has been overtaken.</returns>
        public bool Publish(long pass, IReadOnlyList<TranslatedBlock> lines)
        {
            if (!_order.TryClaim(pass)) return false;

            session.RaiseRegionUpdated(new RealtimeRegionUpdate(region.Id, lines));
            return true;
        }

        /// <summary>Reports a failure, but only if it is not the one already reported.</summary>
        public void Report(string message)
        {
            lock (_gate)
            {
                if (message == _lastReportedFailure) return;
                _lastReportedFailure = message;
            }

            session.RaiseFailed(message);
        }

        public void ClearFailure()
        {
            lock (_gate) _lastReportedFailure = null;
        }

        private void RequestRetry() => Interlocked.Exchange(ref _retryRequested, 1);

        /// <summary>Whether the region should forget what it thinks is on screen and read it again.</summary>
        public bool TakeRetryRequest() => Interlocked.Exchange(ref _retryRequested, 0) == 1;
    }
}
