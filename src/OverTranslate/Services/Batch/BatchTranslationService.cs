using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NLog;
using OverTranslate.Layout;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Brush = System.Windows.Media.Brush;
using Rect = System.Windows.Rect;
using Point = System.Windows.Point;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace OverTranslate.Services.Batch;

/// <summary>
/// One queued image. <see cref="Regions"/> empty means the whole image is scanned; otherwise only
/// those rectangles (in image pixels) are, which is how a comic page with several speech bubbles
/// gets translated without the renderer chewing on the artwork between them.
/// </summary>
public sealed record BatchImage(string Path, IReadOnlyList<Rect> Regions)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record BatchProgress(int Completed, int Total, string FileName);

public sealed record BatchFailure(string FileName, string Reason);

public sealed record BatchResult(
    int Succeeded, IReadOnlyList<BatchFailure> Failures, bool Cancelled, string OutputDirectory);

/// <summary>
/// Runs the existing capture pipeline — OCR, colour sampling, translation, bubble layout — over a
/// list of image files and writes each translated page out as a new PNG. One image failing never
/// stops the run: the batch is often dozens of pages and losing the rest to a single unreadable
/// file would waste far more of the user's time than skipping it.
/// </summary>
public sealed class BatchTranslationService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Enough width for the translation to read as a sentence, without the bubble swallowing the
    // panel around it. Tuned against a comic page: below ~2 the text stacks into a narrow ribbon.
    private const double VerticalBubbleWidthFactor = 2.2;

    private readonly OcrService _ocrService = new();
    private readonly TranslationService _translationService = new();
    private readonly Dispatcher _dispatcher;

    /// <param name="dispatcher">
    /// The UI dispatcher. Compositing uses WPF drawing objects, which are thread-affine, so that
    /// step is marshalled here while OCR and translation stay off the UI thread.
    /// </param>
    public BatchTranslationService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task<BatchResult> RunAsync(
        IReadOnlyList<BatchImage> images,
        string outputDirectory,
        string sourceLanguage,
        string targetLanguage,
        string apiKey,
        bool verticalText = false,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var failures = new List<BatchFailure>();
        int succeeded = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var image = images[i];
            progress?.Report(new BatchProgress(i, images.Count, image.FileName));

            try
            {
                await TranslateOneAsync(
                    image, outputDirectory, sourceLanguage, targetLanguage, apiKey, verticalText,
                    usedNames, cancellationToken);
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                return new BatchResult(succeeded, failures, Cancelled: true, outputDirectory);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Batch translation failed for {File}", image.Path);
                failures.Add(new BatchFailure(image.FileName, Describe(ex)));
            }
        }

        progress?.Report(new BatchProgress(images.Count, images.Count, string.Empty));
        return new BatchResult(succeeded, failures, Cancelled: false, outputDirectory);
    }

    private async Task TranslateOneAsync(
        BatchImage image,
        string outputDirectory,
        string sourceLanguage,
        string targetLanguage,
        string apiKey,
        bool verticalText,
        HashSet<string> usedNames,
        CancellationToken cancellationToken)
    {
        using var source = LoadUnlocked(image.Path);

        var full = new Rect(0, 0, source.Width, source.Height);
        var regions = image.Regions.Count == 0
            ? [full]
            : image.Regions.Select(r => Rect.Intersect(r, full)).Where(r => r is { Width: >= 8, Height: >= 8 }).ToList();

        var rendered = new List<RenderedRegion>();
        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var crop = Crop(source, region);
            var recognized = await _ocrService.RecognizeAsync(
                crop, sourceLanguage, cancellationToken, verticalText);
            if (recognized.Count == 0)
                continue;

            var (translated, _) = await _translationService.TranslateAsync(
                recognized, sourceLanguage, targetLanguage, apiKey, cancellationToken: cancellationToken);

            rendered.Add(new RenderedRegion(region, WithSampledColors(translated, crop)));
        }

        var outputPath = ReserveOutputPath(outputDirectory, image.FileName, usedNames);

        // Even a page where nothing was recognised is written out, so the output folder mirrors the
        // input one — a missing file would read as "the run broke here", not "no text on this page".
        await _dispatcher.InvokeAsync(
            () => Compose(
                image.Path, source.Width, source.Height, rendered,
                sourceLanguage, targetLanguage, verticalText, outputPath));
    }

    private sealed record RenderedRegion(Rect Region, IReadOnlyList<TranslatedBlock> Blocks);

    private static List<TranslatedBlock> WithSampledColors(List<TranslatedBlock> blocks, Bitmap crop)
    {
        var data = crop.LockBits(
            new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            return blocks
                .Select(block =>
                {
                    var (background, text) = BlockColorSampler.Sample(data, crop.Width, crop.Height, block.Bounds);
                    return block with { BackgroundColor = background, TextColor = text };
                })
                .ToList();
        }
        finally
        {
            crop.UnlockBits(data);
        }
    }

    /// <summary>
    /// Draws the original page and then every bubble on top, at the image's own pixel scale — no DPI
    /// conversion, because the output is a file rather than a screen.
    /// </summary>
    private static void Compose(
        string sourcePath,
        int width,
        int height,
        IReadOnlyList<RenderedRegion> regions,
        string sourceLanguage,
        string targetLanguage,
        bool verticalText,
        string outputPath)
    {
        var page = new BitmapImage();
        page.BeginInit();
        page.CacheOption = BitmapCacheOption.OnLoad;   // so the file is not held open
        page.UriSource = new Uri(sourcePath);
        page.EndInit();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(page, new Rect(0, 0, width, height));

            var typeface = OverlayBubbleLayout.CreateTypeface();

            foreach (var region in regions)
            {
                var context = new OverlayLayoutContext(
                    DpiX: 1, DpiY: 1,
                    OriginPhysX: region.Region.X,
                    OriginPhysY: region.Region.Y,
                    OriginPhysWidth: region.Region.Width,
                    OriginPhysHeight: region.Region.Height,
                    SurfacePhysLeft: 0,
                    SurfacePhysTop: 0,
                    CanvasWidth: width,
                    CanvasHeight: height,
                    SourceLanguage: sourceLanguage,
                    TargetLanguage: targetLanguage,
                    // A vertical column is a narrow target and the page offers no selection edge to
                    // stop at, so without this a two-word line spreads right across the drawing.
                    MaxWidthFactor: verticalText ? VerticalBubbleWidthFactor : double.PositiveInfinity,
                    VerticalText: verticalText);

                // Backgrounds for the whole region first, then the text: a later bubble's opaque
                // background must never land on an earlier bubble's glyphs.
                var bubbles = OverlayBubbleLayout.Calculate(region.Blocks, context);

                foreach (var bubble in bubbles)
                    dc.DrawRectangle(
                        new SolidColorBrush(bubble.Background), null,
                        new Rect(bubble.Left, bubble.Top, bubble.Width, bubble.Height));

                foreach (var bubble in bubbles)
                    DrawBubbleText(dc, bubble, typeface);
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static void DrawBubbleText(DrawingContext dc, OverlayBubble bubble, Typeface typeface)
    {
        if (bubble.Vertical)
        {
            foreach (var (glyph, cell) in OverlayBubbleLayout.VerticalCells(bubble))
            {
                var drawn = new FormattedText(
                    glyph.ToString(),
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    bubble.FontSize,
                    new SolidColorBrush(bubble.Foreground),
                    96);

                // Centred in its cell, so a column of mixed glyph widths still reads as a column.
                var origin = new Point(
                    cell.X + (cell.Width - drawn.Width) / 2,
                    cell.Y + (cell.Height - drawn.Height) / 2);

                bool turn = OverlayBubbleLayout.RotatesInVerticalText(glyph);
                if (turn)
                    dc.PushTransform(new RotateTransform(
                        90, cell.X + cell.Width / 2, cell.Y + cell.Height / 2));

                dc.DrawText(drawn, origin);

                if (turn)
                    dc.Pop();
            }

            return;
        }

        // Matches the padding the on-screen bubble's Border applies (3,2,3,2).
        const double padX = 3;
        const double padY = 2;

        Brush foreground = new SolidColorBrush(bubble.Foreground);
        var text = new FormattedText(
            bubble.Text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            bubble.FontSize,
            foreground,
            96)
        {
            MaxTextWidth = Math.Max(1, bubble.Width - padX * 2),
            MaxTextHeight = Math.Max(1, bubble.Height - padY * 2),
            Trimming = bubble.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };

        // The on-screen TextBlock is vertically centred inside its bubble; match that here.
        double y = bubble.Top + Math.Max(padY, (bubble.Height - text.Height) / 2);
        dc.DrawText(text, new Point(bubble.Left + padX, y));
    }

    /// <summary>
    /// Loads a copy so the source file is not left locked — users pick images straight out of
    /// folders they are still working in.
    /// </summary>
    private static Bitmap LoadUnlocked(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var loaded = new Bitmap(stream);
        var copy = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(copy);
        graphics.DrawImage(loaded, new Rectangle(0, 0, copy.Width, copy.Height));
        return copy;
    }

    private static Bitmap Crop(Bitmap source, Rect region)
    {
        var rect = new Rectangle(
            (int)Math.Round(region.X), (int)Math.Round(region.Y),
            Math.Max(1, (int)Math.Round(region.Width)), Math.Max(1, (int)Math.Round(region.Height)));

        var crop = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(crop);
        graphics.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
        return crop;
    }

    /// <summary>
    /// Keeps the original file name so pages stay in order, but never overwrites: two folders can
    /// each hold a "001.png", and silently losing one of them would be the worst possible outcome
    /// of a batch the user just waited on.
    /// </summary>
    internal static string ReserveOutputPath(string directory, string fileName, HashSet<string> usedNames)
    {
        var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var candidate = baseName + ".png";
        int suffix = 2;

        while (!usedNames.Add(candidate) || File.Exists(System.IO.Path.Combine(directory, candidate)))
        {
            candidate = $"{baseName} ({suffix}).png";
            suffix++;
        }

        return System.IO.Path.Combine(directory, candidate);
    }

    private static string Describe(Exception ex) => ex switch
    {
        NotSupportedException => "目前不支援此語言的辨識",
        FileNotFoundException or DirectoryNotFoundException => "找不到檔案",
        UnauthorizedAccessException => "沒有讀取或寫入權限",
        OutOfMemoryException => "圖片過大或格式不支援",
        _ => ex.Message,
    };

    public void Dispose() => _ocrService.Dispose();
}
