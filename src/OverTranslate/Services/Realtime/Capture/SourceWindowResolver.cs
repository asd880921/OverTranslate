using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Works out which application a set of framed regions is watching, so the capture can read that
/// window directly instead of reading the screen it happens to be drawn on.
/// </summary>
/// <remarks>
/// This is the price of window capture, and it is worth naming plainly: 即時翻譯 asks the user to
/// frame a rectangle, not to pick an application. The rectangle is the whole interface, it is what
/// makes the feature usable over a game that has no window worth choosing, and nothing here is
/// allowed to replace it with a window picker. So the window has to be inferred, and the inference
/// has to be honest about failing — a wrong answer is not a worse crop, it is translating a
/// different application.
///
/// The inference is a vote, not a lookup. A single centre point is wrong often enough to matter: a
/// subtitle band is frequently framed over the letterboxed black of a video, over a transparent
/// game HUD, or with its middle on a tooltip that appeared a moment ago. Several points across the
/// region, each resolved to its top-level window, and a clear majority required — that turns those
/// cases into an ambiguous answer rather than a confident wrong one.
///
/// Two kinds of window are never the answer. This process's own windows, which is the entire point
/// of capturing a window in the first place. And the desktop itself: a region over wallpaper or
/// icons has no application behind it, so window capture simply does not apply there and the caller
/// must be told so rather than handed the shell.
/// </remarks>
public static class SourceWindowResolver
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // How much of the vote one window must take. Two thirds means a region lying mostly over the
    // watched application still resolves when a corner overlaps something else, while a region
    // genuinely straddling two windows does not.
    private const double RequiredShare = 2.0 / 3.0;

    /// <param name="Hwnd">The window to capture, or <see cref="IntPtr.Zero"/> when there is none.</param>
    /// <param name="Reason">Why, in a form fit for the log. Always set.</param>
    public readonly record struct Resolution(IntPtr Hwnd, string Reason)
    {
        public bool Resolved => Hwnd != IntPtr.Zero;
    }

    /// <summary>
    /// The one window behind every one of <paramref name="regions"/>. All of them, deliberately: a
    /// session captures one source, so regions that disagree are as unresolved as a single region
    /// that is ambiguous on its own.
    /// </summary>
    public static Resolution Resolve(IReadOnlyList<Rectangle> regions)
    {
        if (regions.Count == 0) return new Resolution(IntPtr.Zero, "no regions");

        var votes = new Dictionary<IntPtr, int>();
        var total = 0;

        foreach (var region in regions)
        {
            foreach (var point in SamplePoints(region))
            {
                total++;
                var hwnd = TopLevelWindowAt(point);
                if (hwnd == IntPtr.Zero) continue;
                votes[hwnd] = votes.GetValueOrDefault(hwnd) + 1;
            }
        }

        if (votes.Count == 0)
            return new Resolution(IntPtr.Zero, "nothing capturable under the framed regions");

        var (winner, count) = votes.MaxBy(vote => vote.Value);
        var share = (double)count / total;

        if (share < RequiredShare)
            return new Resolution(
                IntPtr.Zero,
                $"the framed regions span {votes.Count} windows, none holding more than " +
                $"{share:P0} of {total} sampled points");

        Log.Info(
            "Realtime capture source resolved to hwnd={Hwnd:X} \"{Title}\" class={Class} " +
            "({Count}/{Total} sampled points)",
            winner, DescribeTitle(winner), DescribeClass(winner), count, total);

        return new Resolution(winner, $"{count}/{total} sampled points");
    }

    /// <summary>
    /// Where in a region to ask. The centre, the four quadrant centres and four inset corners: nine
    /// points spread over the rectangle without ever touching its edge, because the edge of a framed
    /// region routinely sits one pixel outside the thing it was drawn around.
    /// </summary>
    private static IEnumerable<Point> SamplePoints(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0) yield break;

        foreach (var fx in new[] { 0.15, 0.5, 0.85 })
        {
            foreach (var fy in new[] { 0.15, 0.5, 0.85 })
            {
                yield return new Point(
                    region.Left + (int)(region.Width * fx),
                    region.Top + (int)(region.Height * fy));
            }
        }
    }

    /// <summary>
    /// The top-level window a screen point belongs to, or zero when that is this process, the
    /// desktop, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <c>WindowFromPoint</c> already skips click-through windows, which is what every one of this
    /// application's overlays is (see <c>WindowStyles.ApplyClickThrough</c>) — so the block layers
    /// standing over the region do not have to be worked around here. The process check stays
    /// anyway: the control bar is not click-through, and a rule this load-bearing should not rest on
    /// a window style set somewhere else for a different reason.
    /// </remarks>
    private static IntPtr TopLevelWindowAt(Point point)
    {
        var hit = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
        if (hit == IntPtr.Zero) return IntPtr.Zero;

        var root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero) return IntPtr.Zero;

        GetWindowThreadProcessId(root, out var pid);
        if (pid == Environment.ProcessId) return IntPtr.Zero;

        // Progman and WorkerW are the desktop — wallpaper and icons. A region drawn there has no
        // application behind it, and capturing the shell would produce a frame with no source.
        var className = DescribeClass(root);
        if (className is "Progman" or "WorkerW") return IntPtr.Zero;

        return root;
    }

    private static string DescribeClass(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : "";
    }

    private static string DescribeTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetWindowText(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : "";
    }

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int capacity);
}
