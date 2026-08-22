using System.Globalization;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// The identifier that says two diagnostic reports came from the same installation.
/// </summary>
/// <remarks>
/// <para>Uploads are anonymous and stay anonymous: this is issued here, from nothing but the clock
/// and a random GUID. Nothing about the machine or the person goes into it, so it cannot be read
/// backwards — the one and only question it answers is whether two reports are the same install
/// talking twice, which is the question that cannot be answered at all without it.</para>
///
/// <para>It lives in appsettings.json under <see cref="AppSettings.ID"/> rather than in a file of
/// its own, and it is deliberately not hidden. A user who deletes or edits it gets a new one on the
/// next launch and nothing else happens; there is no attempt to make it stick, because anyone who
/// wanted a fresh identity could reinstall for one anyway, and a value hidden from the person it
/// describes would sit badly beside a diagnostic bundle whose whole promise is that they can read
/// what they are handing over.</para>
///
/// <para>Read once, at startup, and held here. Not re-read afterwards: a value that changed
/// underneath a running process would put two identities on one session's worth of log, and the
/// only thing to gain would be honouring an edit made while the application was open.</para>
/// </remarks>
public static class AppIdentityService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>The timestamp half, which says when this install first ran.</summary>
    /// <remarks>
    /// Local time, matching the timestamps this application already writes into export filenames.
    /// Across time zones it is out by hours, which changes nothing about the only thing it is read
    /// for: whether a report comes from an install of long standing or one that broke on day one.
    /// </remarks>
    private const string TimestampFormat = "yyyyMMddHHmmss";

    private const int TimestampLength = 14;

    /// <summary>A GUID in "D" format — 32 digits and four hyphens.</summary>
    private const int GuidLength = 36;

    /// <summary>The identifier for this run, or empty before <see cref="Initialize"/> has run.</summary>
    public static string Current { get; private set; } = "";

    /// <summary>
    /// Settles the identifier for this run, issuing and saving a new one when what is on disk is
    /// missing or malformed.
    /// </summary>
    /// <remarks>
    /// Anything that does not parse is replaced rather than repaired or reported. There is nothing
    /// in a broken value worth keeping and nothing the user would do about being told, and the
    /// failure this covers is not sabotage but the ordinary sort: a hand-edited file, a half-written
    /// one, a field this application has not written yet because the install predates it.
    /// </remarks>
    public static void Initialize()
    {
        var settings = SettingsService.Instance.Current;

        if (IsWellFormed(settings.ID))
        {
            Current = settings.ID;
            Log.Info("Install identity {0}", Current);
            return;
        }

        Current = Generate(DateTime.Now);
        settings.ID = Current;
        SettingsService.Instance.Save();

        Log.Info("Install identity issued: {0}", Current);
    }

    /// <summary>Builds an identifier from a moment and a fresh GUID.</summary>
    public static string Generate(DateTime now) =>
        $"{now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid():D}";

    /// <summary>
    /// Whether a stored value is one this class wrote.
    /// </summary>
    /// <remarks>
    /// The date is parsed rather than pattern-matched, so a run of fourteen digits that is not a
    /// date does not survive as one. Both halves have to hold: an identifier is only useful if
    /// every part of it means what it claims to.
    /// </remarks>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != TimestampLength + 1 + GuidLength) return false;
        if (value[TimestampLength] != '-') return false;

        return DateTime.TryParseExact(
                   value[..TimestampLength], TimestampFormat,
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
               && Guid.TryParseExact(value[(TimestampLength + 1)..], "D", out _);
    }
}
