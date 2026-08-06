using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NLog;
using Screen = System.Windows.Forms.Screen;

namespace OverTranslate.Services;

// Diagnostic-only. Dumps every coordinate space the capture pipeline mixes, so a multi-monitor
// misalignment report can be diagnosed from a log file instead of reproduced.
//
// The capture flow reads screen geometry from two sources that only agree when every monitor runs
// at the same effective scale: MainWindow captures from Screen.AllScreens (pixels as this process
// sees them) while ScreenCaptureWindow positions itself from SystemParameters.VirtualScreen*
// (DIP). ScreenshotImage uses Stretch="Fill", so any disagreement silently becomes a scaled and
// offset image rather than a visible failure.
//
// The decisive cross-check here is EnumDisplaySettings: it reports true physical display modes
// regardless of the process's DPI awareness, so comparing it against Screen.Bounds reveals the
// virtualization factor Windows is applying — which is invisible to every managed API we use.
internal static class DisplayDiagnostics
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int ENUM_CURRENT_SETTINGS = -1;

    private const int MDT_EFFECTIVE_DPI = 0;
    private const int MDT_RAW_DPI       = 1;

    private const int MONITOR_DEFAULTTONEAREST = 2;

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // Writes one multi-line snapshot of the display topology. `phase` labels the call site so
    // several snapshots in one session can be told apart. Never throws: a diagnostic must not be
    // able to break the flow it is observing.
    public static void LogSnapshot(string phase, Window? window = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Display diagnostics [{phase}] ===");
            AppendProcessInfo(sb);
            AppendVirtualScreenInfo(sb);
            AppendScreens(sb);
            if (window != null)
                AppendWindow(sb, window);
            Log.Debug(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Display diagnostics failed for phase {Phase}", phase);
        }
    }

    private static void AppendProcessInfo(StringBuilder sb)
    {
        string awareness = "unavailable";
        try
        {
            IntPtr context = GetDpiAwarenessContextForProcess(GetCurrentProcess());
            awareness = DescribeAwareness(GetAwarenessFromDpiAwarenessContext(context));

            // DPI_AWARENESS collapses both per-monitor contexts onto one value, so V2 — the only one
            // that scales non-client area and forwards DPI changes to child windows — is invisible
            // unless the context itself is compared.
            if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                awareness += "_V2";
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1803. The shcore call goes back to 8.1 but cannot tell V1 from V2 per-monitor.
            if (GetProcessDpiAwareness(IntPtr.Zero, out int value) == 0)
                awareness = DescribeAwareness(value) + " (shcore)";
        }
        catch (DllNotFoundException) { }

        uint systemDpi = TryGetDpiForSystem();
        sb.AppendLine($"process : dpiAwareness={awareness} systemDpi={systemDpi} ({Scale(systemDpi)})");
    }

    private static void AppendVirtualScreenInfo(StringBuilder sb)
    {
        // WPF's DIP view of the virtual desktop — what the overlay windows are sized from.
        sb.AppendLine(
            "virtual (WPF DIP)   : " +
            $"left={SystemParameters.VirtualScreenLeft} top={SystemParameters.VirtualScreenTop} " +
            $"w={SystemParameters.VirtualScreenWidth} h={SystemParameters.VirtualScreenHeight}");

        // The same rectangle in the pixel space this process is allowed to see. Equal to the DIP
        // values above whenever WPF believes the primary monitor is at 96 DPI.
        sb.AppendLine(
            "virtual (SM_ pixels): " +
            $"left={GetSystemMetrics(SM_XVIRTUALSCREEN)} top={GetSystemMetrics(SM_YVIRTUALSCREEN)} " +
            $"w={GetSystemMetrics(SM_CXVIRTUALSCREEN)} h={GetSystemMetrics(SM_CYVIRTUALSCREEN)}");
    }

    private static void AppendScreens(StringBuilder sb)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var b = screen.Bounds;
            sb.AppendLine(
                $"screen  : {screen.DeviceName}{(screen.Primary ? " [PRIMARY]" : "")} " +
                $"bounds={b.Left},{b.Top} {b.Width}x{b.Height} " +
                $"work={screen.WorkingArea.Width}x{screen.WorkingArea.Height}");

            AppendMonitorDpi(sb, b.Left + b.Width / 2, b.Top + b.Height / 2);
            AppendRealDisplayMode(sb, screen.DeviceName, b.Width, b.Height);
        }
    }

    private static void AppendMonitorDpi(StringBuilder sb, int centerX, int centerY)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = centerX, Y = centerY }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint effX, out uint effY) != 0)
                return;
            GetDpiForMonitor(monitor, MDT_RAW_DPI, out uint rawX, out uint rawY);
            sb.AppendLine($"          dpi effective={effX}x{effY} ({Scale(effX)}) raw={rawX}x{rawY}");
        }
        catch (DllNotFoundException) { /* shcore missing — pre-8.1 */ }
    }

    // True physical mode and desktop position, unaffected by this process's DPI awareness. If
    // realWidth differs from the Screen.Bounds width logged above, Windows is virtualizing our
    // coordinates and the capture/overlay geometry cannot line up.
    private static void AppendRealDisplayMode(StringBuilder sb, string deviceName, int boundsWidth, int boundsHeight)
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
        {
            sb.AppendLine("          real mode unavailable");
            return;
        }

        double ratioX = boundsWidth  > 0 ? (double)dm.dmPelsWidth  / boundsWidth  : 0;
        double ratioY = boundsHeight > 0 ? (double)dm.dmPelsHeight / boundsHeight : 0;
        sb.AppendLine(
            $"          real pos={dm.dmPositionX},{dm.dmPositionY} " +
            $"mode={dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}Hz " +
            $"real/reported={ratioX:0.###}x{ratioY:0.###}" +
            (Math.Abs(ratioX - 1) > 0.001 || Math.Abs(ratioY - 1) > 0.001 ? "  <-- MISMATCH" : ""));
    }

    private static void AppendWindow(StringBuilder sb, Window window)
    {
        sb.AppendLine(
            $"window  : {window.GetType().Name} wpf={window.Left},{window.Top} " +
            $"{window.Width}x{window.Height} actual={window.ActualWidth}x{window.ActualHeight}");

        if (PresentationSource.FromVisual(window)?.CompositionTarget is CompositionTarget target)
        {
            Matrix m = target.TransformToDevice;
            sb.AppendLine($"          transformToDevice={m.M11:0.###}x{m.M22:0.###}");
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // The only number that says where the window physically landed. Compared against the
        // capture bounds, this is what proves or disproves an offset.
        if (GetWindowRect(hwnd, out RECT r))
        {
            sb.AppendLine(
                $"          hwndRect={r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}");
        }

        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            sb.AppendLine($"          windowDpi={dpi} ({Scale(dpi)})");
        }
        catch (EntryPointNotFoundException) { /* pre-1607 */ }
    }

    private static uint TryGetDpiForSystem()
    {
        try { return GetDpiForSystem(); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private static string Scale(uint dpi) => dpi == 0 ? "?" : $"{dpi * 100 / 96}%";

    private static string DescribeAwareness(int value) => value switch
    {
        -1 => "INVALID",
        0  => "UNAWARE",
        1  => "SYSTEM_AWARE",
        2  => "PER_MONITOR_AWARE",
        _  => $"UNKNOWN({value})",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint   dmFields;
        public int    dmPositionX;
        public int    dmPositionY;
        public uint   dmDisplayOrientation;
        public uint   dmDisplayFixedOutput;
        public short  dmColor;
        public short  dmDuplex;
        public short  dmYResolution;
        public short  dmTTOption;
        public short  dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint   dmBitsPerPel;
        public uint   dmPelsWidth;
        public uint   dmPelsHeight;
        public uint   dmDisplayFlags;
        public uint   dmDisplayFrequency;
        public uint   dmICMMethod;
        public uint   dmICMIntent;
        public uint   dmMediaType;
        public uint   dmDitherType;
        public uint   dmReserved1;
        public uint   dmReserved2;
        public uint   dmPanningWidth;
        public uint   dmPanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDpiAwarenessContextForProcess(IntPtr hprocess);

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr dpiContextA, IntPtr dpiContextB);

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(IntPtr hprocess, out int value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
