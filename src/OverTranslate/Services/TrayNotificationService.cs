using System.Windows.Forms;

namespace OverTranslate.Services;

/// <summary>
/// Sends notifications through the tray icon, which Windows routes to its own notification centre.
/// <para>
/// The in-app toast exists for things tied to the capture session — it appears beside the selection
/// and disappears with it. Work that finishes while the user is in another window has to reach them
/// there, and only the system's own notifications do that: they survive the app losing focus, stack
/// with everything else the user is being told, and stay in the action centre until dismissed.
/// </para>
/// </summary>
public static class TrayNotificationService
{
    private static NotifyIcon? _icon;

    /// <summary>Called once by the window that owns the tray icon.</summary>
    public static void Attach(NotifyIcon icon) => _icon = icon;

    public static void Show(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        // No tray icon means the app is running without its shell (tests, tooling); silently doing
        // nothing beats taking the caller down over a notification.
        if (_icon is null) return;

        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
    }
}
