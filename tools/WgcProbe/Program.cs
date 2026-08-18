using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OverTranslate.Services.Realtime.Capture;

namespace WgcProbe;

/// <summary>
/// Answers, on a real machine, the questions the realtime capture rework cannot be decided without:
/// what this system's Windows.Graphics.Capture can do, whether a window capture is aligned with the
/// screen coordinates the overlays are pinned to, what a readback costs, and — the one that matters
/// — whether OverTranslate's own subtitle layers appear in the captured frame.
/// </summary>
/// <remarks>
/// A console tool rather than a test because none of it can be asserted without a real compositor,
/// a real GPU and a real window on a real monitor. It exists so those answers are measured on each
/// machine that matters rather than assumed from documentation, and so a user on a system where this
/// path misbehaves can be asked to run one command.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // The capture code logs through NLog and this tool has no NLog.config, so without this
        // every diagnosis it writes — a frame it had to drop, the geometry it attached to — goes
        // nowhere, which is the opposite of what a probe is for.
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(
            NLog.LogLevel.Debug, NLog.LogLevel.Fatal,
            new NLog.Targets.ConsoleTarget("console")
            {
                Layout = "  ${level:uppercase=true:padding=-5} ${message} ${exception:format=ToString}"
            });
        NLog.LogManager.Configuration = logging;

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                WgcProbe — measures Windows.Graphics.Capture on this machine.

                  WgcProbe caps
                      Report what capture APIs are available here.

                  WgcProbe list
                      Every visible window with a title, with its handle. Start here when
                      capturing something by name.

                  WgcProbe window <title substring | handle> [outputDir]
                      Capture the first visible window whose title contains the text, or the
                      window with that hex handle, report its geometry against the captured
                      frame, time the readback, and write a PNG.

                  WgcProbe region <x> <y> <w> <h> [outputDir]
                      Resolve the window behind a screen rectangle the way a realtime session
                      would, capture it, and write that rectangle as a PNG.

                  WgcProbe overlay [x y w h] [outputDir]
                      Put up a source window and a stand-in subtitle layer over it, then capture
                      the layer's rectangle both ways and report how much of the layer each
                      capture saw. The go/no-go test: the desktop grab must see it and the
                      window capture must not. Defaults to 200,200 1200x600.

                  WgcProbe exclusion [x y w h] [outputDir]
                      Capture the whole monitor with a stand-in subtitle layer excluded from the
                      frame, and report what the excluded region came back as: the layer still
                      there, black, or the source window underneath it. The go/no-go for
                      capturing the screen without needing the overlays hidden. Defaults to
                      200,200 1200x600.

                  WgcProbe border [x y w h] [outputDir]
                      Put up a source window, start capturing it, and measure whether the system
                      draws its capture indicator around it — the cost window capture adds to
                      every session, and the one this application may not be able to opt out of.
                """);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "caps" => ReportCapabilities(),
                "list" => ListWindows(),
                "window" => CaptureWindow(args),
                "region" => CaptureRegion(args),
                "overlay" => RunOverlayTest(args),
                "exclusion" => RunExclusionTest(args),
                "border" => RunBorderTest(args),
                _ => Fail($"unknown command '{args[0]}'")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int ReportCapabilities()
    {
        Console.WriteLine($"OS                     {Environment.OSVersion.Version}");
        Console.WriteLine($"capture supported      {WgcCapability.IsCaptureSupported}");
        Console.WriteLine($"borderless property    {WgcCapability.CanRequestBorderless}");
        Console.WriteLine($"window exclusion list  {WgcCapability.SupportsWindowExclusion}");
        Console.WriteLine($"display capture session{WgcCapability.SupportsDisplaySession}");
        return WgcCapability.IsCaptureSupported ? 0 : 2;
    }

    /// <summary>Every visible window with a title, for picking one to capture.</summary>
    private static int ListWindows()
    {
        foreach (var hwnd in VisibleWindows())
        {
            var rect = WindowRect(hwnd);
            Console.WriteLine($"{hwnd,10:X}  {rect.Width,5}x{rect.Height,-5} at {rect.X,6},{rect.Y,-6}  {WindowTitle(hwnd)}");
        }

        return 0;
    }

    private static int CaptureWindow(string[] args)
    {
        if (args.Length < 2) return Fail("window needs a title substring or a handle");

        var hwnd = ParseHandle(args[1]) ?? FindWindow(args[1]);
        if (hwnd == IntPtr.Zero) return Fail($"no visible window matching '{args[1]}' — try: WgcProbe list");

        ReportGeometry(hwnd);

        if (!WgcCapability.IsCaptureSupported) return Fail("Windows.Graphics.Capture is not supported here");

        var bounds = WindowRect(hwnd);
        return Capture(() => hwnd, bounds, OutputDirectory(args, 2), $"window-{hwnd:X}");
    }

    private static int CaptureRegion(string[] args)
    {
        if (args.Length < 5) return Fail("region needs x y w h");

        var bounds = new Rectangle(
            int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));

        var resolution = SourceWindowResolver.Resolve([bounds]);
        Console.WriteLine($"resolution             {(resolution.Resolved ? $"hwnd={resolution.Hwnd:X}" : "unresolved")} ({resolution.Reason})");
        if (!resolution.Resolved) return 2;

        ReportGeometry(resolution.Hwnd);

        if (!WgcCapability.IsCaptureSupported) return Fail("Windows.Graphics.Capture is not supported here");

        return Capture(() => resolution.Hwnd, bounds, OutputDirectory(args, 5), $"region-{bounds.X}x{bounds.Y}");
    }

    private static int RunOverlayTest(string[] args)
    {
        if (!WgcCapability.IsCaptureSupported) return Fail("Windows.Graphics.Capture is not supported here");

        return OverlayTest.Run(ProbeBounds(args), OutputDirectory(args, args.Length >= 5 ? 5 : 1));
    }

    private static int RunExclusionTest(string[] args)
    {
        if (!WgcCapability.IsCaptureSupported) return Fail("Windows.Graphics.Capture is not supported here");

        return ExclusionTest.Run(ProbeBounds(args), OutputDirectory(args, args.Length >= 5 ? 5 : 1));
    }

    private static int RunBorderTest(string[] args)
    {
        if (!WgcCapability.IsCaptureSupported) return Fail("Windows.Graphics.Capture is not supported here");

        return BorderTest.Run(ProbeBounds(args), OutputDirectory(args, args.Length >= 5 ? 5 : 1));
    }

    /// <summary>The rectangle for the self-contained tests, given or defaulted.</summary>
    private static Rectangle ProbeBounds(string[] args) =>
        args.Length >= 5
            ? new Rectangle(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]))
            : new Rectangle(200, 200, 1200, 600);

    /// <summary>
    /// The measurement everything else rests on: the frame's own size against the two rectangles
    /// Windows reports for the window, which is what decides where a region is cut from.
    /// </summary>
    private static void ReportGeometry(IntPtr hwnd)
    {
        var window = WindowRect(hwnd);
        var extended = ExtendedFrameBounds(hwnd);

        Console.WriteLine($"hwnd                   {hwnd:X} \"{WindowTitle(hwnd)}\"");
        Console.WriteLine($"GetWindowRect          {window.X},{window.Y} {window.Width}x{window.Height}");
        Console.WriteLine(extended is { } e
            ? $"extended frame bounds  {e.X},{e.Y} {e.Width}x{e.Height}"
            : "extended frame bounds  unavailable");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362.0")]
    private static int Capture(Func<IntPtr> source, Rectangle bounds, string outputDirectory, string name)
    {

        using var backend = WgcWindowCaptureBackend.TryCreate(source);
        if (backend is null) return Fail("could not start window capture");

        var lost = false;
        backend.SourceLost += (_, message) => { lost = true; Console.Error.WriteLine($"source lost: {message}"); };

        // The first frame only arrives once the window's content changes, so the first few polls
        // legitimately come back empty and are not counted against the timing.
        Bitmap? frame = null;
        var waited = Stopwatch.StartNew();
        while (frame is null && waited.ElapsedMilliseconds < 3000 && !lost)
        {
            frame = backend.GrabRegion(bounds);
            if (frame is null) Thread.Sleep(100);
        }

        if (frame is null)
        {
            Console.Error.WriteLine($"backend                {backend.DescribeActivity()}");
            return Fail("no frame arrived within 3s — is the window's content static and never yet drawn?");
        }

        // At the rate a realtime session polls, so the numbers mean what they will mean in use.
        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var sample = backend.GrabRegion(bounds);
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Thread.Sleep(250);
        }

        timings.Sort();
        Console.WriteLine($"grab ms (n={timings.Count})   median={timings[timings.Count / 2]:F1} max={timings[^1]:F1}");
        Console.WriteLine($"backend                {backend.DescribeActivity()}");

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"wgc-{name}-{DateTime.Now:HHmmss}.png");
        frame.Save(path, ImageFormat.Png);
        frame.Dispose();
        Console.WriteLine($"written                {path}");

        return 0;
    }

    private static string OutputDirectory(string[] args, int index) =>
        args.Length > index ? args[index] : Path.Combine(Path.GetTempPath(), "wgcprobe");

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    // ── Window lookup ────────────────────────────────────────────────────────────────────────────

    /// <summary>A window handle given directly, as hex — what <c>list</c> prints.</summary>
    private static IntPtr? ParseHandle(string argument)
    {
        var text = argument.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? argument[2..] : argument;
        return long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? new IntPtr(value)
            : null;
    }

    private static IntPtr FindWindow(string titleSubstring) =>
        VisibleWindows().FirstOrDefault(hwnd =>
            WindowTitle(hwnd).IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>
    /// Every visible top-level window with a title, except the console this is being run from.
    /// </summary>
    /// <remarks>
    /// That exception is not tidiness. A console window's title contains the command line that
    /// started the process, so searching for a window called "Crab Champions" matches the very
    /// terminal the search was typed into — before it ever reaches the game. It then captures the
    /// terminal, reports plausible-looking geometry, and writes a screenshot of itself.
    /// </remarks>
    private static IEnumerable<IntPtr> VisibleWindows()
    {
        var console = GetConsoleWindow();
        var consoleRoot = console == IntPtr.Zero ? IntPtr.Zero : GetAncestor(console, GA_ROOT);

        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (hwnd == console || hwnd == consoleRoot) return true;
            if (WindowTitle(hwnd).Length == 0) return true;

            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetWindowText(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : "";
    }

    private static Rectangle WindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static Rectangle? ExtendedFrameBounds(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<RECT>()) == 0
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const uint GA_ROOT = 2;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int capacity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out RECT value, int size);
}
