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

    // 標記 deliberately keeps nothing here. Which pen is in hand lasts exactly as long as the
    // capture it was picked up for: every new capture starts on the black pen at the middle width,
    // and the choice only has to survive closing and reopening the panel inside that one session —
    // see MainWindow's annotation session state.
}
