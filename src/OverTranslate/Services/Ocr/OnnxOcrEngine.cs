using System.Drawing;
using System.IO;
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

    private readonly object _sync = new();
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

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        return Task.Run(() =>
        {
            var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
            var isCjk = OcrLanguageRouter.UsesCjkOnnx(normalizedLanguage);

            // Select the runtime and register an in-use reference atomically under _sync, so the
            // idle timer cannot dispose it between selection and Detect. The matching release
            // (which also re-arms the idle countdown) runs in the finally, under _sync.
            var runtime = AcquireRuntime(normalizedLanguage);
            try
            {
                Log.Debug(
                    "Running ONNX OCR on {W}x{H} bitmap, lang={Lang}, model={Model}, threads={Threads}",
                    bitmap.Width,
                    bitmap.Height,
                    normalizedLanguage,
                    runtime.ModelName,
                    ThreadCount);

                using var skBitmap = ConvertToSkBitmap(bitmap);
                var result = runtime.Engine.Detect(skBitmap, CreateOptions());
                var blocks = NormalizeBlocks(ConvertBlocks(result.TextBlocks), isCjk);

                Log.Debug(
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
        });
    }

    internal static string GetModelKeyForLanguage(string language) =>
        OcrLanguageRouter.Normalize(language) switch
        {
            "EN" => "en",
            "KO" => "korean",
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

            // A different model is requested. Swapping is only safe when no Detect is running
            // against the current runtime; _inUse > 0 here would mean two captures on different
            // languages overlap, which the single-capture UI never produces. Guard anyway.
            if (_inUse > 0)
                throw new InvalidOperationException("無法在辨識進行中切換 OCR 模型。");

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

    private static SKBitmap ConvertToSkBitmap(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var skBitmap = SKBitmap.Decode(ms);
        return skBitmap ?? throw new InvalidOperationException("無法將影像轉換為 ONNX OCR 可讀格式。");
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
    }

    private sealed record RapidOcrRuntime(string ModelName, RapidOcr Engine) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }
}
