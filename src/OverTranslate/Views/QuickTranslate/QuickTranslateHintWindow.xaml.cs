using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NLog;
using OverTranslate.Layout;
using OverTranslate.Services;
using Clipboard = System.Windows.Clipboard;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverTranslate.Views.QuickTranslate;

/// <summary>
/// The one thing 快速翻譯 puts on the screen: a line saying what is happening to the text the user
/// just selected.
/// </summary>
/// <remarks>
/// The feature itself is invisible — a shortcut goes in, somebody else's document changes — and
/// without this the user would press a key and watch nothing happen for as long as a translation
/// takes. So the card exists to answer three questions in the order they occur to them: is it
/// working, did it work, and if not, why not.
///
/// It appears without an entrance. Something is already in flight by the time it is shown and the
/// user is waiting on it, so a card that grows or slides in spends that time on the announcement
/// rather than on the answer. It leaves with a fade, which is the opposite case: nothing is waiting
/// on it, and a card that vanished between two frames would leave the reader unsure what they saw.
///
/// One at a time. A second shortcut press replaces the card outright rather than stacking another on
/// top of it — see <see cref="Show"/> — because the newer one is the only one whose outcome the user
/// is still waiting for.
///
/// It never takes activation. The paste that replaces the selection is sent to whatever window has
/// the foreground, so a hint that took the foreground would be pasting the translation into itself.
/// </remarks>
public partial class QuickTranslateHintWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <inheritdoc cref="QuickTranslateHintWindow"/>
    private static QuickTranslateHintWindow? _current;

    /// <summary>
    /// How long a success stays up.
    /// </summary>
    /// <remarks>
    /// Long enough to be seen and no longer: the answer to "did it work" is already in the document
    /// underneath, where the user is looking, and this is only the part that says the change came
    /// from the key they pressed.
    /// </remarks>
    private static readonly TimeSpan SuccessHold = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// How long a failure stays up.
    /// </summary>
    /// <remarks>
    /// A failure has to be read rather than glanced at, and unlike a success nothing else on the
    /// screen carries it — the document is simply unchanged, which is also what a shortcut that
    /// missed looks like.
    /// </remarks>
    private static readonly TimeSpan FailureHold = TimeSpan.FromMilliseconds(2500);

    private const int FadeMs = 260;

    /// <summary>Shorter than the timed fade: the reader has already decided.</summary>
    private const int CloseFadeMs = 120;

    /// <summary>
    /// How far the card sits from the selection, and the closest it comes to a screen edge.
    /// </summary>
    /// <remarks>
    /// Small because it is not the whole distance: the window carries a transparent margin around
    /// the card for its shadow to fall into, so what the reader sees between the two is this plus
    /// that margin.
    /// </remarks>
    private const double Gap = 6;

    /// <summary>How long the copy button says it copied before going back to being a button.</summary>
    private static readonly TimeSpan CopiedHold = TimeSpan.FromMilliseconds(1400);

    private const string CopyGlyph = "";
    private const string DoneGlyph = "";
    private const string FailedGlyph = "";

    /// <summary>Where the selection was, in physical pixels, or null when nothing would say.</summary>
    private readonly Rect? _anchor;

    /// <summary>
    /// Where the pointer was when the card was put up, which is the fallback anchor.
    /// </summary>
    /// <remarks>
    /// Taken once rather than read again on every placement: the card is placed a second time when
    /// its state changes, and a pointer that has moved since would take the card with it — away from
    /// the text the reader pressed the shortcut over.
    /// </remarks>
    private readonly System.Drawing.Point _pointer;

    private DispatcherTimer? _hold;
    private DispatcherTimer? _copiedHold;

    /// <summary>What the copy button puts on the clipboard, or empty while there is nothing to copy.</summary>
    private string _copyText = "";

    /// <summary>True while this card is reporting a failure, which is the only state that waits.</summary>
    private bool _holdsForPointer;

    private bool _closed;
    private bool _fadingOut;

    /// <summary>Set once the reader has dismissed it, so hovering stops reviving it.</summary>
    private bool _userDismissed;

    /// <summary>
    /// Puts a card up in its working state, replacing whichever one is on screen.
    /// </summary>
    /// <param name="anchor">
    /// Where the selection is in physical pixels, or null to sit by the pointer instead.
    /// </param>
    public static QuickTranslateHintWindow Show(Rect? anchor)
    {
        Dismiss();

        var hint = new QuickTranslateHintWindow(anchor);
        _current = hint;
        ((Window)hint).Show();
        return hint;
    }

    /// <summary>Takes the card off the screen, if one is up.</summary>
    /// <remarks>
    /// For when the card cannot go on existing — a realtime session taking the screen, a newer press
    /// of the shortcut — rather than when it has merely outstayed its welcome.
    /// </remarks>
    public static void Dismiss()
    {
        var existing = _current;
        _current = null;
        existing?.Close();
    }

    // Private on purpose: constructing one directly and calling the inherited Window.Show() would
    // bypass the single-card bookkeeping and leave two of them on the same spot.
    private QuickTranslateHintWindow(Rect? anchor)
    {
        InitializeComponent();
        _anchor = anchor;
        _pointer = System.Windows.Forms.Cursor.Position;

        MessageText.Text = LocalizationService.Get("S.Translation.Translating");
        RenderCopyAvailability();

        Closed += (_, _) =>
        {
            _closed = true;
            _hold?.Stop();
            _copiedHold?.Stop();
            if (ReferenceEquals(_current, this)) _current = null;
        };

        // Only a failure waits for a pointer resting on it. A success is gone in a second by design,
        // and it is placed against the text the user just acted on — which is where their pointer
        // already is, so holding on hover would leave it up until they moved the mouse.
        MouseEnter += (_, _) => { if (_holdsForPointer) PauseHold(); };
        MouseLeave += (_, _) => { if (_holdsForPointer) ResumeHold(); };

        // Parked off-screen until Loaded can measure it, and invisible until the position is
        // settled: the first placement can still be moved by WPF resizing the window on a DPI
        // change, and watching the card travel to its final spot reads as a glitch.
        Left = -9999;
        Top = -9999;
        Opacity = 0;

        Loaded += (_, _) =>
        {
            Position();
            Dispatcher.BeginInvoke(
                new Action(() => { Position(); Opacity = 1; }), DispatcherPriority.Loaded);
        };

        // Every state change resizes the card, and it has to stay against the same text afterwards.
        SizeChanged += (_, _) => { if (IsLoaded && !_fadingOut) Position(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowStyles.ApplyNoActivate(this);
    }

    // ══════════════════════════ What the card says ══════════════════════════

    /// <summary>Reports that the selection now holds <paramref name="translation"/>.</summary>
    public void ReportSuccess(string translation)
    {
        if (_closed) return;

        WorkingBar.Visibility = Visibility.Collapsed;
        ShowGlyph(DoneGlyph, "AppSuccess");
        MessageText.Text = LocalizationService.Get("S.QuickTranslate.Replaced");

        // The translation rather than the line above it: the words are what someone reaching for the
        // copy button wants, and they have just been pasted somewhere that may not have taken them.
        _copyText = translation;
        RenderCopyAvailability();

        _holdsForPointer = false;
        StartHold(SuccessHold);
    }

    /// <summary>Reports that the selection was left as it was, and why.</summary>
    public void ReportFailure(string message)
    {
        if (_closed) return;

        WorkingBar.Visibility = Visibility.Collapsed;
        ShowGlyph(FailedGlyph, "AppError");
        MessageText.Text = message;

        _copyText = message;
        RenderCopyAvailability();

        _holdsForPointer = true;
        StartHold(FailureHold);
    }

    // Resource reference rather than a resolved brush, so the mark follows a live theme switch.
    private void ShowGlyph(string glyph, string brushKey)
    {
        StatusGlyph.Text = glyph;
        StatusGlyph.SetResourceReference(ForegroundProperty, brushKey);
        StatusGlyph.Visibility = Visibility.Visible;
    }

    /// <remarks>
    /// Disabled rather than hidden while there is nothing to copy: showing it later would change the
    /// card's width under a pointer already on its way to the close button beside it.
    /// </remarks>
    private void RenderCopyAvailability() => CopyBtn.IsEnabled = _copyText.Length > 0;

    // ══════════════════════════ Placement ══════════════════════════

    /// <remarks>
    /// All physical pixels, scaled by the monitor being placed on. Reading the scale off this window
    /// instead reports the monitor it currently sits on — which until this runs is the one holding
    /// the off-screen parking position, not the one it is headed for.
    /// </remarks>
    private void Position()
    {
        // The monitor the card is going to, which is the selection's when there is one: it and the
        // pointer are not always on the same screen.
        var origin = _anchor is { } selection
            ? new System.Drawing.Point(
                (int)Math.Round(selection.Left + selection.Width / 2),
                (int)Math.Round(selection.Top))
            : _pointer;

        var area = System.Windows.Forms.Screen.FromPoint(origin).WorkingArea;
        var scale = ScreenGeometry.ScaleAt(origin.X, origin.Y);

        var (left, top) = HintPlacement.Place(
            _anchor,
            new Point(_pointer.X, _pointer.Y),
            new Size(ActualWidth * scale, ActualHeight * scale),
            new Rect(area.Left, area.Top, area.Width, area.Height),
            Gap * scale);

        ScreenGeometry.MoveToPhysical(this, left, top);
    }

    // ══════════════════════════ Going away ══════════════════════════

    // A timer rather than an awaited delay, because the countdown has to be stoppable on hover.
    private void StartHold(TimeSpan duration)
    {
        _hold?.Stop();
        _hold = new DispatcherTimer { Interval = duration };
        _hold.Tick += (_, _) =>
        {
            _hold!.Stop();
            FadeOutAndClose();
        };
        _hold.Start();

        // A pointer already resting on the card when it changed state gets no MouseEnter of its own,
        // so a failure it is sitting on would count down under it.
        if (_holdsForPointer && IsMouseOver) PauseHold();
    }

    private void PauseHold()
    {
        if (_closed || _userDismissed) return;

        _hold?.Stop();

        // Cancels a fade already in flight. The flag is what actually calls it off: removing the
        // animation does not reliably suppress its Completed handler, so without this the card would
        // close under a pointer that is resting on it to read.
        _fadingOut = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    /// <remarks>
    /// The full countdown again rather than what was left of it: the pointer leaving means the
    /// reader just finished, and giving them the leftover 200ms of a spent timer reads as a glitch.
    /// </remarks>
    private void ResumeHold()
    {
        if (_closed || _userDismissed) return;
        _hold?.Start();
    }

    private void FadeOutAndClose(int durationMs = FadeMs)
    {
        if (_closed) return;

        // Windows' "animation effects" setting is the local equivalent of a reduced-motion
        // preference; a fade is motion the user has asked not to be shown.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        _fadingOut = true;
        var fade = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(durationMs));
        fade.Completed += (_, _) => { if (_fadingOut) Close(); };
        BeginAnimation(OpacityProperty, fade);
    }

    // ══════════════════════════ The two buttons ══════════════════════════

    /// <remarks>
    /// The clipboard belongs to whatever else is running, and any of it can hold the clipboard open
    /// long enough for this to fail — WPF already retries for about a second before throwing. Left
    /// unhandled that throw reaches the dispatcher and takes the whole application down, over a copy
    /// button on a card that was about to dismiss itself anyway.
    /// </remarks>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_copyText.Length == 0) return;

        try
        {
            Clipboard.SetText(_copyText);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "快速翻譯 could not copy the hint's text to the clipboard");
            return;
        }

        // Confirmed on the button that was pressed: there is no room on a card this size for a line
        // saying what happened, and the button is where the reader is already looking.
        CopyBtn.Content = DoneGlyph;
        PauseHold();

        _copiedHold?.Stop();
        _copiedHold = new DispatcherTimer { Interval = CopiedHold };
        _copiedHold.Tick += (_, _) =>
        {
            _copiedHold!.Stop();
            CopyBtn.Content = CopyGlyph;
            if (!IsMouseOver) ResumeHold();
        };
        _copiedHold.Start();
    }

    // Hovering has already stopped the countdown on a failure, so without this the only way out of a
    // card the reader is done with is to move the pointer away and wait.
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _userDismissed = true;
        _hold?.Stop();
        FadeOutAndClose(CloseFadeMs);
    }
}
