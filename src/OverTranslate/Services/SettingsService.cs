using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

public class SettingsService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Velopack installs each version into ...\OverTranslate\current\ and replaces that entire folder
    // on update. Settings used to live in BaseDirectory, i.e. inside current\, so every update wiped
    // them and the app came back up on factory defaults. Roaming AppData sits outside the install.
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverTranslate");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "appsettings.json");

    // Where 1.7.0 and earlier kept the file. Velopack has usually deleted it by the time an updated
    // build runs, but when it survives — a build relaunched in place, a dev run — those are still the
    // user's settings, so read them once on the way to the new location.
    private static readonly string LegacySettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    public AppSettings Current { get; private set; } = new();

    private SettingsService()
    {
        Load();
    }

    public void Load()
    {
        var json = ReadFirstAvailable();
        if (json is null)
        {
            Current = new AppSettings();
            Save();
            return;
        }

        Current = Parse(json);

        // Nothing at the canonical path means what we just read came from the legacy one. Persist it
        // now rather than waiting for the user to happen to change a setting.
        if (!File.Exists(SettingsPath))
            Save();
    }

    /// <summary>
    /// Reads the settings one field at a time so a single bad value cannot cost the user everything
    /// else. A file that is not JSON at all still falls back to defaults, but an individual field
    /// that cannot be read — an enum value a later build dropped, a type that changed, an explicit
    /// null — costs only that field. The previous all-or-nothing catch reset API keys and hotkeys
    /// alike whenever any one value went bad.
    /// </summary>
    public static AppSettings Parse(string json)
    {
        var settings = new AppSettings();

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            Log.Warn(ex, "appsettings.json is not valid JSON; falling back to defaults");
            return settings;
        }

        if (root is null)
            return settings;

        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (!property.CanWrite)
                continue;

            // Absent and explicitly-null both leave the property on its initialiser, which keeps a
            // hand-edited "ApiKey": null from turning into a null string the rest of the app trips on.
            if (!root.TryGetPropertyValue(property.Name, out var node) || node is null)
                continue;

            try
            {
                var value = node.Deserialize(property.PropertyType);
                if (value is not null)
                    property.SetValue(settings, value);
            }
            catch (JsonException ex)
            {
                Log.Warn(ex, "Ignoring unreadable setting '{0}'; keeping its default", property.Name);
            }
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            // Write-then-rename: losing power mid-write leaves the previous file intact instead of a
            // truncated one, which the next launch would read as corrupt and replace with defaults.
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(Current, WriteOptions));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {0}", SettingsPath);
        }
    }

    private static string? ReadFirstAvailable()
    {
        foreach (var path in new[] { SettingsPath, LegacySettingsPath })
        {
            if (!File.Exists(path))
                continue;

            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn(ex, "Could not read settings from {0}", path);
            }
        }

        return null;
    }
}
