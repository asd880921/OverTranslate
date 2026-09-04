using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NLog;
using RapidOcrNet;
using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// KNOWN LIMITATION: the detector sometimes returns a box that starts part way along a line, and the
/// characters before it are lost with nothing in the log to say they existed. Closed as unfixable in
/// #69; do not spend another round looking for the setting that causes it, because it is not one.
/// </summary>
/// <remarks>
/// Ruled out, each with a measurement rather than an argument: the recognition confidence floor (the
/// leading box is never returned, so nothing filters it), all three detector box thresholds
/// (<see cref="DetectorThresholdOverride"/> — dropping BoxScoreThresh to 0.10, which discards
/// nothing, changes not one character; #71 later found all three were on the wrong values anyway and
/// corrected them, see <see cref="ExportedThresholds"/>, and this symptom survived that too), the
/// border (<see cref="DetectorPaddingOverride"/>, 0 to 96),
/// the normalisation statistics (<see cref="ShippedDetector"/>), the detector size
/// (<c>--scale-sweep</c>), and how tightly the user framed the capture (<c>--margin-series</c> over
/// 27 whole screens: 1.1% apart, and the tightest framing is the worst of them).
///
/// What it actually is: the response is knife-edge, and only along the short axis. On the reported
/// frame, cropping one pixel off the top takes the reading from 5 characters to 6, and four pixels
/// takes it to 10 — while cropping up to eight pixels off the side changes nothing at all. Appending
/// blank rows below the picture, which touches no content whatsoever, walks the reading between 5 and
/// 10 characters with no pattern: +4px reads 10, +20px reads 6, +24px reads 9. The capture the user
/// happened to draw lands where it lands.
///
/// So a frame either reads or does not, and the deciding factor is a few pixels of height that
/// nobody chose. Replacing the detector is the only lever left and #33 already measured that
/// trade — PP-OCRv6_det_medium costs 90ms per recognition and 62MB to save roughly one line every
/// two and a half minutes — and rejected it.
/// </remarks>
internal sealed class OnnxOcrEngine : IOcrEngine
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly string ModelRoot =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocrmodels", "onnx");
    // Threads inside one inference and how many inferences may run at once, decided together — see
    // OcrThreadBudget for the table and why the product rather than either number is the thing held
    // fixed.
    private static readonly int ThreadCount = OcrThreadBudget.For(Environment.ProcessorCount).Threads;

    // Blocks whose average per-character recognition confidence falls below this are
    // discarded as noise / icon misdetections. Mirrors the old Tesseract MinWordConfidence
    // (60/100) that the previous English engine relied on to drop non-text regions.
    private const double MinRecognitionConfidence = 0.6;

    // Automatic mode classifies each block independently, but the overlay should not render two
    // font scales in one frame or jump between them when a borderline OCR reading changes. A
    // shared midpoint keeps the effective glyph height independent from the chosen layout path.
    private const double AutomaticGlyphHeightFromPitch = 1.24;

    // A runtime holds det + cls + rec ONNX sessions plus their CPU memory arenas (hundreds of
    // MB with the larger ImgResize). This is a tray-resident, occasional-use tool, so we keep
    // only the active model loaded AND release it after a period of inactivity, returning that
    // memory to baseline while idle. The next capture transparently reloads the needed model.
    private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromMinutes(1);

    // Long enough that no real inference could still be running, short enough that a caller stuck
    // behind a wedged one gets an error instead of never returning.
    private static readonly TimeSpan ModelSwapDrainTimeout = TimeSpan.FromSeconds(10);

    // Measured on a 16-core machine against a 1200x200 screen grab, back when each pass took two
    // threads: one pass 320ms; four concurrent passes 502ms in total, so 2.5x the throughput for
    // 1.6x the latency, and five was slower than four. That is where the cap of 4 comes from.
    //
    // Realtime has never reached this limit: across a full day of sessions the gate turned nobody
    // away once, because a session runs one or two blocks and each block is one loop. The batch
    // image translation feature is the caller this number was really chosen for, and the one to
    // re-measure for if it lands.
    private static readonly int InferenceSlots = OcrThreadBudget.For(Environment.ProcessorCount).Slots;

    /// <inheritdoc cref="InferenceSlots"/>
    internal static int ConcurrentRecognitions => InferenceSlots;

    private readonly object _sync = new();
    // Admits a bounded number of concurrent inferences; see RecognizeAsync for why it is bounded.
    private readonly SemaphoreSlim _inferenceGate = new(InferenceSlots, InferenceSlots);
    private readonly System.Threading.Timer _idleReleaseTimer;
    private RapidOcrRuntime? _current;
    private string? _currentModelKey;
    // Number of Detect calls currently running against _current. Inference runs OUTSIDE _sync
    // (it is slow and must not block other work or the timer), so the idle timer must never
    // dispose a runtime while this is > 0 — that would free the native ONNX sessions mid-
    // inference and crash. A Timer.Change() does NOT cancel an already-queued callback, so this
    // in-use check is what makes a stale idle callback that fires during a new capture benign.
    // Guarded by _sync.
    private int _inUse;
    private bool _disposed;
    // Suspends the idle release. Guarded by _sync.
    private bool _keepWarm;

    public OnnxOcrEngine() =>
        _idleReleaseTimer = new System.Threading.Timer(_ => ReleaseIdleRuntime());

    /// <summary>
    /// Holds the loaded model in memory regardless of how long it sits unused.
    /// </summary>
    /// <remarks>
    /// For a realtime session, where the inactivity release is measuring the wrong thing. A watched
    /// region is idle between lines of dialogue, and a gap over <see cref="IdleReleaseDelay"/> is
    /// ordinary — a quiet scene, a paused video, a menu nobody is touching. Releasing the model
    /// there means the next line pays to load it again: measured at 575–1027ms against a steady
    /// state of 234ms, all of it inside the poll loop, where it is time the region is not being
    /// watched. Idle in a session is not idle; it is waiting.
    /// </remarks>
    public void SetKeepWarm(bool keepWarm)
    {
        lock (_sync)
        {
            _keepWarm = keepWarm;

            // Nothing re-arms the countdown on its own once the last pass has finished, so leaving
            // the mode has to start it — otherwise the model would sit loaded until the next use.
            if (!keepWarm && !_disposed && _inUse == 0)
                _idleReleaseTimer.Change(IdleReleaseDelay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Hands the loaded model's memory back now, instead of waiting out
    /// <see cref="IdleReleaseDelay"/>.
    /// </summary>
    /// <remarks>
    /// For a realtime session being paused: the user has said they are done watching for a while,
    /// and a runtime that holds hundreds of MB has no business sitting in memory for another minute
    /// on the strength of a timer. Reloading it costs the same as the first load did, which is what
    /// makes 繼續 affordable.
    ///
    /// Leaves the model alone while a Detect is running against it — freeing the native sessions
    /// mid-inference would crash the process. Nothing is lost by returning: the pass that is holding
    /// it re-arms the inactivity countdown as it finishes, so the memory still comes back on its own.
    /// </remarks>
    public void ReleaseNow()
    {
        lock (_sync)
        {
            // Otherwise the release below would be undone by the very next recognition — and a
            // caller asking for the model to go is a caller that has stopped watching the screen.
            _keepWarm = false;

            if (_disposed || _inUse > 0) return;

            DisposeCurrentRuntime();
        }
    }

    public Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        return Task.Run(async () =>
        {
            // Bounded, not unlimited. Each inference already uses ThreadCount threads, so past the
            // slot count concurrent passes only split the same cores between themselves: overlapping
            // captures (frame it wrong, Esc, frame again) used to pile up that way and starve the
            // one the user was actually waiting for.
            await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Checked after the gate, not before: by the time an abandoned request reaches the
                // front of the queue its session is usually long gone, and bailing out here costs
                // nothing at all. Inference itself cannot be interrupted, so this is the last point
                // where giving up is still free.
                cancellationToken.ThrowIfCancellationRequested();

                return RecognizeCore(bitmap, sourceLanguage);
            }
            finally
            {
                _inferenceGate.Release();
            }
        }, cancellationToken);
    }

    public Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        return Task.Run<List<OcrTextBlock>?>(() =>
        {
            // Inside the lambda, not before it: Task.Run with an already-cancelled token never runs
            // the body, so a slot taken out here would never be given back.
            if (!_inferenceGate.Wait(0, cancellationToken))
                return null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RecognizeCore(bitmap, sourceLanguage, maxDetectSize);
            }
            finally
            {
                _inferenceGate.Release();
            }
        }, cancellationToken);
    }

    private List<OcrTextBlock> RecognizeCore(
        Bitmap bitmap, string sourceLanguage, int? maxDetectSize = null)
    {
        var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
        var isCjk = OcrLanguageRouter.UsesCjkOnnx(normalizedLanguage);
        var usesAutomaticLayout = OcrLanguageRouter.UsesAutomaticLayout(normalizedLanguage);

        // Select the runtime and register an in-use reference atomically under _sync, so the
        // idle timer cannot dispose it between selection and Detect. The matching release
        // (which also re-arms the idle countdown) runs in the finally, under _sync.
        var runtime = AcquireRuntime(normalizedLanguage);
        try
        {
            Log.Info(
                "Running ONNX OCR on {W}x{H} bitmap, detect={Detect}, lang={Lang}, model={Model}, threads={Threads}",
                bitmap.Width,
                bitmap.Height,
                maxDetectSize?.ToString() ?? "default",
                normalizedLanguage,
                runtime.ModelName,
                ThreadCount);

            var options = CreateOptions(maxDetectSize);
            using var skBitmap = ConvertToSkBitmap(bitmap);
            using var detectorInput = AlignForDetector(skBitmap, options.Padding);
            var result = runtime.Engine.Detect(detectorInput, options);
            var blocks = ApplyBlockFilters(result.TextBlocks, normalizedLanguage, isCjk, usesAutomaticLayout);

            // Counts and lengths only — enough to tell "found nothing" from "found the wrong thing"
            // without the recognised text itself, which LogBlocks keeps at Debug.
            Log.Info(
                "ONNX OCR lang={Lang} rawBlocks={RawBlocks} blocks={Blocks} strLen={StrLen}",
                normalizedLanguage,
                result.TextBlocks.Length,
                blocks.Count,
                result.StrRes?.Length ?? 0);

            // Before the filters rather than after, and only when they took everything: a region
            // that reads as empty has nothing left to log, which is exactly the case anyone is
            // trying to diagnose. Where the rejected boxes sit and what they scored is what tells a
            // line framed outside the block (a box against an edge, a couple of clipped glyphs)
            // from one the confidence floor threw away (a box over the text, plausible words, a
            // score just under the bar).
            if (blocks.Count == 0 && result.TextBlocks.Length > 0)
                LogRejectedBlocks(normalizedLanguage, result.TextBlocks);

            LogBlocks(normalizedLanguage, blocks);
            return blocks;
        }
        finally
        {
            ReleaseRuntime();
        }
    }

    internal static string GetModelKeyForLanguage(string language) =>
        OcrLanguageRouter.Normalize(language) switch
        {
            "KO" => "korean",
            // Everything else uses the general ("cjk") model — PP-OCRv6_small_rec, one model
            // covering 50 languages: Simplified and Traditional Chinese, English, Japanese, and 46
            // Latin-script ones. English UI captures very often contain embedded Chinese (chrome,
            // labels, ratings), which a Latin-only model dropped or garbled; this reads Latin AND
            // those CJK glyphs in one pass. The text is still Latin, so source-language routing
            // (UsesCjkOnnx) keeps EN on the Latin layout path. Lone-ideograph icon misreads that
            // come with reading both scripts at once are no longer stripped — see
            // <see cref="StripLoneIdeographs"/>.
            //
            // Korean stays on its own model above because v6 carries no Hangul at all — measured
            // on its dictionary, 0 of 18,708 characters — so the one model cannot cover KO.
            _ => "cjk",
        };

    /// <summary>
    /// The detector's own output — every box it found and the score it gave — with none of the
    /// recognition, normalisation or filtering that <see cref="RecognizeAsync"/> runs afterwards.
    /// </summary>
    /// <remarks>
    /// For OcrHarness, and specifically for telling a box the detector never found from a box it
    /// found and the recogniser then read differently. Those two are indistinguishable in the
    /// finished blocks and have completely different causes, and no other entry point separates
    /// them.
    ///
    /// Everything ahead of the detector is shared with the real path on purpose — the same options,
    /// the same alignment, the same runtime — because a measurement that prepared its own input
    /// would be measuring the preparation.
    /// </remarks>
    /// <param name="maxDetectSize">As <see cref="TryRecognizeAsync"/>; null is the screenshot flow.</param>
    internal IReadOnlyList<(System.Windows.Rect Bounds, float Score)> DetectBoxesOnly(
        Bitmap bitmap, string sourceLanguage, int? maxDetectSize = null)
    {
        var runtime = AcquireRuntime(OcrLanguageRouter.Normalize(sourceLanguage));
        try
        {
            var options = CreateOptions(maxDetectSize);
            using var skBitmap = ConvertToSkBitmap(bitmap);
            using var detectorInput = AlignForDetector(skBitmap, options.Padding);

            return runtime.Engine.DetectBoxes(detectorInput, options)
                .Select(box =>
                {
                    var xs = box.BoxPoints.Select(point => point.X).ToList();
                    var ys = box.BoxPoints.Select(point => point.Y).ToList();
                    return (
                        new System.Windows.Rect(
                            xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min()),
                        box.Score);
                })
                .ToList();
        }
        finally
        {
            ReleaseRuntime();
        }
    }

    /// <summary>
    /// Detection held open, so recognition can be asked for a chosen subset of the boxes instead of
    /// all of them. Everything after recognition is the shipped path.
    /// </summary>
    /// <remarks>
    /// For OcrHarness, and specifically for the candidate that detects once over the whole source
    /// image and then reads only the boxes that fall inside what the user framed: the boxes then
    /// come from a bitmap larger than the answer is about, which no other entry point allows.
    ///
    /// Assembled out of the library's own pieces rather than reimplemented, several of them reached
    /// by reflection. That is the point of the type. Recognition does NOT crop from the bitmap that
    /// was handed in: the library prepares a detector input first — outer padding, an optional
    /// letterbox, and the resize <see cref="RapidOcrOptions.ImgResize"/> caps — detects on that, and
    /// crops the part images from THAT bitmap, mapping the boxes back to the original only on the
    /// way out. Cropping from the original instead reads a different picture, and on a page the
    /// resize actually shrinks it would read a picture at the wrong size, which is exactly the cost
    /// this measurement exists to find.
    ///
    /// Verified rather than argued — see the harness's <c>--roi-fullframe</c>, which begins by
    /// recognising every box through here and checking the text against <see cref="RecognizeAsync"/>.
    /// </remarks>
    internal DetectionSession BeginDetection(
        Bitmap bitmap, string sourceLanguage, int? maxDetectSize = null)
    {
        var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
        var runtime = AcquireRuntime(normalizedLanguage);
        try
        {
            return new DetectionSession(this, runtime, bitmap, normalizedLanguage, maxDetectSize);
        }
        catch
        {
            ReleaseRuntime();
            throw;
        }
    }

    /// <summary>
    /// One detection, kept alive so its boxes can be recognised a subset at a time.
    /// </summary>
    internal sealed class DetectionSession : IDisposable
    {
        private readonly OnnxOcrEngine _owner;
        private readonly RapidOcr _engine;
        private readonly string _language;
        private readonly SKBitmap _skBitmap;
        private readonly SKBitmap _aligned;
        // Boxed, because the type is internal to the library and cannot be named here.
        private readonly object _detectorInput;
        private readonly SKBitmap _detectorBitmap;
        private readonly RapidOcrOptions _options;
        private readonly IReadOnlyList<RapidOcrNet.TextBox> _detectorSpaceBoxes;

        internal DetectionSession(
            OnnxOcrEngine owner,
            RapidOcrRuntime runtime,
            Bitmap bitmap,
            string normalizedLanguage,
            int? maxDetectSize)
        {
            _owner = owner;
            _engine = runtime.Engine;
            _language = normalizedLanguage;
            _options = CreateOptions(maxDetectSize);
            _skBitmap = ConvertToSkBitmap(bitmap);
            _aligned = AlignForDetector(_skBitmap, _options.Padding);

            _detectorInput = PrepareDetectorInputMethod.Invoke(null, new object[] { _aligned, _options })!;
            _detectorBitmap = (SKBitmap)DetectorInputBitmapField.GetValue(_detectorInput)!;
            var scale = (ScaleParam)DetectorInputScaleField.GetValue(_detectorInput)!;

            var detector = (TextDetector)TextDetectorField.GetValue(_engine)!;
            _detectorSpaceBoxes = detector.GetTextBoxes(
                _detectorBitmap, scale, _options.BoxScoreThresh, _options.BoxThresh, _options.UnClipRatio);

            // The caller works in the coordinates of the bitmap it handed in, so every box is
            // reported there. The detector-space originals are what recognition crops with and are
            // kept beside them, because mapping is not reversible once the resize is not 1.0.
            Boxes = _detectorSpaceBoxes
                .Select(box =>
                {
                    var points = (SKPointI[])box.BoxPoints.Clone();
                    MapToOriginalMethod.Invoke(_detectorInput, new object[] { points });
                    var xs = points.Select(point => point.X).ToList();
                    var ys = points.Select(point => point.Y).ToList();
                    return (
                        Bounds: new System.Windows.Rect(
                            xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min()),
                        box.Score);
                })
                .ToList();
        }

        /// <summary>Every box the detector found, in the handed-in bitmap's own coordinates.</summary>
        internal IReadOnlyList<(System.Windows.Rect Bounds, float Score)> Boxes { get; }

        /// <summary>
        /// Recognises the boxes at the given indices into <see cref="Boxes"/> and nothing else.
        /// </summary>
        internal IReadOnlyList<OcrTextBlock> Recognize(IReadOnlyList<int> boxIndices)
        {
            if (boxIndices.Count == 0) return Array.Empty<OcrTextBlock>();

            var chosen = boxIndices.Select(index => _detectorSpaceBoxes[index]).ToList();
            var partImages = (SKBitmap[])GetPartImagesMethod.Invoke(
                null, new object[] { _detectorBitmap, chosen })!;
            try
            {
                // No classifier pass: DoAngle is false on every options set the app builds, and the
                // library's own 180° rotation is gated on it.
                var recognizer = (TextRecognizer)TextRecognizerField.GetValue(_engine)!;
                var lines = recognizer.GetTextLines(partImages);

                var textBlocks = new List<TextBlock>(chosen.Count);
                for (var i = 0; i < chosen.Count; i++)
                {
                    // The library's own floor, applied here for the same reason it applies it: what
                    // reaches ConvertBlocks on the shipped path has already been through it.
                    var scores = lines[i].CharScores;
                    if (scores is not { Length: > 0 } || scores.Average() < _options.TextScore)
                        continue;

                    var points = (SKPointI[])chosen[i].BoxPoints.Clone();
                    MapToOriginalMethod.Invoke(_detectorInput, new object[] { points });

                    textBlocks.Add(new TextBlock
                    {
                        BoxPoints = points,
                        BoxScore = chosen[i].Score,
                        // Required by the type and ignored downstream: ConvertBlocks builds the text
                        // from Chars, the same as it does for the shipped path.
                        Text = string.Concat(lines[i].Chars ?? Array.Empty<string>()),
                        Chars = lines[i].Chars,
                        CharScores = scores,
                    });
                }

                return ApplyBlockFilters(
                    textBlocks.ToArray(),
                    _language,
                    OcrLanguageRouter.UsesCjkOnnx(_language),
                    OcrLanguageRouter.UsesAutomaticLayout(_language));
            }
            finally
            {
                foreach (var part in partImages) part.Dispose();
            }
        }

        public void Dispose()
        {
            ((IDisposable)_detectorInput).Dispose();
            _aligned.Dispose();
            _skBitmap.Dispose();
            _owner.ReleaseRuntime();
        }
    }

    private static readonly MethodInfo PrepareDetectorInputMethod =
        typeof(RapidOcr).GetMethod("PrepareDetectorInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetPartImagesMethod =
        typeof(RapidOcr).Assembly.GetType("RapidOcrNet.OcrUtils")!
            .GetMethod("GetPartImages", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo TextRecognizerField =
        typeof(RapidOcr).GetField("_textRecognizer", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo TextDetectorField =
        typeof(RapidOcr).GetField("_textDetector", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly Type DetectorInputType =
        typeof(RapidOcr).Assembly.GetType("RapidOcrNet.RapidOcr+DetectorInput")!;

    private static readonly FieldInfo DetectorInputBitmapField =
        DetectorInputType.GetField("Bitmap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo DetectorInputScaleField =
        DetectorInputType.GetField("Scale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo MapToOriginalMethod =
        DetectorInputType.GetMethod("MapToOriginal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Selects (loading if necessary) the runtime for the language and registers an in-use
    // reference under _sync. Every successful call MUST be paired with a ReleaseRuntime().
    private RapidOcrRuntime AcquireRuntime(string language)
    {
        var modelKey = GetModelKeyForLanguage(language);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_current is not null && _currentModelKey == modelKey)
            {
                _inUse++;
                return _current;
            }

            // A different model is requested, and swapping is only safe once no Detect is running
            // against the current runtime. With concurrent inference this is genuinely reachable —
            // a realtime session watching English while a screenshot is translated from Korean — so
            // wait the others out rather than failing the pass. The timeout is a backstop: it means
            // an inference has been running far longer than any real one does, and hanging here
            // would take the caller's whole session with it.
            while (_inUse > 0)
            {
                if (!Monitor.Wait(_sync, ModelSwapDrainTimeout))
                    throw new InvalidOperationException(LocalizationService.Get("S.Error.OcrSwapTimeout"));

                ObjectDisposedException.ThrowIf(_disposed, this);

                // Whoever we were waiting for may have loaded the model we wanted in the meantime.
                if (_current is not null && _currentModelKey == modelKey)
                {
                    _inUse++;
                    return _current;
                }
            }

            // Release the previous model's sessions/arenas before loading the next one.
            // Clear the fields first so a failed load doesn't leave a disposed runtime cached.
            _current?.Dispose();
            _current = null;
            _currentModelKey = null;

            var runtime = CreateRuntime(modelKey);
            _current = runtime;
            _currentModelKey = modelKey;
            _inUse++;
            return runtime;
        }
    }

    // Releases the in-use reference taken by AcquireRuntime. Once no Detect is in flight, the
    // inactivity countdown is (re)armed so the delay is measured from the end of the last use.
    private void ReleaseRuntime()
    {
        lock (_sync)
        {
            if (_inUse > 0)
                _inUse--;

            // Wakes any pass waiting to swap models — see AcquireRuntime. Pulsed even when disposed,
            // so a waiter is never left holding the door for a runtime that is going away.
            if (_inUse == 0)
                Monitor.PulseAll(_sync);

            if (_disposed || _keepWarm)
                return;

            if (_inUse == 0)
                _idleReleaseTimer.Change(IdleReleaseDelay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void ReleaseIdleRuntime()
    {
        lock (_sync)
        {
            // Skip disposal if we are gone, a session is holding the model open, or a Detect is
            // still running against the runtime. A timer callback that
            // was queued before a later Change() still fires; these checks are what make a stale
            // callback benign — including one queued before SetKeepWarm was turned on.
            if (_disposed || _keepWarm || _inUse > 0)
                return;

            DisposeCurrentRuntime();
        }
    }

    // Callers hold _sync and have already established that nothing is running against the runtime.
    private void DisposeCurrentRuntime()
    {
        if (_current is null) return;

        try
        {
            _current.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "釋放閒置 OCR 模型時發生例外。");
        }
        finally
        {
            _current = null;
            _currentModelKey = null;
        }
    }

    /// <summary>
    /// A text detection model and the pixel normalisation it was exported with.
    /// </summary>
    /// <param name="Path">Full path to the detector's .onnx file.</param>
    /// <param name="Mean">Per-channel mean subtracted from each pixel, in BGR order, 0–255 scale.</param>
    /// <param name="Std">Per-channel standard deviation each pixel is divided by, same order and scale.</param>
    /// <remarks>
    /// The numbers are not a tuning knob: feeding a model the statistics it was not trained with
    /// shifts every pixel it sees, so a detector measured under the wrong pair is not that detector.
    /// PP-OCRv5 was exported with the ImageNet statistics; RapidOcrNet's own PP-OCRv6 presets use
    /// 127.5/127.5 instead, which is why swapping the file alone is not enough to measure a v6
    /// detector — see <see cref="ImageNetNormalization"/> and <see cref="HalfNormalization"/>.
    /// </remarks>
    internal sealed record DetectorModel(string Path, float[] Mean, float[] Std);

    /// <summary>What PP-OCRv5 detectors were exported with.</summary>
    internal static float[] ImageNetNormalization => [123.675f, 116.28f, 103.53f];

    /// <inheritdoc cref="ImageNetNormalization"/>
    internal static float[] ImageNetNormalizationStd => [58.395f, 57.12f, 57.375f];

    /// <summary>
    /// What PP-OCRv6 detectors — including the shipped one — are read with, for both mean and
    /// deviation.
    /// </summary>
    /// <remarks>
    /// This contradicts PaddlePaddle's own <c>inference.yml</c> for the v6 detectors, which lists
    /// the ImageNet statistics, and the contradiction is not academic: swept over the same 15
    /// frames at the same sizes, PP-OCRv6_small read 8 of them at the application's primary size
    /// under 127.5 and 1 of them under ImageNet. RapidOcrNet's own v6 presets use 127.5, and the
    /// measurement agrees with the library rather than the export config.
    /// </remarks>
    internal static float[] HalfNormalization => [127.5f, 127.5f, 127.5f];

    /// <summary>
    /// The shipped detector and the statistics to read it with.
    /// </summary>
    /// <remarks>
    /// Re-measured on PP-OCRv6_det_tiny after the detector was swapped under the choice recorded on
    /// <see cref="HalfNormalization"/>, which had been made on PP-OCRv6_small. ImageNet looked like
    /// the better pair on the six fixtures in this repository and read one reported failure at twice
    /// the characters — and then lost on 137 real dumped frames: it read four of them as completely
    /// empty that 127.5 read fine ("It's bright.", "Let's pay CiRCLE a visit on the way home.",
    /// "ここは…？" twice), against one frame the other way whose reading was a misread anyway. The
    /// leading-character loss that started the investigation happens under both, at about the same
    /// rate. So 127.5 stays, and the fixtures in this repository are now known to be too small a
    /// sample to move this on. See issue #69.
    /// </remarks>
    private static DetectorModel ShippedDetector(string detPath) =>
        new(detPath, HalfNormalization, HalfNormalization);

    /// <summary>
    /// Detector to load in place of the shipped one. Null — the shipped detector — everywhere but
    /// <c>OcrHarness</c>.
    /// </summary>
    /// <remarks>
    /// A measurement seam, not a setting. Issue #22 needs the same frames read by different
    /// detectors to say whether the dead band in detector sizes is a property of the model, and
    /// the answer only means anything if everything around the detector — recogniser, dictionary,
    /// options, grouping — is the code the application really runs. Nothing in the application
    /// assigns this, so the shipped path is the untouched two-argument load below.
    ///
    /// Set it before the first recognition of an <see cref="OnnxOcrEngine"/>: a runtime already
    /// loaded is reused until it goes idle, so changing this mid-life leaves the previous detector
    /// in place. One engine per detector under measurement is the way to be sure.
    /// </remarks>
    internal static DetectorModel? DetectorOverride { get; set; }

    /// <summary>
    /// Loads the detector, classifier, recogniser and dictionary for one recognition model.
    /// </summary>
    /// <remarks>
    /// The detector is PP-OCRv6_det_tiny. The one before it, PP-OCRv5_mobile_det, did not respond
    /// to scale smoothly: swept over 15 frames a watched region had failed to read, it read 8 of
    /// them at 0.40 of native, 4 at 0.50, 6 at 0.55 and 8 again at 0.60 — a dead band with the
    /// subtitle primary size sitting in it, which is why a subtitle session spent 13% of its passes
    /// paying for fallback sizes. The same sweep with v6_det_tiny reads 9 of 15 across the whole
    /// band, and 84 of the 84 control frames the old detector already read (2 of them only from
    /// 0.70 up, where the existing fallback catches them). It is also cheaper: 89ms against 104ms
    /// at the primary size, and 1.8MB against 4.8MB on disk. See issue #22.
    ///
    /// Only the detector changed. The recogniser stayed on PP-OCRv6_small from #23, deliberately:
    /// the two models answer different questions — whether text was found at all, and whether it
    /// was read correctly — and moving both at once makes neither answer attributable.
    /// </remarks>
    private static RapidOcrRuntime CreateRuntime(string modelName)
    {
        var sharedPath = Path.Combine(ModelRoot, "shared");
        var modelPath = Path.Combine(ModelRoot, modelName);

        var detPath = Path.Combine(sharedPath, "det.onnx");
        var clsPath = Path.Combine(sharedPath, "cls.onnx");
        var recPath = Path.Combine(modelPath, "rec.onnx");
        var dictPath = Path.Combine(modelPath, "dict.txt");

        EnsureModelFile(detPath);
        EnsureModelFile(clsPath);
        EnsureModelFile(recPath);
        EnsureModelFile(dictPath);

        var detector = DetectorOverride ?? ShippedDetector(detPath);
        if (DetectorOverride is not null)
        {
            EnsureModelFile(detector.Path);
            Log.Info(
                "ONNX OCR detector overridden: {Path} mean=[{Mean}]",
                detector.Path,
                string.Join(",", detector.Mean));
        }

        var engine = new RapidOcr();

        // The model-set overload rather than the four-path one, because it is the only one that
        // carries the detector's normalisation — and the shipped detector is no longer from the
        // same family as the library's default. Verified equivalent before the swap: loaded this
        // way with the old detector and the ImageNet statistics, a sweep of two frames across all
        // fifteen sizes reproduced the four-path result line for line.
        engine.InitModels(
            new RapidOcrModelSet
            {
                DetModelPath = detector.Path,
                ClsModelPath = clsPath,
                RecModelPath = recPath,
                KeysPath = dictPath,
                DetMean = detector.Mean,
                DetStd = detector.Std,
            },
            ThreadCount);

        return new RapidOcrRuntime(modelName, engine);
    }

    // Default ImgResize (1024) downscales wide UI screenshots and destroys small-text detail,
    // causing recognition errors. Raising it keeps typical captures near native resolution;
    // smaller images are unaffected, because ImgResize only ever downscales.
    private const int ScreenshotDetectSize = 2048;

    /// <param name="maxDetectSize">
    /// Longest side to give the detector, or null for the screenshot default. A caller that knows
    /// its text is far larger than interface text passes a smaller number — see
    /// <see cref="Realtime.RealtimeDetectorSize"/> for the measurements behind that.
    /// </param>
    /// <remarks>
    /// <c>DoAngle</c> is off, against the library's default. It runs the classifier model over every
    /// detected box to decide whether the text is upside down, and nothing this application reads
    /// ever is: screen text, game interfaces and subtitles are all drawn the right way up by the
    /// application underneath. Left on it can only cost — a misfired classification flips a box and
    /// turns a readable line into nonsense — so this is not a speed-for-accuracy trade in either
    /// direction.
    ///
    /// Measured on a 1380x750 grab of a game screen with 7 text boxes: 440ms with it, 427ms without.
    /// 3%, which is worth saying out loud — the classifier is cheap next to recognition, and anyone
    /// arriving here looking for the big win should keep reading past this line. The box count is
    /// where the time goes; see issue #21.
    /// </remarks>
    /// <summary>
    /// Border to surround the image with instead of the library default of 50, or null for it.
    /// </summary>
    /// <remarks>
    /// A measurement seam for <c>OcrHarness</c>, like <see cref="DetectorOverride"/>. Nothing in the
    /// application sets this.
    ///
    /// The library's 50 was inherited rather than chosen, so it was swept under the current models
    /// (RapidOcrNet 3.0.0, PP-OCRv6_det_tiny) across 0, 8, 16, 24, 32, 50, 64 and 96, on the two
    /// small capture fixtures, 25 subtitle strips and 6 game panels. Scored as the share of each
    /// frame's own best reading that a border returned:
    ///
    /// <code>
    ///   border      0      8     16     24     32     50     64     96
    ///   strips   93.5%  91.2%  92.7%  96.8%  96.1%  97.5%  96.3%  94.6%
    ///   panels   72.1%  71.5%  71.3%  81.4%  71.7%  99.1%  85.4%
    /// </code>
    ///
    /// 50 is the best value in every category, with both neighbours worse — a peak rather than a
    /// floor, so raising it is not "safer". The small values are not merely weaker: on a 264x56
    /// capture, borders of 8 and 16 return no boxes at all where 0 returns a fragment and 50 reads
    /// the whole thing. That is <see cref="AlignForDetector"/> showing through — the border decides
    /// what the aligned dimensions become, and a few of them land on geometry the detector dislikes.
    ///
    /// The border is also not the free choice it looks like on the clock. A strip reads in 43ms
    /// without it and 77ms with it, and almost all of that difference is recognition of text that
    /// no border failed to find: the detector's own input is the same size either way, because
    /// <c>ImgResize</c> caps the long side after the border is added.
    ///
    /// ITS COLOUR IS NOT A KNOB, AND THE QUESTION IS OPEN. The library fills the border itself and
    /// exposes no colour, so the only way to try another one is to draw the border here and ask for
    /// none — and that turned out not to be the same experiment. Reproducing the shipped
    /// composition by hand (align with transparent pixels, then a white border, then
    /// <c>Padding = 0</c>) still read less than the shipped path does, so something in how the
    /// library builds its own border is not accounted for, and every colour measured that way is
    /// measuring that difference as much as the colour. Worth knowing because subtitles are white
    /// text and a white border is the one combination nobody chose — but it needs the library's
    /// source, not another harness mode.
    /// </remarks>
    internal static int? DetectorPaddingOverride { get; set; }

    /// <summary>
    /// The three thresholds that turn the detector's probability map into boxes.
    /// </summary>
    /// <param name="BoxThresh">
    /// Where the probability map is cut into "text" and "not text". Higher leaves weakly answered
    /// strokes out of the component, which is what puts a box edge inside a glyph.
    /// </param>
    /// <param name="BoxScoreThresh">
    /// The mean probability a finished box must reach to be returned at all. A box under it is
    /// dropped inside the library, before anything this class can see or count.
    /// </param>
    /// <param name="UnClipRatio">
    /// How far the shrunken polygon is expanded back out. Too small and every box loses a little at
    /// each end.
    /// </param>
    internal readonly record struct DetectorThresholds(
        float BoxThresh,
        float BoxScoreThresh,
        float UnClipRatio);

    /// <summary>
    /// What PP-OCRv6_det_tiny was exported to be read with, from the <c>inference.yml</c> published
    /// beside the model.
    /// </summary>
    /// <remarks>
    /// Until #71 these were whatever <c>RapidOcrOptions.Default</c> happened to carry — 0.30, 0.50
    /// and 1.60, which are the library's generic numbers and not this model's. Every one of the
    /// three was wrong, in the direction that costs text: a stricter binarisation threshold leaves
    /// faint strokes out of a box, a stricter box score throws whole boxes away, and a larger unclip
    /// returns boxes taller than the glyphs in them — and box height is what the overlay sizes its
    /// font from.
    ///
    /// Measured on the model's own values against three workloads, all of which improve:
    ///
    /// <code>
    ///   workload                       shipped        exported
    ///   realtime subtitles, 45 frames  697 chars      701, boxes 5% tighter
    ///   realtime fallback size, 19     364 chars      371, boxes 6% tighter
    ///   screenshot flow, 16 frames     3481 chars     3522, 15 of 16 frames equal or better
    /// </code>
    ///
    /// Small, but free, and it is a correction rather than a tuning: the same class of mistake as
    /// reading a v6 detector with v5's normalisation statistics.
    ///
    /// WHAT WAS TRIED AND REJECTED. Raising the box score to reject the noise boxes that reach the
    /// screen as huge garbage — the obvious move — loses on every workload (682 against 697 on
    /// subtitles, 3450 against 3481 on screenshots) and reads nothing at all on more frames. The
    /// noise boxes it was aimed at survive it. See #71.
    /// </remarks>
    internal static readonly DetectorThresholds ExportedThresholds = new(0.2f, 0.4f, 1.4f);

    /// <summary>
    /// Detector post-processing thresholds to use instead of <see cref="ExportedThresholds"/>, or
    /// null for those.
    /// </summary>
    /// <remarks>
    /// A measurement seam for <c>OcrHarness</c>, like <see cref="DetectorPaddingOverride"/>. Nothing
    /// in the application sets this.
    ///
    /// Worth a seam because these three are the only part of the pipeline that can lose text without
    /// leaving a trace — <c>rawBlocks</c> is counted after the library has already applied
    /// <see cref="DetectorThresholds.BoxScoreThresh"/>, so a box it dropped is indistinguishable in
    /// the log from a box that was never found. #69 spent a round proving that by sweeping each of
    /// them one at a time, and missed the answer for exactly that reason: the other two stayed on the
    /// library's values while one moved, and what was wrong was all three together.
    /// </remarks>
    internal static DetectorThresholds? DetectorThresholdOverride { get; set; }

    /// <remarks>
    /// Internal rather than private only so OcrHarness can ask what the app would send. A measuring
    /// mode that rebuilt these itself would be measuring its own copy, and the copy is exactly the
    /// thing that drifts.
    /// </remarks>
    internal static RapidOcrOptions CreateOptions(int? maxDetectSize)
    {
        var options = RapidOcrOptions.Default with
        {
            ImgResize = maxDetectSize ?? ScreenshotDetectSize,
            DoAngle = false,
            Padding = DetectorPaddingOverride ?? RapidOcrOptions.Default.Padding,
        };

        var thresholds = DetectorThresholdOverride ?? ExportedThresholds;
        return options with
        {
            BoxThresh = thresholds.BoxThresh,
            BoxScoreThresh = thresholds.BoxScoreThresh,
            UnClipRatio = thresholds.UnClipRatio,
        };
    }

    // Everything between the library handing back its raw blocks and the caller getting usable ones.
    // One method rather than a chain repeated at each entry point: the order matters (see the
    // comments inside), and a second copy of it is the kind of thing that drifts a filter at a time.
    private static List<OcrTextBlock> ApplyBlockFilters(
        TextBlock[] textBlocks, string normalizedLanguage, bool isCjk, bool usesAutomaticLayout)
    {
        var converted = ConvertBlocks(textBlocks);

        // Every language, because an accented letter is a misread whichever script surrounds
        // it, and it costs the whole line its translation rather than just one character.
        converted = FoldBlockDiacritics(converted);

        // The lone-ideograph icon filter used to run here, on Latin pages only. It is off — see
        // StripLoneIdeographs, which is kept for whatever replaces it — because a cleanup that
        // deletes text on some source languages and not others is the one thing that cannot be
        // reconciled with reading the same picture the same way whatever the user picked.
        //
        // After normalisation, because that is where a CJK box is pulled in onto its glyphs and
        // the shape being judged becomes the real one. Before grouping, which happens further
        // out, so a stray box is never joined to the line beside it.
        var normalized = usesAutomaticLayout
            ? NormalizeAutomaticBlocks(converted)
            : NormalizeBlocks(converted, isCjk);

        return RemoveMisshapenBlocks(normalized, normalizedLanguage);
    }

    private static List<OcrTextBlock> ConvertBlocks(TextBlock[] textBlocks)
    {
        var blocks = new List<OcrTextBlock>(textBlocks.Length);
        foreach (var block in textBlocks)
        {
            var text = string.Concat(block.Chars ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(text) || block.BoxPoints is null || block.BoxPoints.Length == 0)
                continue;

            var confidence = block.CharScores is { Length: > 0 } scores ? scores.Average() : 1f;
            if (confidence < MinRecognitionConfidence)
                continue;

            // Kept blocks carry their score into the log as well as the rejected ones. Reading the
            // same subtitle several times produces several slightly different answers, and the
            // score is the only thing that says which of them to believe.
            if (Log.IsDebugEnabled)
                Log.Debug("ONNX OCR kept score={Score:0.00} text=\"{Text}\"", confidence, text);

            var left = block.BoxPoints.Min(p => p.X);
            var top = block.BoxPoints.Min(p => p.Y);
            var right = block.BoxPoints.Max(p => p.X);
            var bottom = block.BoxPoints.Max(p => p.Y);
            blocks.Add(new OcrTextBlock(
                text,
                new System.Windows.Rect(left, top, right - left, bottom - top),
                Confidence: confidence));
        }

        return blocks
            .OrderBy(b => b.Bounds.Y)
            .ThenBy(b => b.Bounds.X)
            .ToList();
    }

    /// <summary>
    /// Drops boxes that cannot be holding the text read out of them — see <see cref="BoxShapeNoise"/>.
    /// </summary>
    /// <remarks>
    /// Applies to every language, which the lone-ideograph rule that used to sit beside it did not.
    /// A Japanese or Korean capture had no noise filter at all before this, and the lone □ that a
    /// detector returns for a strip of interface is not a script-specific problem.
    /// </remarks>
    internal static List<OcrTextBlock> RemoveMisshapenBlocks(List<OcrTextBlock> blocks, string language)
    {
        List<OcrTextBlock>? kept = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            if (!BoxShapeNoise.IsTooWideForItsText(blocks[index]))
            {
                kept?.Add(blocks[index]);
                continue;
            }

            kept ??= [.. blocks.Take(index)];

            if (Log.IsDebugEnabled)
                Log.Debug(
                    "ONNX OCR dropped a misshapen box lang={Lang} {W:0}x{H:0} text=\"{Text}\"",
                    language, blocks[index].Bounds.Width, blocks[index].Bounds.Height, blocks[index].Text);
        }

        return kept ?? blocks;
    }

    private static List<OcrTextBlock> FoldBlockDiacritics(List<OcrTextBlock> blocks)
    {
        List<OcrTextBlock>? folded = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            var text = FoldLatinDiacritics(blocks[index].Text);
            if (text == blocks[index].Text)
            {
                folded?.Add(blocks[index]);
                continue;
            }

            // Copied lazily: an accented letter is rare, so nearly every pass keeps the list it
            // was given.
            folded ??= [.. blocks.Take(index)];
            folded.Add(blocks[index] with { Text = text });

            Log.Debug(
                "ONNX OCR folded accented letters lang=\"{Lang}\" \"{Before}\" -> \"{After}\"",
                "latin", blocks[index].Text, text);
        }

        return folded ?? blocks;
    }

    // Folds accented Latin letters onto their plain form: "șong" -> "song".
    //
    // PP-OCRv6 carries ~200 diacritical characters so one model can serve 46 Latin-script
    // languages. None of the languages this application reads (EN, ZH, ZH-HANT, JA, KO) use them,
    // so when one appears in a reading it is a misread of the plain letter — and it does more
    // damage than a wrong letter usually would, because the result is a word no translator knows.
    // Measured: "That kind of șong" (U+0219, Romanian s-with-comma) came back at 0.93 confidence,
    // far too sure to be caught by any score floor, and the line was translated as nonsense while
    // the very next frame read plain "song" and translated correctly.
    //
    // Deliberately limited to the Latin ranges. Normalising everything would decompose Japanese
    // voiced kana as well — が is か plus a combining mark — and stripping that mark would quietly
    // turn Japanese into a different word.
    internal static string FoldLatinDiacritics(string text)
    {
        if (!text.Any(IsLatinWithDiacritic))
            return text;

        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsLatinWithDiacritic(c))
            {
                folded.Append(c);
                continue;
            }

            // The decomposed form is the base letter followed by its combining marks, so the first
            // character that is not a mark is the letter wanted. Characters that do not decompose
            // at all (ø, đ) come back unchanged, which is the right answer for them too.
            var baseLetter = c.ToString()
                .Normalize(NormalizationForm.FormD)
                .FirstOrDefault(ch =>
                    CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);

            folded.Append(baseLetter == '\0' ? c : baseLetter);
        }

        return folded.ToString();
    }

    private static bool IsLatinWithDiacritic(char c) =>
        c is >= 'À' and <= 'ɏ'    // Latin-1 Supplement, Latin Extended-A and -B
        or >= 'Ḁ' and <= 'ỿ';     // Latin Extended Additional

    // Strips a single isolated Han ideograph glued to the start or end of a Latin word, which is
    // what an icon misread on a Latin page looks like. The letter-adjacency guard preserves date
    // glyphs like the 年/月/日 in "2026年5月8日" (those sit next to digits), and multi-ideograph runs
    // (真實中文 such as 翻譯這個網頁 / 免費) are never single, so are kept.
    //
    // A block that is nothing BUT one ideograph is deliberately not touched. It used to be dropped
    // outright, on the reasoning that a lone ideograph on an English page is an icon — and that is
    // sometimes true, but 攻 防 技 火 水 光 闇 標準 are how a Chinese or Japanese interface labels
    // things, and a screenshot is not required to be in one language just because the user named
    // one. The old rule deleted them under 英文 and under 自動 alike, silently and with nothing in
    // the log. Keeping a piece of OCR rubbish costs the reader one wrong word; deleting a real
    // label costs them the one thing they pointed the tool at.
    internal static string StripLoneIdeographs(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return text;

        if (text.Length >= 2 && LayoutScriptDetection.IsHanIdeograph(text[0]) && !LayoutScriptDetection.IsHanIdeograph(text[1]) && char.IsAsciiLetter(text[1]))
            text = text[1..].TrimStart();

        if (text.Length >= 2 && LayoutScriptDetection.IsHanIdeograph(text[^1]) && !LayoutScriptDetection.IsHanIdeograph(text[^2]) && char.IsAsciiLetter(text[^2]))
            text = text[..^1].TrimEnd();

        return text;
    }

    /// <summary>
    /// Chooses the layout path from one recognized block when the source language is automatic.
    /// Kana is unambiguously Japanese. Two Han characters are enough to identify real CJK text,
    /// while a lone Han character stays on the Latin path so the existing icon-noise filter can
    /// remove the common one-character misreads found in English interfaces.
    /// </summary>
    internal static bool UsesCjkLayoutForText(string text) =>
        text.Any(LayoutScriptDetection.IsKana) || text.Count(LayoutScriptDetection.IsHanIdeograph) >= 2;

    internal static List<OcrTextBlock> NormalizeAutomaticBlocks(List<OcrTextBlock> blocks)
    {
        var normalized = new List<OcrTextBlock>(blocks.Count);

        foreach (var block in blocks)
        {
            var isCjk = UsesCjkLayoutForText(block.Text);
            normalized.Add(NormalizeBlock(block, isCjk, AutomaticGlyphHeightFromPitch));
        }

        return normalized;
    }

    internal static List<OcrTextBlock> NormalizeBlocks(List<OcrTextBlock> blocks, bool isCjk)
        => blocks.Select(block => NormalizeBlock(block, isCjk)).ToList();

    /// <summary>
    /// Glyph body height estimated from a detection box: 0.82 of it, clamped against the average
    /// glyph pitch on wide lines where unclip leaves the box vertically loose.
    /// </summary>
    /// <remarks>
    /// ONNX/unclip can return vertically loose boxes on wide single lines. The average glyph pitch
    /// is a better proxy for the real line height than an over-tall detection rectangle.
    /// </remarks>
    private static double EstimateGlyphHeight(System.Windows.Rect box, int glyphCount, double glyphHeightFromPitch)
    {
        const double verticalScale = 0.82;

        var glyphHeight = box.Height * verticalScale;

        if (glyphCount >= ShortTextGlyphHeight.PitchCorrectedFromGlyphs &&
            box.Width > box.Height * 2)
        {
            var estimatedGlyphPitch = box.Width / glyphCount;
            glyphHeight = Math.Min(glyphHeight, estimatedGlyphPitch * glyphHeightFromPitch);
        }

        return Math.Max(1, glyphHeight);
    }

    /// <summary>
    /// The same estimate keyed on the block's own script rather than on the language the user
    /// picked, so it reads the same under 自動 as under 日文.
    /// </summary>
    /// <remarks>
    /// Mixed and Unknown get nothing: there is no single glyph body to estimate, and grouping
    /// falls back to the raw detection box for them. Latin carries the short-line correction the
    /// overlay's own height carries, because too few glyphs for the pitch clamp leaves 0.82 of the
    /// box standing, which is 1.7x the truth. CJK does not, matching how its box is normalised.
    /// </remarks>
    internal static double? LayoutGlyphHeightFor(OcrLayoutScript script, System.Windows.Rect box, string text)
    {
        if (script is not (OcrLayoutScript.Latin or OcrLayoutScript.Cjk))
            return null;

        var glyphCount = text.Count(c => !char.IsWhiteSpace(c));
        var glyphHeight = EstimateGlyphHeight(box, glyphCount, script == OcrLayoutScript.Cjk ? 1.18 : 1.3);

        return script == OcrLayoutScript.Cjk
            ? glyphHeight
            : ShortTextGlyphHeight.For(glyphHeight, box.Height, glyphCount);
    }

    private static OcrTextBlock NormalizeBlock(
        OcrTextBlock block,
        bool isCjk,
        double? glyphHeightFromPitchOverride = null)
    {
        // Convert the average source-glyph pitch (width / glyphCount) into the line height that
        // drives the overlay font size, clamping the unclipped (loose) detection box so text is
        // not rendered far too large. The multiplier is keyed on the *rendered* script, which is
        // always the translated CJK text — so a Latin source page must use ~the CJK ratio too,
        // not a Latin one. Measured EN-vs-KO box heights on the same screenshot showed the old
        // Latin value (2.0) rendered English ~1.7x larger than the Korean (CJK) path; 1.3 brings
        // it in line, leaving English just slightly larger than CJK.
        var glyphHeightFromPitch = glyphHeightFromPitchOverride ?? (isCjk ? 1.18 : 1.3);

        // Per block, from its own text, and the box exactly as the detector drew it. Both callers
        // reach here, and this runs before the CJK branch below rewrites Bounds, so it is the one
        // place the layout side gets told what it is looking at.
        var layoutScript = LayoutScriptDetection.For(block.Text);
        block = block with
        {
            LayoutScript = layoutScript,
            LayoutBounds = block.Bounds,
            LayoutGlyphHeight = LayoutGlyphHeightFor(layoutScript, block.Bounds, block.Text),
        };

        var bounds = block.Bounds;
        var glyphCount = block.Text.Count(c => !char.IsWhiteSpace(c));
        var glyphHeight = EstimateGlyphHeight(bounds, glyphCount, glyphHeightFromPitch);

        if (isCjk)
        {
            // CJK glyphs ≈ the detection box height, so shrinking + recentering the box
            // drives both the overlay font and its background coverage correctly.
            var adjustedY = bounds.Y + (bounds.Height - glyphHeight) / 2.0;
            return block with { Bounds = new System.Windows.Rect(bounds.X, adjustedY, bounds.Width, glyphHeight) };
        }

        // Latin: the detection box is much taller than the rendered CJK font. Keep the
        // full box as the bubble's coverage area (so it still hides the taller original
        // Latin glyphs) and carry the reduced glyph height separately for font sizing.
        //
        // Too few glyphs for the pitch clamp above to have run leaves that height at 0.82
        // of the box, which is 1.7x the truth — a one- or two-character line rendered
        // enormously. See ShortTextGlyphHeight for the measurements.
        return block with
        {
            RenderGlyphHeight = ShortTextGlyphHeight.For(glyphHeight, bounds.Height, glyphCount)
        };
    }

    // Debug on purpose, and the shipped configuration drops that level: this is the text the user
    // just had on screen, so it must never reach a log file that gets sent to anyone.
    /// <summary>
    /// Everything the detector found on a pass that ended up empty, with the score that decided it.
    /// </summary>
    private static void LogRejectedBlocks(string language, TextBlock[] blocks)
    {
        if (!Log.IsDebugEnabled)
            return;

        for (var index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            var text = string.Concat(block.Chars ?? []).Trim();
            var score = block.CharScores is { Length: > 0 } scores ? scores.Average() : 0;
            var points = block.BoxPoints;

            Log.Debug(
                "ONNX OCR rejected lang={Lang} index={Index} score={Score:0.00} floor={Floor:0.00} " +
                "bounds=({L},{T},{R},{B}) text=\"{Text}\"",
                language,
                index,
                score,
                MinRecognitionConfidence,
                points is { Length: > 0 } ? points.Min(p => p.X) : -1,
                points is { Length: > 0 } ? points.Min(p => p.Y) : -1,
                points is { Length: > 0 } ? points.Max(p => p.X) : -1,
                points is { Length: > 0 } ? points.Max(p => p.Y) : -1,
                text);
        }
    }

    private static void LogBlocks(string language, IReadOnlyList<OcrTextBlock> blocks)
    {
        if (!Log.IsDebugEnabled)
            return;

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            Log.Debug(
                "ONNX OCR block lang={Lang} index={Index} bounds=({X:0.#},{Y:0.#},{W:0.#},{H:0.#}) text=\"{Text}\"",
                language,
                index,
                block.Bounds.X,
                block.Bounds.Y,
                block.Bounds.Width,
                block.Bounds.Height,
                block.Text);
        }
    }

    // RapidOcrNet feeds the detector an image whose width and height have each been rounded down
    // to a multiple of DetectorAlignment — and rounded down a whole extra step, via
    // (n / 32 - 1) * 32 in ScaleParam.GetScaleParam. The two axes are quantised independently, so
    // the amount of squashing differs between them by an amount that depends on the exact capture
    // size. A 264x56 capture reaches the detector squashed to 0.88x horizontally but 0.62x
    // vertically; widen the same selection by six pixels and 270x60 comes out 0.86x by 1.00x. That
    // is why two captures of identical pixels could recognise differently ("domain" -> "donain"):
    // the user's selection box, not the glyphs, was choosing the aspect ratio.
    //
    // Detect() pads the bitmap by options.Padding on all four sides and, for anything below
    // ImgResize, targets exactly that padded long side — so the scale factor is already 1.0 on the
    // long axis and the quantisation is the only thing left distorting the image. Growing the
    // bitmap so the padded dimensions land on multiples of DetectorAlignment skips the
    // quantisation on both axes, and the detector sees native, undistorted pixels.
    //
    // Only the right and bottom edges grow, leaving block coordinates in the caller's frame, and
    // the added pixels are the same transparent black Detect() already surrounds every capture
    // with — this pushes that border out by under 32px rather than introducing a new edge.
    //
    // WHAT THIS DOES NOT FIX. Above ImgResize a real downscale takes over, and there this buys
    // nothing: Detect() scales the padded image so its long side lands on ImgResize exactly (a
    // multiple of 32, so that axis survives), then quantises the short axis anyway, costing it
    // another 0-63px. A 2330x1102 capture still reaches the detector squashed by 5.2%, a
    // 2554x1437 one by 2.9% — small next to the 30% above, but still an amount that moves with
    // the selection size. Captures that large therefore behave exactly as they did before this
    // change, residual squash included.
    //
    // Doing that downscale ourselves, uniformly, would remove the squash at every size. It was
    // measured and did not pay: on a ground-truth benchmark of a real capture across five canvas
    // sizes it scored 111/120 against 115/120 for leaving the library to it (and 110/120 with a
    // sharper resampler). That is inside the benchmark's noise, so the honest reading is "no
    // measurable gain" rather than "worse" — but an unmeasurable gain does not justify changing
    // how every large capture is fed. Revisit only with a benchmark strong enough to resolve it.
    private const int DetectorAlignment = 32;

    internal static SKBitmap AlignForDetector(SKBitmap src, int detectPadding)
    {
        var aligned = new SKBitmap(
            AlignedLength(src.Width, detectPadding),
            AlignedLength(src.Height, detectPadding),
            src.ColorType,
            src.AlphaType);

        using (var canvas = new SKCanvas(aligned))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(src, 0, 0);
        }

        return aligned;
    }

    // Smallest length >= the original for which length + Detect()'s own padding is a multiple of
    // DetectorAlignment.
    private static int AlignedLength(int length, int detectPadding)
    {
        var overshoot = (length + 2 * detectPadding) % DetectorAlignment;
        return overshoot == 0 ? length : length + (DetectorAlignment - overshoot);
    }

    /// <summary>
    /// Hands the captured pixels to Skia by copying them straight across.
    /// </summary>
    /// <remarks>
    /// This used to encode the bitmap to PNG and decode it again, which is a lot of work to move
    /// pixels between two in-memory buffers — compression and decompression, entirely discarded.
    /// A screenshot pays it once and nobody notices; realtime translation pays it on every pass of
    /// every watched region, several times a second, for as long as the session runs.
    ///
    /// The format is not a free choice: it is whatever the decoder used to hand back, because
    /// everything downstream was tuned against that. Measured against the old path on four capture
    /// sizes, Bgra8888/Premul reproduces its output pixel for pixel — Format32bppArgb is already
    /// BGRA in memory, and a screen grab's alpha is 255, which premultiplied leaves untouched.
    /// Declaring the surface opaque instead is the tempting simplification and a real bug: it also
    /// changes what <see cref="AlignForDetector"/>'s transparent fill means, turning the padding
    /// from clear to black, which moved recognised text at the edges of every size tested.
    /// </remarks>
    internal static SKBitmap ConvertToSkBitmap(Bitmap bitmap)
    {
        var source = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            // Format32bppArgb is BGRA in memory, which is Bgra8888 here — so the copy is a copy and
            // never a per-pixel conversion.
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var skBitmap = new SKBitmap(info);

            var destination = skBitmap.GetPixels();
            if (destination == IntPtr.Zero)
            {
                skBitmap.Dispose();
                throw new InvalidOperationException(LocalizationService.Get("S.Error.OcrImageConvert"));
            }

            // Row by row rather than one block: GDI+ and Skia pad their rows independently, so the
            // two strides agree only by coincidence.
            var rowBytes = bitmap.Width * 4;
            var row = new byte[rowBytes];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(source.Scan0 + y * source.Stride, row, 0, rowBytes);
                Marshal.Copy(row, 0, destination + y * skBitmap.RowBytes, rowBytes);
            }

            return skBitmap;
        }
        finally
        {
            bitmap.UnlockBits(source);
        }
    }

    private static void EnsureModelFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                LocalizationService.Format("S.Error.OcrModelMissing", path), path);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _idleReleaseTimer.Dispose();
            _current?.Dispose();
            _current = null;
            _currentModelKey = null;
        }

        // Outside _sync: a waiter released by disposal must not need the lock we are holding.
        _inferenceGate.Dispose();
    }

    internal sealed record RapidOcrRuntime(string ModelName, RapidOcr Engine) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }
}
