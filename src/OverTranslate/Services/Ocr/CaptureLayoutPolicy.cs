namespace OverTranslate.Services.Ocr;

/// <summary>
/// The shipped capture UI currently exposes one mode. Keep this at the application boundary:
/// settings can retain Interface, and harnesses can still exercise both grouping/placement paths.
/// </summary>
internal static class CaptureLayoutPolicy
{
    public static bool IsModeSelectionAvailable => false;

    public static CaptureLayoutMode ForApplication(CaptureLayoutMode requested) =>
        IsModeSelectionAvailable ? requested : CaptureLayoutMode.General;
}
