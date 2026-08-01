using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

/// <summary>
/// Saves screenshots to a local folder. The image handed in is the same composited bitmap the
/// "copy screenshot" flow puts on the clipboard, so the saved file always matches what was copied.
/// </summary>
public static class ScreenshotSaveService
{
    /// <summary>圖片\OverTranslate — used whenever the user hasn't picked a custom folder.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OverTranslate");

    /// <summary>The folder currently in effect: the user's choice, or the default when unset/blank.</summary>
    public static string ResolveDirectory(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : configured.Trim();

    /// <summary>
    /// Writes <paramref name="image"/> as a PNG into <paramref name="directory"/>, creating the
    /// folder when missing. The file name carries a millisecond timestamp so rapid captures
    /// never overwrite each other. Returns the full path of the written file.
    /// </summary>
    public static string Save(BitmapSource image, string? directory = null)
    {
        var dir = ResolveDirectory(directory);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"OverTranslate_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }

    /// <summary>Opens the folder in File Explorer, creating it first so the window isn't empty-handed.</summary>
    public static void OpenFolder(string? directory = null)
    {
        var dir = ResolveDirectory(directory);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
