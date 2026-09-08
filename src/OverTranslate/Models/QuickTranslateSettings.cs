namespace OverTranslate.Models;

/// <summary>Language preferences used only by quick translation.</summary>
public class QuickTranslateSettings
{
    public string SourceLanguage { get; set; } = LanguageData.DefaultSourceLanguage;
    public string TargetLanguage { get; set; } = "EN-US";
}
