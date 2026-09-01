namespace OverTranslate.Models;

/// <summary>
/// Everything 截圖翻譯 keeps between capture sessions, under one key.
/// </summary>
public class CaptureSettings
{
    /// <summary>
    /// Whether the capture toolbar last translated text written downwards in columns. False means
    /// the ordinary horizontal layout and remains the default for existing settings files.
    /// </summary>
    public bool VerticalText { get; set; } = false;

    /// <summary>
    /// The pen 標記 was last using: which tool, what colour, and where the 粗細 slider sat.
    /// </summary>
    /// <remarks>
    /// Remembered for the same reason <see cref="VerticalText"/> is. Someone marking up captures is
    /// nearly always working through one thing — a page, a screen, a game — and being handed the
    /// orange pen again on the next capture is being handed back the tool they put down. It is a
    /// setting, not accumulated data: three values that the next press of a swatch overwrites.
    ///
    /// The colour is a string because a settings file is read by people as well as by the app, and
    /// #F97316 says something a packed integer does not. Anything unparseable, or a colour no longer
    /// on the palette, falls back to the first swatch — see AnnotationPanelWindow.BuildPalette.
    /// </remarks>
    public AnnotationTool AnnotationTool { get; set; } = AnnotationTool.Pen;

    public string AnnotationColor { get; set; } = "#F97316";

    /// <summary>Where the 粗細 slider sat, 0 to 1. What that width is depends on the tool.</summary>
    public double AnnotationThickness { get; set; } = 0.3;
}
