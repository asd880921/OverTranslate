using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

public static class AppIconService
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = GetStream("icons.icon_32.ico");
        return new System.Drawing.Icon(stream);
    }

    public static ImageSource CreateWindowIcon()
    {
        using var stream = GetStream("icons.icon_256.ico");
        using var icon   = new System.Drawing.Icon(stream);
        var src = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }

    private static System.IO.Stream GetStream(string relativeName)
    {
        var name = $"OverTranslate.{relativeName}";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
    }
}
