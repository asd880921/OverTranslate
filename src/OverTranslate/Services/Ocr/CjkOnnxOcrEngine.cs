using System.Drawing;
using System.IO;
using NLog;
using RapidOcrNet;
using SkiaSharp;

namespace OverTranslate.Services.Ocr;

internal sealed class CjkOnnxOcrEngine : IOcrEngine
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly string ModelRoot =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocrmodels", "onnx");
    private static readonly int ThreadCount = Math.Clamp(Environment.ProcessorCount, 1, 2);

    private readonly object _sync = new();
    private readonly Dictionary<string, RapidOcrRuntime> _runtimes = [];

    public Task<List<OcrTextBlock>> RecognizeAsync(Bitmap bitmap, string sourceLanguage)
    {
        if (!OcrLanguageRouter.UsesCjkOnnx(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        return Task.Run(() =>
        {
            var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
            var runtime = GetRuntime(normalizedLanguage);

            Log.Debug(
                "Running CJK ONNX OCR baseline on {W}x{H} bitmap, lang={Lang}, model={Model}, threads={Threads}",
                bitmap.Width,
                bitmap.Height,
                normalizedLanguage,
                runtime.ModelName,
                ThreadCount);

            using var skBitmap = ConvertToSkBitmap(bitmap);
            var result = runtime.Engine.Detect(skBitmap, CreateOptions());
            var blocks = NormalizeBlocks(ConvertBlocks(result.TextBlocks));

            Log.Debug(
                "CJK ONNX OCR baseline lang={Lang} rawBlocks={RawBlocks} blocks={Blocks} strLen={StrLen}",
                normalizedLanguage,
                result.TextBlocks.Length,
                blocks.Count,
                result.StrRes?.Length ?? 0);

            LogBlocks(normalizedLanguage, blocks);
            return blocks;
        });
    }

    internal static string GetModelKeyForLanguage(string language) =>
        OcrLanguageRouter.Normalize(language) == "KO" ? "korean" : "cjk";

    private RapidOcrRuntime GetRuntime(string language)
    {
        var modelKey = GetModelKeyForLanguage(language);
        lock (_sync)
        {
            if (_runtimes.TryGetValue(modelKey, out var runtime))
                return runtime;

            runtime = CreateRuntime(modelKey);
            _runtimes[modelKey] = runtime;
            return runtime;
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

    private static RapidOcrOptions CreateOptions() => RapidOcrOptions.Default;

    private static List<OcrTextBlock> ConvertBlocks(TextBlock[] textBlocks)
    {
        var blocks = new List<OcrTextBlock>(textBlocks.Length);
        foreach (var block in textBlocks)
        {
            var text = string.Concat(block.Chars ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(text) || block.BoxPoints is null || block.BoxPoints.Length == 0)
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

    private static List<OcrTextBlock> NormalizeBlocks(List<OcrTextBlock> blocks)
    {
        const double verticalScale = 0.82;

        return blocks
            .Select(block =>
            {
                var bounds = block.Bounds;
                var adjustedHeight = bounds.Height * verticalScale;
                var glyphCount = block.Text.Count(c => !char.IsWhiteSpace(c));

                // ONNX can return vertically loose boxes on wider captures.
                // For horizontal CJK/Korean text, average glyph width is a better proxy
                // for the real line height than an over-tall detection rectangle.
                if (glyphCount >= 4 && bounds.Width > bounds.Height * 2)
                {
                    var estimatedGlyphHeight = bounds.Width / glyphCount;
                    var maxExpectedHeight = estimatedGlyphHeight * 1.18;
                    adjustedHeight = Math.Min(adjustedHeight, maxExpectedHeight);
                }

                adjustedHeight = Math.Max(1, adjustedHeight);
                var adjustedY = bounds.Y + (bounds.Height - adjustedHeight) / 2.0;
                return block with { Bounds = new System.Windows.Rect(bounds.X, adjustedY, bounds.Width, adjustedHeight) };
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
                "CJK ONNX OCR block lang={Lang} index={Index} bounds=({X:0.#},{Y:0.#},{W:0.#},{H:0.#}) text=\"{Text}\"",
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
            foreach (var runtime in _runtimes.Values)
                runtime.Dispose();

            _runtimes.Clear();
        }
    }

    private sealed record RapidOcrRuntime(string ModelName, RapidOcr Engine) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }
}
