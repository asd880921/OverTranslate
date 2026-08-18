using System.Runtime.Versioning;
using Windows.Foundation.Metadata;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// What Windows.Graphics.Capture can actually do on the machine this is running on, asked of the
/// system rather than inferred from its build number.
/// </summary>
/// <remarks>
/// The build number is not the answer to any of these questions. Capture is refused outright on
/// systems whose graphics stack cannot serve it regardless of version; the window exclusion list is
/// new enough that being on a build that shipped it is no promise that this projection can reach it;
/// and turning off the capture border needs a consent this application's deployment model may not be
/// able to ask for. Each is asked separately, and the answers go in the log — the first thing any
/// report about this feature needs is which capture path the user was actually on.
///
/// Nothing here throws. An unavailable API is a fact about the system, not a failure.
/// </remarks>
public static class WgcCapability
{
    private const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string DisplaySessionType = "Windows.Graphics.Capture.IDisplayGraphicsCaptureSession";

    /// <summary>Capture at all: the 1903 API surface, and a system willing to serve it.</summary>
    /// <remarks>
    /// The version check is here as well as inside, and it is not redundant: it is what lets the
    /// platform analyser see that nothing newer than 1809 is reached on a system that only has 1809.
    /// </remarks>
    [SupportedOSPlatformGuard("windows10.0.18362.0")]
    public static bool IsCaptureSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) && WgcInterop.IsCaptureSupported();

    /// <summary>
    /// Whether the yellow capture indicator around a captured window can be turned off. Present from
    /// Windows 11 21H2 — and present is not permitted: doing it also needs
    /// <c>GraphicsCaptureAccess.RequestAccessAsync(Borderless)</c>, which is granted against a
    /// package identity that an unpackaged Velopack build does not have.
    /// </summary>
    public static bool CanRequestBorderless => IsApiPresent(() =>
        ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired"));

    /// <summary>
    /// Whether a capture session can be told to leave specific windows out of its frames — the 2026
    /// API that lets a monitor capture exclude OverTranslate's own overlays, and so keeps the "frame
    /// any part of the screen" interface with none of the WDA problem. What
    /// <see cref="WgcMonitorCaptureBackend"/> is built on, and the reason it is offered only on some
    /// machines.
    /// </summary>
    /// <remarks>
    /// Asked of both places it could live. The documentation puts it on the display-capture session,
    /// while the SDK release notes graduate <c>IGraphicsCaptureSession7</c> — which the projection
    /// would fold into <see cref="Windows.Graphics.Capture.GraphicsCaptureSession"/> — so neither on
    /// its own is a safe question.
    /// </remarks>
    public static bool SupportsWindowExclusion => IsApiPresent(() =>
        ApiInformation.IsMethodPresent(SessionType, "SetWindowExclusionList")
        || ApiInformation.IsMethodPresent(DisplaySessionType, "SetWindowExclusionList"));

    /// <summary>
    /// Whether the display-capture session interface the exclusion list is documented against exists
    /// here. Tracked separately from <see cref="SupportsWindowExclusion"/> because the two shipped at
    /// different times and under different stability promises.
    /// </summary>
    public static bool SupportsDisplaySession => IsApiPresent(() =>
        ApiInformation.IsTypePresent(DisplaySessionType));

    /// <summary>One line for the log, listing every answer above.</summary>
    public static string Describe() =>
        $"os={Environment.OSVersion.Version} capture={IsCaptureSupported} " +
        $"borderless={CanRequestBorderless} windowExclusion={SupportsWindowExclusion} " +
        $"displaySession={SupportsDisplaySession}";

    private static bool IsApiPresent(Func<bool> ask)
    {
        try
        {
            // ApiInformation itself is 1607 and older than anything supported here, but the metadata
            // lookup behind it can fail on a system whose projection is incomplete.
            return ask();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
