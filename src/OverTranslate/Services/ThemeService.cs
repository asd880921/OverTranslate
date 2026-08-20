using System.Windows;

namespace OverTranslate.Services;

public static class ThemeService
{
    public const string Light = "Light";
    public const string Dark  = "Dark";

    private static readonly Uri LightUri = new("Themes/LightTheme.xaml", UriKind.Relative);
    private static readonly Uri DarkUri  = new("Themes/DarkTheme.xaml",  UriKind.Relative);

    public static string Current => SettingsService.Instance.Current.Theme;

    /// <summary>
    /// Raised once the new dictionary is in place.
    /// </summary>
    /// <remarks>
    /// For the colours a DynamicResource cannot reach — the shell window's outer edge is drawn by
    /// the desktop compositor from a value handed over once, so nothing repaints it on its own.
    /// </remarks>
    public static event EventHandler? Changed;

    public static void Apply(string theme)
    {
        var dicts = System.Windows.Application.Current.Resources.MergedDictionaries;

        var old = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (old != null) dicts.Remove(old);

        dicts.Insert(0, new ResourceDictionary
        {
            Source = theme == Dark ? DarkUri : LightUri
        });

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
