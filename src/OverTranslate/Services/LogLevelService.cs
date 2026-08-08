using NLog;
using NLog.Config;

namespace OverTranslate.Services;

/// <summary>
/// Switches the app's own logging between Info and Debug at runtime, so a user reporting a problem
/// can turn detail on from the settings page instead of being talked through an environment
/// variable or a config file edit.
/// </summary>
/// <remarks>
/// Debug is not the shipped default and should not become one: that level carries the recognised
/// text, which is whatever happened to be on the user's screen. It is opt-in, off by default, and
/// the hint beside the checkbox says what it is for.
/// </remarks>
public static class LogLevelService
{
    /// <summary>
    /// The escape hatch NLog.config documents. Someone who set it wants that level for the whole
    /// run — including the startup lines written before settings are read — so it stays the
    /// authority and the checkbox becomes a no-op while it is present.
    /// </summary>
    private const string OverrideVariable = "OVERTRANSLATE_LOGLEVEL";

    /// <summary>Rules for the app's own loggers. Everything else stays at Warn either way.</summary>
    private const string AppLoggerPrefix = "OverTranslate";

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static bool _verbose;
    private static bool _hooked;
    private static bool _applying;

    public static bool IsOverriddenByEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OverrideVariable));

    public static void Apply(bool verbose)
    {
        _verbose = verbose;

        // autoReload is on, so NLog may replace the configuration under us — reloading it would
        // otherwise silently restore the file's Info and leave the checkbox lying.
        if (!_hooked)
        {
            LogManager.ConfigurationChanged += (_, _) => { if (!_applying) ApplyToConfiguration(); };
            _hooked = true;
        }

        ApplyToConfiguration();
    }

    private static void ApplyToConfiguration()
    {
        if (IsOverriddenByEnvironment) return;

        var config = LogManager.Configuration;
        if (config is null) return;

        var minLevel = _verbose ? LogLevel.Debug : LogLevel.Info;

        _applying = true;
        try
        {
            foreach (LoggingRule rule in config.LoggingRules)
            {
                if (rule.LoggerNamePattern?.StartsWith(AppLoggerPrefix, StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                rule.SetLoggingLevels(minLevel, LogLevel.Fatal);
            }

            LogManager.ReconfigExistingLoggers();
        }
        catch (Exception ex)
        {
            // Logging its own failure to the log it just failed to configure is the best available
            // record, and a level that would not take is never worth bringing the app down for.
            Log.Warn(ex, "Could not apply log level {0}", minLevel);
        }
        finally
        {
            _applying = false;
        }
    }
}
