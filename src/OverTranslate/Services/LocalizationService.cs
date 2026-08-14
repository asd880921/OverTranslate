using System.Globalization;
using System.Windows;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so this name collides
using Application = System.Windows.Application;

namespace OverTranslate.Services;

/// <param name="Display">
/// The language's name written in itself, and the one string in the app that is never translated.
/// </param>
/// <remarks>
/// Deliberately not a resource key. Someone who has landed in a language they cannot read has to be
/// able to find their way back out, and "繁體中文" is legible from an English interface in a way
/// that "Traditional Chinese" is not from a Chinese one.
/// </remarks>
public record UiLanguageOption(string Code, string Display);

/// <summary>
/// The UI language, swapped exactly the way <see cref="ThemeService"/> swaps colours.
/// </summary>
/// <remarks>
/// Strings live in a merged <see cref="ResourceDictionary"/> and are consumed through
/// DynamicResource, so replacing the dictionary re-resolves every binding in place. That matters
/// here more than it would elsewhere: this app keeps several windows alive at once (the overlay,
/// the capture toolbar, the realtime blocks, the tray menu), and asking the user to restart to
/// change a language would leave all of them stale until they did.
///
/// Code-behind reads the same dictionary through <see cref="Get"/> / <see cref="Format"/>. Those
/// return a snapshot rather than a binding, so a string handed to a control that way is only
/// correct until the next swap — see <see cref="LanguageChanged"/> for how pages refresh it.
/// </remarks>
public static class LocalizationService
{
    public const string TraditionalChinese = "zh-Hant";
    public const string English             = "en";

    private static readonly Uri ZhHantUri = new("Resources/Strings.zh-Hant.xaml", UriKind.Relative);
    private static readonly Uri EnglishUri = new("Resources/Strings.en.xaml",      UriKind.Relative);

    /// <summary>The languages offered in settings, in the order they are listed.</summary>
    public static readonly List<UiLanguageOption> Options =
    [
        new(TraditionalChinese, "繁體中文"),
        new(English,            "English"),
    ];

    /// <summary>
    /// Raised after the dictionary swap, for text that DynamicResource cannot reach.
    /// </summary>
    /// <remarks>
    /// DynamicResource covers everything declared in XAML. It cannot cover a string that was
    /// composed in code — a hint chosen by which provider is selected, a caption with a percentage
    /// in it — because that string was already materialised. Pages holding such text subscribe and
    /// recompose it.
    /// </remarks>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// The language in effect: the stored choice, or the OS default when none was made.
    /// </summary>
    public static string Current
    {
        get
        {
            var stored = SettingsService.Instance.Current.UiLanguage;
            return string.IsNullOrEmpty(stored) ? ResolveSystemDefault() : stored;
        }
    }

    /// <summary>
    /// The language to start a first-run profile in, from the OS UI language.
    /// </summary>
    /// <remarks>
    /// Chinese of any flavour gets Traditional — this app has no Simplified UI, and Traditional is
    /// far closer for a Simplified reader than English is. Everything else gets English.
    /// </remarks>
    public static string ResolveSystemDefault()
    {
        var name = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return name.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? TraditionalChinese
            : English;
    }

    public static void Apply(string language)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;

        var old = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Strings.", StringComparison.OrdinalIgnoreCase) == true);
        if (old != null) dicts.Remove(old);

        dicts.Add(new ResourceDictionary
        {
            Source = language == English ? EnglishUri : ZhHantUri
        });

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Strings for code that runs with no <see cref="Application"/>, loaded on first use.
    /// </summary>
    /// <remarks>
    /// Always Traditional Chinese, which is the language these strings are authored in. Not the
    /// current preference and deliberately not the OS language: this path is reached from unit
    /// tests and from anything running before the UI exists, and a fallback that changed with the
    /// machine's locale would make those results depend on where they ran.
    /// </remarks>
    private static ResourceDictionary? _fallback;

    private static ResourceDictionary? Fallback
    {
        get
        {
            if (_fallback is not null) return _fallback;

            try
            {
                // Touching the helper registers the pack scheme, which Application would otherwise
                // have done on startup — without it the Uri below cannot be resolved.
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                _fallback = new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/OverTranslate;component/Resources/Strings.zh-Hant.xaml",
                        UriKind.Absolute)
                };
            }
            catch
            {
                // Nothing to be done about it here, and Get still has the key to fall back on.
            }

            return _fallback;
        }
    }

    /// <summary>
    /// The string for <paramref name="key"/>, or the key itself when it is missing.
    /// </summary>
    /// <remarks>
    /// Returning the key rather than throwing keeps a typo from taking down a window that was
    /// otherwise fine, and makes the mistake obvious on screen. StringsParityTests is what actually
    /// catches these, before they ship.
    /// </remarks>
    public static string Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is string fromApp) return fromApp;
        if (Fallback?[key] is string fromFallback) return fromFallback;
        return key;
    }

    /// <inheritdoc cref="Get"/>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
