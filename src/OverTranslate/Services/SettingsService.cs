using System.IO;
using System.Text.Json;
using OverTranslate.Models;

namespace OverTranslate.Services;

public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    public AppSettings Current { get; private set; } = new();

    private SettingsService()
    {
        Load();
    }

    public void Load()
    {
        if (!File.Exists(SettingsPath))
        {
            Current = new AppSettings();
            Save();
            return;
        }
        try
        {
            var json = File.ReadAllText(SettingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, options));
    }
}
