using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using NLog;
using RapidOcrNet;
using SkiaSharp;

namespace OverTranslate.Services.Ocr;

internal sealed class OnnxOcrEngine : IOcrEngine
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly string ModelRoot =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocrmodels", "onnx");
    private static readonly int ThreadCount = Math.Clamp(Environment.ProcessorCount, 1, 2);

    // Blocks whose average per-character recognition confidence falls below this are
    // discarded as noise / icon misdetections. Mirrors the old Tesseract MinWordConfidence
    // (60/100) that the previous English engine relied on to drop non-text regions.
    private const double MinRecognitionConfidence = 0.6;

    // A runtime holds det + cls + rec ONNX sessions plus their CPU memory arenas (hundreds of
    // MB with the larger ImgResize). This is a tray-resident, occasional-use tool, so we keep
    // only the active model loaded AND release it after a period of inactivity, returning that
    // memory to baseline while idle. The next capture transparently reloads the needed model.
    private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromMinutes(1);

    // Long enough that no real inference could still be running, short enough that a caller stuck
    // behind a wedged one gets an error instead of never returning.
    private static readonly TimeSpan ModelSwapDrainTimeout = TimeSpan.FromSeconds(10);

    // How many inferences may run at once. Each already uses ThreadCount threads of its own, so the
    // budget is cores/4 rather than cores/2. Measured on a 16-core machine against a 1200x200 screen
    // grab: one pass 320ms; four concurrent passes 502ms in total, so 2.5x the throughput for 1.6x
    // the latency. Five was slower than four, which is where the cap comes from.
    private static readonly int InferenceSlots = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

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

    public OnnxOcrEngine() =>
        _idleReleaseTimer = new System.Threading.Timer(_ => ReleaseIdleRuntime());

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
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default)
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
                return RecognizeCore(bitmap, sourceLanguage);
            }
            finally
            {
                _inferenceGate.Release();
            }
        }, cancellationToken);
    }

    private List<OcrTextBlock> RecognizeCore(Bitmap bitmap, string sourceLanguage)
    {
        var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
        var isCjk = OcrLanguageRouter.UsesCjkOnnx(normalizedLanguage);

        // Select the runtime and register an in-use reference atomically under _sync, so the
        // idle timer cannot dispose it between selection and Detect. The matching release
        // (which also re-arms the idle countdown) runs in the finally, under _sync.
        var runtime = AcquireRuntime(normalizedLanguage);
        try
        {
            Log.Info(
                "Running ONNX OCR on {W}x{H} bitmap, lang={Lang}, model={Model}, threads={Threads}",
                bitmap.Width,
                bitmap.Height,
                normalizedLanguage,
                runtime.ModelName,
                ThreadCount);

            var options = CreateOptions();
            using var skBitmap = ConvertToSkBitmap(bitmap);
            using var detectorInput = AlignForDetector(skBitmap, options.Padding);
            var result = runtime.Engine.Detect(detectorInput, options);
            var converted = ConvertBlocks(result.TextBlocks);

            // On a non-CJK (Latin) page the shared general model occasionally misreads icons
            // as a lone Han ideograph; strip that noise without touching real embedded Chinese.
            if (!isCjk)
                converted = RemoveIconIdeographNoise(converted);

            var blocks = NormalizeBlocks(converted, isCjk);

            // Counts and lengths only — enough to tell "found nothing" from "found the wrong thing"
            // without the recognised text itself, which LogBlocks keeps at Debug.
            Log.Info(
                "ONNX OCR lang={Lang} rawBlocks={RawBlocks} blocks={Blocks} strLen={StrLen}",
                normalizedLanguage,
                result.TextBlocks.Length,
                blocks.Count,
                result.StrRes?.Length ?? 0);

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
            // EN uses the PP-OCRv5 general ("cjk") model rather than a dedicated Latin model.
            // English UI captures very often contain embedded Chinese (chrome, labels, ratings),
            // which a Latin-only model dropped or garbled; the general model reads Latin AND those
            // CJK glyphs in one pass, and on real captures injected fewer stray foreign-Latin
            // glyphs (e.g. ¡) than the Latin model. The text is still Latin, so source-language
            // routing (UsesCjkOnnx) keeps EN on the Latin layout path. Lone-ideograph icon misreads
            // from this broader model are stripped by RemoveIconIdeographNoise.
            _ => "cjk",
        };

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
                    throw new InvalidOperationException("等待其他辨識結束以切換 OCR 模型時逾時。");

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

            if (_disposed)
                return;

            if (_inUse == 0)
                _idleReleaseTimer.Change(IdleReleaseDelay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void ReleaseIdleRuntime()
    {
        lock (_sync)
        {
            // Skip disposal if we are gone, a Detect is still running against the runtime, or
            // there is nothing to release. A timer callback that was queued before a later
            // Change() still fires; the _inUse check is what makes that stale callback benign.
            if (_disposed || _inUse > 0 || _current is null)
                return;

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
    }

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

        var engine = new RapidOcr();
        engine.InitModels(detPath, clsPath, recPath, dictPath, ThreadCount);
        return new RapidOcrRuntime(modelName, engine);
    }

    private static RapidOcrOptions CreateOptions() =>
        // Default ImgResize (1024) downscales wide UI screenshots and destroys small-text
        // detail, causing recognition errors. Raising it keeps typical captures near native
        // resolution; smaller images are unaffected (no upscaling).
        RapidOcrOptions.Default with { ImgResize = 2048 };

    private static List<OcrTextBlock> ConvertBlocks(TextBlock[] textBlocks)
    {
        var blocks = new List<OcrTextBlock>(textBlocks.Length);
        foreach (var block in textBlocks)
        {
            var text = string.Concat(block.Chars ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(text) || block.BoxPoints is null || block.BoxPoints.Length == 0)
                continue;

            if (block.CharScores is { Length: > 0 } &&
                block.CharScores.Average() < MinRecognitionConfidence)
                continue;

            var left = block.BoxPoints.Min(p => p.X);
            var top = block.BoxPoints.Min(p => p.Y);
            var right = block.BoxPoints.Max(p => p.X);
            var bottom = block.BoxPoints.Max(p => p.Y);
            blocks.Add(new OcrTextBlock(text, new System.Windows.Rect(left, top, right - left, bottom - top)));
        }

        return blocks
            .OrderBy(b => b.Bounds.Y)
            .ThenBy(b => b.Bounds.X)
            .ToList();
    }

    // Removes lone-Han-ideograph icon misreads from a Latin page's blocks. English never contains
    // a Han ideograph and real embedded Chinese labels are runs of >= 2 ideographs, so this leaves
    // genuine text untouched while dropping graphic/icon noise the general model reads as e.g. 白.
    private static List<OcrTextBlock> RemoveIconIdeographNoise(List<OcrTextBlock> blocks)
    {
        var cleaned = new List<OcrTextBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            var text = StripLoneIdeographs(block.Text);
            if (text.Length == 0)
                continue; // the whole block was a single ideograph (an icon) -> drop it
            cleaned.Add(text == block.Text ? block : block with { Text = text });
        }

        return cleaned;
    }

    // Strips a single isolated Han ideograph when it is clearly icon noise: either the block is
    // exactly one ideograph, or one is glued to the start/end of a Latin word. The letter-adjacency
    // guard preserves date glyphs like the 年/月/日 in "2026年5月8日" (those sit next to digits), and
    // multi-ideograph runs (真實中文 such as 翻譯這個網頁 / 免費) are never single, so are kept.
    internal static string StripLoneIdeographs(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return text;

        if (text.Length == 1 && IsHanIdeograph(text[0]))
            return string.Empty;

        if (text.Length >= 2 && IsHanIdeograph(text[0]) && !IsHanIdeograph(text[1]) && char.IsAsciiLetter(text[1]))
            text = text[1..].TrimStart();

        if (text.Length >= 2 && IsHanIdeograph(text[^1]) && !IsHanIdeograph(text[^2]) && char.IsAsciiLetter(text[^2]))
            text = text[..^1].TrimEnd();

        return text;
    }

    private static bool IsHanIdeograph(char c) =>
        c is >= '一' and <= '鿿' || // CJK Unified Ideographs
        c is >= '㐀' and <= '䶿' || // Extension A
        c is >= '豈' and <= '﫿';   // Compatibility Ideographs

    private static List<OcrTextBlock> NormalizeBlocks(List<OcrTextBlock> blocks, bool isCjk)
    {
        const double verticalScale = 0.82;

        // Convert the average source-glyph pitch (width / glyphCount) into the line height that
        // drives the overlay font size, clamping the unclipped (loose) detection box so text is
        // not rendered far too large. The multiplier is keyed on the *rendered* script, which is
        // always the translated CJK text — so a Latin source page must use ~the CJK ratio too,
        // not a Latin one. Measured EN-vs-KO box heights on the same screenshot showed the old
        // Latin value (2.0) rendered English ~1.7x larger than the Korean (CJK) path; 1.3 brings
        // it in line, leaving English just slightly larger than CJK.
        var glyphHeightFromPitch = isCjk ? 1.18 : 1.3;

        return blocks
            .Select(block =>
            {
                var bounds = block.Bounds;
                var glyphHeight = bounds.Height * verticalScale;
                var glyphCount = block.Text.Count(c => !char.IsWhiteSpace(c));

                // ONNX/unclip can return vertically loose boxes on wide single lines.
                // The average glyph pitch is a better proxy for the real line height than
                // an over-tall detection rectangle.
                if (glyphCount >= 4 && bounds.Width > bounds.Height * 2)
                {
                    var estimatedGlyphPitch = bounds.Width / glyphCount;
                    var maxExpectedHeight = estimatedGlyphPitch * glyphHeightFromPitch;
                    glyphHeight = Math.Min(glyphHeight, maxExpectedHeight);
                }

                glyphHeight = Math.Max(1, glyphHeight);

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
                return block with { SourceGlyphHeight = glyphHeight };
            })
            .ToList();
    }

    // Debug on purpose, and the shipped configuration drops that level: this is the text the user
    // just had on screen, so it must never reach a log file that gets sent to anyone.
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

    private static SKBitmap AlignForDetector(SKBitmap src, int detectPadding)
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
    private static SKBitmap ConvertToSkBitmap(Bitmap bitmap)
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
                throw new InvalidOperationException("無法將影像轉換為 ONNX OCR 可讀格式。");
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
            throw new FileNotFoundException($"找不到 OCR 模型檔案：{path}", path);
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

    private sealed record RapidOcrRuntime(string ModelName, RapidOcr Engine) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }
}
