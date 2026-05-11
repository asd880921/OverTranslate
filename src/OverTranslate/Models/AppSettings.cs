using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProvider { Google, Google2, Bing, Yandex, DeepL }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrEngineType { WindowsOcr, Tesseract }

public class AppSettings
{
    public uint HotkeyModifiers { get; set; } = 3;
    public uint HotkeyVirtualKey { get; set; } = 0x41;
    public string HotkeyDisplay { get; set; } = "Ctrl+Alt+A";
    public string SourceLanguage { get; set; } = "EN";
    public string TargetLanguage { get; set; } = "ZH-HANT";
    public TranslationProvider Provider { get; set; } = TranslationProvider.Google2;
    public string ApiKey { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    public OcrEngineType OcrEngine { get; set; } = OcrEngineType.Tesseract;
}
