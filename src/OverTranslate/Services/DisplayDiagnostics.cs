using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
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
    //
    // Defaults to Debug, which the shipped configuration drops: repeating the topology on every
    // capture fills the log with copies of what the launch snapshot already said. Pass Info for the
    // one snapshot worth keeping.
    public static void LogSnapshot(string phase, Window? window = null, LogLevel? level = null)
    {
        level ??= LogLevel.Debug;
        if (!Log.IsEnabled(level))
            return;

        // Declared outside the try so a failure part-way through can still report what was gathered
        // before it. The sections are ordered cheapest and most reliable first for the same reason.
        var sb = new StringBuilder();

        try
        {
            sb.AppendLine($"=== Display diagnostics [{phase}] ===");
            AppendOsInfo(sb);
            AppendCaptureCapability(sb);
            AppendProcessInfo(sb);
            AppendVirtualScreenInfo(sb);
            AppendScreens(sb);
            if (window != null)
                AppendWindow(sb, window);
            Log.Log(level, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            // The partial snapshot goes out with the failure rather than being dropped with it. The
            // section most likely to throw is AppendScreens — it enumerates every monitor and
            // marshals a DEVMODE per display — and an unusual display topology is exactly what this
            // snapshot exists to describe, so the machine that cannot finish one is the machine
            // whose first few lines are worth the most.
            Log.Warn(
                ex, "Display diagnostics failed for phase {Phase}; partial snapshot:\n{Partial}",
                phase, sb.ToString().TrimEnd());
        }
    }

    // Which Windows this is, down to the build. Several of the capture path's behaviours are decided
    // by the build number and by nothing else observable from here — whether a capture session has a
    // window exclusion list is one, and it decides whether 完整螢幕 exists on this machine at all.
    // Every other line in this snapshot describes the display topology; this one describes what is
    // interpreting it.
    private static void AppendOsInfo(StringBuilder sb)
    {
        var version = Environment.OSVersion.Version;

        // Windows 11 still reports itself as 10.0, so the build is the only thing separating the
        // two. Derived rather than read from the registry's ProductName, which says "Windows 10"
        // on Windows 11 and would put a plain lie in the log.
        string name = version is { Major: 10, Build: >= 22000 } ? "Windows 11"
            : version.Major == 10 ? "Windows 10"
            : $"Windows {version.Major}.{version.Minor}";

        // The marketing release (24H2, 25H2). Worth having next to the build because that is how
        // both Microsoft's documentation and a user describing their machine refer to it.
        string? release = ReadCurrentVersion("DisplayVersion");

        // OSVersion stops at the build; the update revision after it lives only in the registry, and
        // it is what separates two machines that will otherwise report the same build.
        string build = $"{version.Major}.{version.Minor}.{version.Build}";
        if (ReadCurrentVersion("UBR") is { } ubr) build += $".{ubr}";

        sb.AppendLine(
            $"os      : {name}{(string.IsNullOrEmpty(release) ? "" : $" {release}")} build={build} " +
            $"arch={RuntimeInformation.OSArchitecture} process={RuntimeInformation.ProcessArchitecture}");
    }

    // What Windows.Graphics.Capture will do on this machine, next to the build number that decides
    // it. These are the facts that settle which realtime capture modes exist here at all — a system
    // without the window exclusion list has no 完整螢幕 (#105) and one without capture at all has no
    // realtime translation — and they are the first thing to read in any report about the feature.
    //
    // Recorded here rather than only when a session starts, which is where it used to live. A user
    // who is refused the mode they wanted, or who never reaches 開始翻譯 at all, produced no line
    // saying why; now every log has one whether or not they got that far. Static facts about the
    // machine, so once at launch is enough.
    private static void AppendCaptureCapability(StringBuilder sb) =>
        sb.AppendLine($"capture : {Realtime.Capture.WgcCapability.Describe()}");

    // Null when the value is missing or unreadable. A diagnostic must not be able to break the flow
    // it is observing, and a machine whose registry will not answer is exactly the kind this
    // snapshot is being taken for — so it reports the rest and leaves this field out.
    private static string? ReadCurrentVersion(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue(valueName)?.ToString();
        }
        catch (Exception)
        {
            return null;
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
