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

    /// <summary>
    /// Where the settings actually are, for the one caller that needs the file rather than the
    /// values in it: <see cref="DiagnosticBundleService"/> copies it into a problem report.
    /// </summary>
    public static string FilePath => SettingsPath;

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

        Apply(settings, root, "");
        return settings;
    }

    /// <summary>
    /// Copies one object's worth of JSON onto <paramref name="target"/>, field by field, descending
    /// into grouped sections.
    /// </summary>
    /// <remarks>
    /// The descent is what keeps the promise above once settings are grouped. Handing a whole group
    /// to <c>Deserialize</c> would make it the unit that fails: one unreadable value inside
    /// <see cref="AppSettings.Realtime"/> would throw, and every other value in that group would go
    /// back to its default with it. Read one at a time all the way down, and the blast radius stays
    /// one value however deep it sits.
    /// </remarks>
    /// <param name="path">Where in the file this object sits, for the log to name a field properly.</param>
    private static void Apply(object target, JsonObject source, string path)
    {
        foreach (var property in target.GetType().GetProperties())
        {
            if (!property.CanWrite)
                continue;

            // Absent and explicitly-null both leave the property on its initialiser, which keeps a
            // hand-edited "ApiKey": null from turning into a null string the rest of the app trips on.
            if (!source.TryGetPropertyValue(property.Name, out var node) || node is null)
                continue;

            var name = path + property.Name;

            if (node is JsonObject group && IsSettingsGroup(property.PropertyType))
            {
                // Never null: a group is always initialised by the class that declares it, so there
                // is something to write onto whatever the file says.
                if (property.GetValue(target) is { } child)
                    Apply(child, group, name + ".");
                continue;
            }

            try
            {
                var value = node.Deserialize(property.PropertyType);
                if (value is not null)
                    property.SetValue(target, value);
            }
            catch (JsonException ex)
            {
                Log.Warn(ex, "Ignoring unreadable setting '{0}'; keeping its default", name);
            }
        }
    }

    /// <summary>
    /// Whether a property is a grouped section of the settings rather than a value.
    /// </summary>
    /// <remarks>
    /// Decided by where the type is declared, not by a list to keep in step: anything this
    /// application defines alongside <see cref="AppSettings"/> is a group of settings, and anything
    /// from the framework — string above all, which is a class and would otherwise qualify — is a
    /// value.
    /// </remarks>
    private static bool IsSettingsGroup(Type type) =>
        type is { IsClass: true, IsArray: false }
        && type != typeof(string)
        && type.Namespace == typeof(AppSettings).Namespace;

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
