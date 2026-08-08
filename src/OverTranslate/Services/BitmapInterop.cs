using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// System.Drawing.Imaging and System.Windows.Media both carry a PixelFormat, and this file is the one
// place that legitimately needs both worlds at once.
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace OverTranslate.Services;

/// <summary>
/// Between the two halves of this application's imaging: screens are grabbed with GDI+
/// (<see cref="Bitmap"/>) and everything drawn or saved afterwards is WPF.
/// </summary>
internal static class BitmapInterop
{
    /// <summary>
    /// Wraps a GDI+ bitmap as a frozen <see cref="BitmapSource"/>.
    /// </summary>
    /// <param name="dpi">
    /// 96 means "one bitmap pixel is one device-independent unit", which is what a caller drawing at
    /// physical resolution wants. Pass the real DPI only when the image is to be laid out at its
    /// natural size on a scaled display.
    /// </param>
    /// <remarks>
    /// The pixels are copied during Create, so the source bitmap may be disposed straight after —
    /// and the result is frozen, so it can cross to another thread or outlive the grab it came from.
    /// </remarks>
    public static BitmapSource ToBitmapSource(Bitmap bitmap, double dpi = 96)
    {
        var locked = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            GdiPixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(
                bitmap.Width, bitmap.Height, dpi, dpi,
                PixelFormats.Bgra32,
                null,
                locked.Scan0,
                Math.Abs(locked.Stride) * bitmap.Height,
                locked.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }
}
