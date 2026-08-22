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
}
