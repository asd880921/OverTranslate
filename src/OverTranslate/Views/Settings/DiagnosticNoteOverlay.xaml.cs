using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using NLog;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Settings;

/// <summary>
/// How the note panel ended: a code if the bundle went up, and whether an attempt was made and
/// failed. Both false-ish means it was dismissed without sending anything.
/// </summary>
public sealed record DiagnosticNoteResult(string? Code, bool AttemptFailed);

/// <summary>
/// Modal panel that collects what the user wants to say about the problem, attaches it to the
/// bundle they have already exported, and sends the two together.
/// </summary>
/// <remarks>
/// The reason this exists at all: a bundle says what the machine was doing and nothing about what
/// the person was trying to do, and the people who would open a GitHub issue to supply the second
/// half are a small fraction of the people who press a button in the app they already have open.
///
/// Drawn on top of the shell rather than in a window of its own, like
/// <see cref="ServiceSettingsOverlay"/>, so the app keeps a single visible surface.
///
/// Nothing here persists. The note is written into the zip at the moment of sending and is not
/// kept anywhere else — dismissing the panel is meant to leave no trace, because what gets typed
/// into it is a description of someone's own machine and their own frustration.
/// </remarks>
public partial class DiagnosticNoteOverlay : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>The bundle this panel is about to send. Set by <see cref="Open"/>.</summary>
    private string _bundlePath = "";

    /// <summary>True from the press until the request settles. Every way out is shut meanwhile.</summary>
    private bool _uploading;

    /// <summary>Set once a send has been tried and did not produce a code.</summary>
    private bool _attemptFailed;

    /// <summary>The code from a successful send, carried out through <see cref="Closed"/>.</summary>
    private string? _code;

    /// <summary>
    /// Which failure is on screen, so it can be re-rendered in the new language. Held as the key
    /// rather than the string for the same reason everything else here is.
    /// </summary>
    private string? _failureKey;

    /// <summary>Raised once the panel has been dismissed, with what became of the bundle.</summary>
    public event EventHandler<DiagnosticNoteResult>? Closed;

    public DiagnosticNoteOverlay()
    {
        InitializeComponent();

        // The service owns the limit: it is the one that has to fit the note into a bundle with an
        // upload ceiling, and a box that accepted more would be promising something it cannot keep.
        NoteBox.MaxLength = DiagnosticBundleService.MaxNoteLength;
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    public void Open(string bundlePath)
    {
        _bundlePath = bundlePath;
        _code = null;
        _attemptFailed = false;

        NoteBox.Text = "";
        BundleNameText.Text = Path.GetFileName(bundlePath);
        HideFailure();
        SetUploading(false);

        Visibility = Visibility.Visible;

        LocalizationService.LanguageChanged += OnLanguageChanged;

        // WPF switches text off pixel snapping while it is being animated and ramps it back over
        // about a second — the card is cached as a bitmap for the length of the scale so the title
        // does not soften and re-sharpen on the way in. Same as the service panel.
        Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) => ReleaseAnimations();
        BeginAnimation(OpacityProperty, fade);

        var grow = new DoubleAnimation
        {
            From = 0.96, To = 1,
            Duration = FadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // The box is what this panel is for, so it is what the caret is in when it appears
        NoteBox.Focus();
    }

    public void Close()
    {
        // The request is on the wire and the page behind this one is waiting to be told how it
        // ended. Every route here is guarded, but they are guarded in one place.
        if (_uploading) return;

        LocalizationService.LanguageChanged -= OnLanguageChanged;

        var result = new DiagnosticNoteResult(_code, _attemptFailed);

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            ReleaseAnimations();

            // Cleared on the way out rather than on the way in: what was typed is a description of
            // someone's own machine, and it has no reason to still be in memory once the panel is
            // gone.
            NoteBox.Text = "";

            Closed?.Invoke(this, result);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // DoubleAnimation holds the properties it animated under the clock's control after it finishes;
    // handing them back drops the composition layer the transition needed.
    private void ReleaseAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;

        Card.CacheMode = null;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside the card must not bubble up to the scrim's dismiss handler
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape only, and no Enter: the box takes multiple lines, and a panel whose one destructive
        // action is on the key that ends a paragraph would send half-written reports.
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Sending ──────────────────────────────────────────────────────────────

    /// <remarks>
    /// The note is written into the bundle rather than sent beside it, so it arrives wherever the
    /// bundle does and in whatever language it was typed in. See
    /// <see cref="DiagnosticBundleService.AttachNote"/>.
    ///
    /// A failure leaves the panel exactly as it was, note included. The bundle is already on disk
    /// either way, so the worst case is that the user closes this and reports it by hand — which is
    /// what the line under the error says, with the link to say it through.
    /// </remarks>
    private async void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_uploading) return;

        HideFailure();
        SetUploading(true);

        var note = NoteBox.Text;
        string? code = null;

        try
        {
            // Off the UI thread: it rewrites a zip that can be several megabytes, and freezing the
            // window while sending a bug report is its own bug report.
            await Task.Run(() => DiagnosticBundleService.AttachNote(_bundlePath, note));

            code = await DiagnosticUploadService.UploadAsync(_bundlePath);
        }
        catch (DiagnosticUploadException ex)
        {
            // One wording for every reason an upload can fail, as everywhere else this is reported:
            // whether it was the network, the size or a refusal, the next move is the same.
            Log.Warn(ex, "Diagnostic upload with note failed");
            _attemptFailed = true;
            ShowFailure("S.Settings.NoteUploadFailed");
        }
        catch (Exception ex)
        {
            // The note could not be written into the bundle, so nothing was sent. Said separately
            // because the fix is different: this one is worth simply trying again.
            Log.Error(ex, "Could not attach the note to the diagnostic bundle");
            _attemptFailed = true;
            ShowFailure("S.Settings.NoteAttachFailed");
        }
        finally
        {
            SetUploading(false);
        }

        if (code is null) return;

        _code = code;
        _attemptFailed = false;
        Close();
    }

    /// <summary>
    /// The panel while the bundle is on its way: nothing can be typed, and every way out is shut
    /// until it is known whether a code came back.
    /// </summary>
    /// <remarks>
    /// The send button keeps its colour and loses only its clicks. Disabling it would dim the one
    /// thing on screen carrying the spinner, and a greyed-out control with a spinner in it reads as
    /// something that has stopped rather than something that is working.
    /// </remarks>
    private void SetUploading(bool uploading)
    {
        _uploading = uploading;

        NoteBox.IsEnabled   = !uploading;
        CancelBtn.IsEnabled = !uploading;
        CloseBtn.IsEnabled  = !uploading;

        SendBtn.IsHitTestVisible = !uploading;
        SendSpinner.Visibility   = uploading ? Visibility.Visible : Visibility.Collapsed;
        SendLabel.SetResourceReference(
            TextBlock.TextProperty,
            uploading ? "S.Settings.NoteSending" : "S.Settings.UploadBundle");
    }

    private void ShowFailure(string key)
    {
        _failureKey = key;
        FailureRun.Text = LocalizationService.Get(key);
        FailurePanel.Visibility = Visibility.Visible;
    }

    private void HideFailure()
    {
        _failureKey = null;
        FailurePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Re-renders the one string this panel sets in code. Everything else is a resource reference
    /// and follows the change on its own.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_failureKey is { } key)
            FailureRun.Text = LocalizationService.Get(key);
    }

    // ── Box ──────────────────────────────────────────────────────────────────

    private void NoteBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NotePlaceholder.Visibility =
            NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A browser that will not open is not a reason to lose the panel the user is standing in
            Log.Warn(ex, "Could not open {0}", e.Uri.AbsoluteUri);
        }

        e.Handled = true;
    }
}
