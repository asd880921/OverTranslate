using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NLog;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Shell;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace OverTranslate.Views.QuickLookup;

/// <summary>
/// 取詞翻譯: one line of text translated in place, over whatever the user was reading.
/// </summary>
/// <remarks>
/// The lightest of the three translation surfaces, and the only one with no way in but a shortcut.
/// It exists for the case the other two are too heavy for — a word in a sentence someone is halfway
/// through — so everything here is arranged around not making them leave what they were doing: the
/// selection is carried in for them, the popup lands where their pointer already is, and it goes
/// away the moment they turn back to what they were doing.
///
/// One at a time, deliberately. Several of these would each be waiting to dismiss themselves on top
/// of somebody's work, and only one window at a time can be the one the user is attending to —
/// which is the whole of what decides whether this one is still wanted.
///
/// It reads the same source language, target language and translation service as 截圖翻譯 and
/// 文字翻譯, so a change made here is a change everywhere. See <see cref="QuickLookupSettings"/> for
/// what it does keep to itself.
/// </remarks>
public partial class QuickLookupWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <inheritdoc cref="QuickLookupWindow"/>
    private static QuickLookupWindow? _current;

    /// <inheritdoc cref="Views.Translation.TranslationPage"/>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private const int EnterMs = 220;
    private const int ExitMs  = 110;
    private const int BodyMs  = 150;

    /// <summary>
    /// How often Windows is asked which window is actually in front.
    /// </summary>
    /// <remarks>
    /// Short enough that a dismissal this catches rather than <see cref="OnDeactivated"/> still
    /// reads as immediate, and cheap enough to leave running: the check is one call returning a
    /// handle, on a window that lives for seconds.
    /// </remarks>
    private static readonly TimeSpan ForegroundWatchInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>How long 複製 says it copied before going back to offering to.</summary>
    private static readonly TimeSpan CopiedHold = TimeSpan.FromMilliseconds(1400);

    private readonly TtsService _tts = new();
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _copiedHold;
    private readonly DispatcherTimer _foregroundWatch;

    /// <summary>This window's handle, for comparing against whatever Windows says is in front.</summary>
    private IntPtr _hwnd;

    /// <summary>
    /// True once this window has actually been the foreground window at least once.
    /// </summary>
    /// <remarks>
    /// The latch is what makes <see cref="CheckForeground"/> mean "it was in front and now it is
    /// not" rather than "it is not in front", which at the moment of opening is briefly true of
    /// every window and would close this one before it had been seen.
    ///
    /// It also carries the one case that cannot be fixed from here: if Windows never hands over the
    /// foreground at all, this stays false and the popup simply waits. Clicking it sets the latch
    /// the ordinary way, and from then on it behaves like any other summon.
    /// </remarks>
    private bool _hadForeground;

    /// <inheritdoc cref="TakeForeground"/>
    private int _foregroundAttempts;

    /// <summary>True while the popup is pinned, which is what keeps it through a deactivation.</summary>
    /// <remarks>
    /// Pinning answers "keep this one on screen while I work". Everything it holds against is the
    /// popup acting on its own — closing itself when attention moves, moving itself to the pointer
    /// on the next summon — and nothing it holds against is the user: a pinned popup still drags,
    /// and the close button still closes it.
    ///
    /// Per window and stored nowhere, because it is a statement about the popup in front of the
    /// user right now. What sets it on the way in is which door was used, not what was set last
    /// time — see <see cref="SummonAsync"/>.
    /// </remarks>
    private bool _pinned;

    /// <summary>True while the gear panel is showing instead of the result.</summary>
    private bool _settingsOpen;

    /// <summary>True while one of the pickers has its list down — see <see cref="OnDeactivated"/>.</summary>
    private bool _dropDownOpen;

    /// <summary>True while the window is writing to its own controls, so that does not auto-translate.</summary>
    private bool _suppressAuto;

    /// <summary>True once the closing animation has started, so nothing restarts it.</summary>
    private bool _closing;

    /// <summary>Monotonic id, so a slow translation cannot overwrite the result of a newer one.</summary>
    private int _seq;

    /// <summary>
    /// The language the engine said the original was in, or empty.
    /// </summary>
    /// <remarks>
    /// This is what makes 朗讀原文 usable at all. The shared source language is 自動 by default and
    /// most people never change it, and there is no such thing as an automatic voice — 文字翻譯
    /// answers that by switching its source speaker off. Here the engine has already been asked, and
    /// its answer is a better one than the picker can give.
    /// </remarks>
    private string _detectedLang = "";

    /// <summary>The button currently driving playback, so a second click stops rather than replays.</summary>
    private Button? _ttsActiveBtn;

    /// <summary>
    /// Where the popup belongs when nothing is holding it up, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// Physical pixels, because that is what <see cref="KeepBodyOnScreen"/> works in and what the
    /// window was placed with. Null is the ordinary state and means "where it is now is where it
    /// belongs"; anything that means the user has chosen a new place — a drag, a re-placement at the
    /// pointer — sets it back to null rather than sending the popup somewhere they left.
    /// </remarks>
    private int? _restingTop;

    /// <summary>
    /// Brings the popup up over the foreground application, carrying whatever is selected there.
    /// </summary>
    /// <remarks>
    /// The selection is read before anything is shown: putting a window on the screen takes the
    /// foreground away from the application holding it, and the copy would then be sent here.
    ///
    /// An already-open popup is refilled rather than replaced, so pressing the shortcut twice does
    /// not throw away a pin or a position the user has set.
    /// </remarks>
    /// <param name="pinned">
    /// Whether to open the popup already pinned, which is how the two doors differ.
    /// <para>
    /// The shortcut is used mid-sentence, on a word, and the popup that answers it is meant to be
    /// glanced at and gone — dismissing itself is the whole of why it costs nothing to press. The
    /// entry on the main window is the opposite errand: the user went looking for the feature and
    /// clicked it, and a window that disappeared while they were still turning back to their text
    /// would look broken to someone who has not yet met the shortcut.
    /// </para>
    /// <para>
    /// It only ever turns the pin on. Off is the user's word, and a shortcut press landing on a
    /// popup they pinned must not quietly take that back.
    /// </para>
    /// </param>
    public static async Task SummonAsync(bool pinned = false)
    {
        var selection = await SelectedTextReader.ReadAsync();

        if (_current is { _closing: false } open)
        {
            open.Refill(selection);
            if (pinned) open.SetPinned(true);
            open.ReacquireForeground();
            return;
        }

        var window = new QuickLookupWindow();
        _current = window;

        // Before Show: showing a window can hand activation straight back, and OnDeactivated closes
        // an unpinned popup without asking how old it is.
        if (pinned) window.SetPinned(true);

        window.Show();
        window.ReacquireForeground();
        window.Refill(selection);
    }

    /// <summary>Takes the foreground, starting the retry budget over.</summary>
    /// <remarks>
    /// Reset per summon rather than per window. A popup that is already open has the latch set from
    /// last time, so a re-summon that failed to take the foreground back would look to
    /// <see cref="CheckForeground"/> exactly like the user clicking away — and close the popup they
    /// had just asked for.
    /// </remarks>
    private void ReacquireForeground()
    {
        _hadForeground = false;
        _foregroundAttempts = 0;
        TakeForeground();
    }

    /// <summary>
    /// Makes this window the one the keyboard is talking to.
    /// </summary>
    /// <remarks>
    /// <c>Show</c> and <c>Activate</c> are not enough, and the failure is silent: Windows refuses to
    /// hand the foreground to a process the user was not already working in, so the popup appears on
    /// top — it is topmost — while every keystroke goes on reaching the application underneath. The
    /// box then draws a focus ring it does not have and typing does nothing, which is the whole
    /// feature broken for anyone who summoned it with nothing selected.
    ///
    /// Attaching this thread's input queue to the foreground one for the length of the call is what
    /// lifts the refusal: while two threads share an input queue, either of them may set the
    /// foreground. Detached again immediately — a permanent attachment couples this application's
    /// message pump to a stranger's, so an application that hangs would take this one with it.
    ///
    /// Even that is not certain to work, which is why it can be asked again. The popup is summoned
    /// moments after this application synthesised a Ctrl+C into the window the user was reading —
    /// see <see cref="SelectedTextReader"/> — and one of the things Windows weighs when deciding
    /// whether to refuse is which process received the last input event. <see cref="CheckForeground"/>
    /// is what notices the refusal and asks again; the attempt count is what stops it asking forever,
    /// because every attempt flashes the taskbar button of whoever is holding the foreground.
    /// </remarks>
    private void TakeForeground()
    {
        var hwnd = Hwnd();
        if (hwnd == IntPtr.Zero) return;

        _foregroundAttempts++;

        var foreground = GetForegroundWindow();
        var owner = GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var attached = owner != 0 && owner != self && AttachThreadInput(self, owner, true);

        try
        {
            SetForegroundWindow(hwnd);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(self, owner, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    /// <summary>Takes the popup off the screen, if one is up.</summary>
    /// <remarks>
    /// Ignores the pin: this is called when the popup cannot go on existing — a realtime session
    /// taking the screen — rather than when something merely thinks it has outstayed its welcome.
    /// </remarks>
    public static void Dismiss() => _current?.BeginClose();

    private QuickLookupWindow()
    {
        InitializeComponent();

        BrandIcon.Source = AppIconService.CreateWindowIcon();

        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TranslateNowAsync(); };

        _copiedHold = new DispatcherTimer { Interval = CopiedHold };
        _copiedHold.Tick += (_, _) => { _copiedHold.Stop(); RenderCopyLabel(copied: false); };

        _foregroundWatch = new DispatcherTimer { Interval = ForegroundWatchInterval };
        _foregroundWatch.Tick += (_, _) => CheckForeground();

        _tts.StateChanged += OnTtsStateChanged;

        _suppressAuto = true;
        LocalizationService.BindLocalizedItems(SrcLangBox, LanguageData.SourceLanguages);
        LocalizationService.BindLocalizedItems(TgtLangBox, LanguageData.TargetLanguages);
        LocalizationService.BindLocalizedItems(ProviderBox, LanguageData.Providers);
        LoadSharedPreferences();
        _suppressAuto = false;

        // Attached after the initial values are in, so setting them up neither saves nor translates.
        SrcLangBox.SelectionChanged  += LangBox_SelectionChanged;
        TgtLangBox.SelectionChanged  += LangBox_SelectionChanged;
        ProviderBox.SelectionChanged += ProviderBox_SelectionChanged;

        foreach (var picker in new[] { SrcLangBox, TgtLangBox, ProviderBox })
        {
            picker.DropDownOpened += (_, _) => _dropDownOpen = true;
            picker.DropDownClosed += (_, _) =>
            {
                _dropDownOpen = false;

                // The deactivation a click outside the list produced arrived while the list was
                // still down, and OnDeactivated skipped it for exactly that reason. Nothing else
                // would ever ask again, so the popup would outlive the click that dismissed it.
                if (!IsActive && !_pinned) BeginClose();
            };
        }

        // Every change of height, not just the result opening: the settings panel is a different
        // height again, and a long enough original wraps the box onto a second line. All of them
        // grow out of the bottom, so all of them can run off it.
        SizeChanged += (_, e) => { if (e.HeightChanged) KeepBodyOnScreen(); };

        MouseEnter += OnPointerEnter;
        MouseLeave += OnPointerLeave;
        PreviewKeyDown += OnPreviewKeyDown;
        Surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;

        // Composed in code from the shortcut and the interface language, so DynamicResource cannot
        // reach them — see LocalizationService.LanguageChanged.
        LocalizationService.LanguageChanged += OnLanguageChanged;

        Loaded += (_, _) =>
        {
            PositionAtPointer();
            AnimateIn();
            _foregroundWatch.Start();
        };

        RenderChrome();
        RenderCopyLabel(copied: false);
        RenderSettingsButton();
        RenderSettingsHint();
    }

    /// <summary>
    /// Puts a new selection into an open popup.
    /// </summary>
    /// <remarks>
    /// A pinned popup keeps its place: the user put it there, and the point of pinning is that it
    /// stops behaving like a thing that follows the pointer around.
    ///
    /// The box takes keyboard focus either way, with the caret after the last character rather than
    /// the text selected: a selection is one keystroke away from being replaced wholesale, and the
    /// text it would destroy is the text the user asked about.
    /// </remarks>
    private void Refill(string selection)
    {
        if (!_pinned && IsLoaded) PositionAtPointer();

        ShowSettings(false);

        _suppressAuto = true;
        SourceTextBox.Text = selection;
        SourceTextBox.CaretIndex = selection.Length;
        _suppressAuto = false;

        _detectedLang = "";
        _seq++;

        if (selection.Length == 0) ShowBody(false);
        else RequestTranslate();

        // After Focus, which selects the whole box on its own when focus arrives programmatically.
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;

        RenderChrome();
    }

    // ══════════════════════════ Placement and motion ══════════════════════════

    /// <summary>
    /// Drops the popup around the pointer, inside the monitor the pointer is on.
    /// </summary>
    /// <remarks>
    /// All physical pixels and the scale of the monitor being placed on, exactly as the toast does:
    /// reading the scale off this window reports the monitor it currently sits on, which before the
    /// first placement is whichever one WPF happened to open it on.
    ///
    /// The pointer ends up just inside the popup's top-left rather than beside it, so that reaching
    /// any of the controls is a small movement from where the hand already is. The offsets put it
    /// over the corner the brand mark occupies, which is the one part of the header nobody clicks.
    ///
    /// Nothing about the placement is remembered between summons. This window goes where the pointer
    /// already is, so a stored position would be a worse answer to the same question every time.
    /// Dragging one pins it, and a pinned popup is not re-placed — see <see cref="Refill"/>.
    /// </remarks>
    private void PositionAtPointer()
    {
        // A fresh placement is a new resting place, and the old one is not somewhere to go back to.
        _restingTop = null;

        var pointer = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(pointer).WorkingArea;
        var scale = ScreenGeometry.ScaleAt(pointer.X, pointer.Y);

        var w = ActualWidth * scale;
        var h = ActualHeight * scale;
        var edge = 4 * scale;

        // Math.Clamp throws when the popup is larger than the monitor it has to fit on.
        var minX = area.Left + edge;
        var maxX = Math.Max(minX, area.Right - w - edge);
        var minY = area.Top + edge;
        var maxY = Math.Max(minY, area.Bottom - h - edge);

        // Measured from the window, which is wider and taller than the card by the shadow margin —
        // so these are the card's own inset plus that margin, and they move together with it.
        var x = Math.Clamp(pointer.X - 48 * scale, minX, maxX);
        var y = Math.Clamp(pointer.Y - 30 * scale, minY, maxY);

        ScreenGeometry.MoveToPhysical(this, (int)Math.Round(x), (int)Math.Round(y));

        // Anchored to the pointer rather than to a corner: the popup should look like it came out of
        // the place it was asked for, and go back into it.
        Surface.RenderTransformOrigin = new Point(
            Math.Clamp((pointer.X - x) / Math.Max(w, 1), 0, 1),
            Math.Clamp((pointer.Y - y) / Math.Max(h, 1), 0, 1));
    }

    /// <summary>
    /// Lifts the popup off the bottom edge while the result is open, and puts it back after.
    /// </summary>
    /// <remarks>
    /// The header is the whole window until a translation arrives, and the result grows out of the
    /// bottom of it — <c>SizeToContent="Height"</c>, so the top stays put and the window gets taller.
    /// A popup summoned or dragged near the foot of the screen therefore grows the answer straight
    /// off the desktop: the user typed a word, something happened, and there is nothing to read.
    ///
    /// The arithmetic is <see cref="QuickLookupLift"/>'s, which is where it can be tested; this is
    /// the half that has to ask Windows where the window and the monitor actually are.
    ///
    /// Physical pixels throughout, for the reason <see cref="PositionAtPointer"/> gives.
    /// </remarks>
    private void KeepBodyOnScreen()
    {
        if (_closing) return;

        var bounds = ScreenGeometry.PhysicalBounds(this);
        if (bounds.IsEmpty) return;

        var area = System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea;
        var edge = (int)Math.Round(4 * ScreenGeometry.ScaleAt(bounds.Left, bounds.Top));

        var (top, resting) = QuickLookupLift.Place(
            bounds.Top, _restingTop, bounds.Height, area.Top, area.Bottom, edge);

        _restingTop = resting;

        if (top != bounds.Top) ScreenGeometry.MoveToPhysical(this, bounds.Left, top);
    }

    /// <remarks>
    /// Blur and scale together rather than a plain fade, so the surface reads as arriving rather
    /// than as being turned up — the shadow and the border are already drawn, and only the geometry
    /// is short of its resting value.
    ///
    /// No overshoot. Nothing threw this window: it appeared because a key was pressed, and a bounce
    /// belongs to motion that inherited momentum from a gesture.
    /// </remarks>
    private void AnimateIn()
    {
        // Windows' "animation effects" setting is this platform's reduced-motion preference.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(EnterMs - 80)),
        });

        var grow = new DoubleAnimation
        {
            From = 0.94, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(EnterMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <remarks>
    /// The same path in reverse, ending where it started. Something that leaves along a different
    /// route than it arrived by reads as a second, unrelated thing happening.
    /// </remarks>
    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;

        _debounce.Stop();
        _foregroundWatch.Stop();
        _tts.Stop();

        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var shrink = new DoubleAnimation
        {
            To = 0.96,
            Duration = new Duration(TimeSpan.FromMilliseconds(ExitMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(ExitMs)),
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Slides whichever panel is now current up into place.</summary>
    private void ShowBody(bool visible)
    {
        if (!visible)
        {
            BodyHost.Visibility = Visibility.Collapsed;
            return;
        }

        var wasVisible = BodyHost.Visibility == Visibility.Visible;
        BodyHost.Visibility = Visibility.Visible;
        if (wasVisible || !SystemParameters.ClientAreaAnimation) return;

        BodyHost.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(BodyMs)),
        });
        BodyTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 6, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(BodyMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ══════════════════════════ Staying and going ══════════════════════════

    // The pointer only decides how the header draws itself; what decides whether this window still
    // exists is OnDeactivated.
    private void OnPointerEnter(object sender, MouseEventArgs e) => RenderChrome();

    private void OnPointerLeave(object sender, MouseEventArgs e) => RenderChrome();

    /// <summary>
    /// Closes the popup as soon as the user's attention goes back to something else.
    /// </summary>
    /// <remarks>
    /// Losing activation is the whole dismissal rule, and it is a better one than a timer watching
    /// the pointer: a pointer that has wandered off the window says nothing about whether the person
    /// is still reading it, while clicking into another window is them saying they are finished, at
    /// the moment they finish. Nothing has to be guessed and nothing has to be waited out.
    ///
    /// It also means the popup never closes while it is the window being used — typing in it, waiting
    /// for a translation, listening to one — so none of those needs a rule of its own.
    ///
    /// Two exceptions. A pinned popup is one the user has said to keep regardless, which is what
    /// pinning is for. And a picker with its list down has not lost anybody's attention: the list is
    /// its own window, and answering that deactivation would close the popup out from under the
    /// language someone is in the middle of choosing.
    /// </remarks>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_pinned || _dropDownOpen) return;
        BeginClose();
    }

    /// <summary>
    /// Asks Windows which window is in front, because <see cref="OnDeactivated"/> cannot always say.
    /// </summary>
    /// <remarks>
    /// WPF raises Deactivated for a window it believes was activated, and the popup is sometimes
    /// never activated at all: it is summoned right after a synthesised Ctrl+C has gone to the
    /// window the user was reading, and Windows can refuse to hand the foreground over on that
    /// basis. The popup then sits on top — it is topmost either way — attached to nothing, and no
    /// amount of clicking elsewhere produces the deactivation that would close it. Clicking the
    /// popup itself was the only way out, which is a window the user has to know a trick to dismiss.
    ///
    /// The foreground handle is the fact underneath the state WPF is inferring, so this asks for it
    /// directly. It answers both halves: a refusal is retried while it is still worth retrying, and
    /// a foreground that has moved on closes the popup whether or not WPF noticed.
    ///
    /// The guards are <see cref="OnDeactivated"/>'s, for the same reasons.
    /// </remarks>
    private void CheckForeground()
    {
        if (_closing) return;

        if (GetForegroundWindow() == Hwnd())
        {
            _hadForeground = true;
            return;
        }

        if (!_hadForeground)
        {
            // Two, not one: the first is the one Show made, and by the next tick whatever was
            // holding the foreground has usually finished handling the keystrokes we sent it.
            if (_foregroundAttempts < 3) TakeForeground();
            return;
        }

        if (_pinned || _dropDownOpen) return;
        BeginClose();
    }

    private IntPtr Hwnd() => _hwnd != IntPtr.Zero
        ? _hwnd
        : _hwnd = new WindowInteropHelper(this).Handle;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            BeginClose();
            return;
        }

        if (e.Key is not (Key.Enter or Key.Return)) return;

        // Enter translates what is in the box now, rather than waiting out the debounce.
        e.Handled = true;
        _debounce.Stop();
        _ = TranslateNowAsync();
    }

    // ══════════════════════════ Moving and pinning ══════════════════════════

    /// <summary>Lets the popup be dragged by any part of it that is not a control.</summary>
    /// <remarks>
    /// Bubbling rather than tunnelling, and that is the whole of it: a press that landed on the box
    /// or on a button has been handled by that control and never arrives here, so dragging cannot
    /// steal a click meant for something. Reached from a tunnelling handler instead, this used to
    /// run for every press on the window — and <c>DragMove</c> then inherited the press's own mouse
    /// capture and never gave it back. A window holding the capture swallows every click on the
    /// desktop after it, which cost the user both the click they aimed at another window and the
    /// deactivation that is the only thing that closes this one.
    ///
    /// Dragging does not pin. It used to, on the reasoning that placing a window somewhere says you
    /// want it to stay there — but the pin is on the header at all times now, so the guess buys
    /// nothing, and a window that pins itself is one the user has to notice and undo.
    ///
    /// Pinning does not stop dragging either, which it briefly did. A pinned popup is the one the
    /// user is going to keep for a while, so it is the one they most need to move out of the way of
    /// what they are reading — locking it in place took that away from exactly the case that wanted
    /// it. What the pin holds against is the popup moving on its own, which is a different thing
    /// from the user moving it: see <see cref="Refill"/>.
    /// </remarks>
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws when the button is already up by the time it runs, which a fast click
            // can manage. There is nothing to move and nothing to report.
        }

        // Wherever they let go is the resting place now. Keeping the old one would send the popup
        // back to somewhere they had already moved it away from, the next time the result closed.
        _restingTop = null;
        KeepBodyOnScreen();
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    /// <summary>Sets the pin and redraws the header, which is the only thing that reports it.</summary>
    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        RenderChrome();
    }

    /// <summary>
    /// Settles the header against where the pointer is, whether it is pinned, and whether the box
    /// has anything in it.
    /// </summary>
    /// <remarks>
    /// The pin is always on the header. It was faded in on hover while dragging was the main way to
    /// pin, so the button only had to be there for whoever went looking; now that it is the only way
    /// to pin, a control that is not on screen is a feature nobody finds.
    ///
    /// Which glyph shows is the action rather than the state, the way every other toggle in this
    /// application draws itself; the accent is what says which state it is in.
    /// </remarks>
    private void RenderChrome()
    {
        // Which glyph shows is the action, not the state — the way every other toggle in this
        // application draws itself. Segoe MDL2 codepoints so they survive Windows 10, where Segoe
        // Fluent Icons is not installed; see Views/Controls/TtsGlyphs.
        PinBtn.Content = _pinned ? "\uE77A" : "\uE718";
        PinBtn.ToolTip = LocalizationService.Get(
            _pinned ? "S.QuickLookup.Unpin" : "S.QuickLookup.Pin");
        PinBtn.SetResourceReference(
            ForegroundProperty, _pinned ? "AppAccent" : "AppTextSecondary");

        var showField = IsMouseOver || SourceTextBox.IsKeyboardFocusWithin;
        SourceTextBox.SetResourceReference(
            BackgroundProperty, showField ? "AppInputBg" : "AppSurfaceBg");
        SourceTextBox.SetResourceReference(
            BorderBrushProperty, showField ? "AppInputBorder" : "AppSurfaceBg");

        var empty = SourceTextBox.Text.Length == 0;
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // On the text alone, not on the pointer, unlike everything else this method settles. The
        // controls above are chrome the user goes looking for; this one is the answer to a box that
        // already has the wrong words in it, and it has to be there when they look down at it.
        ClearBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <remarks>
    /// Cleared through the selection rather than by assigning Text, so it lands on the TextBox's own
    /// undo stack and Ctrl+Z puts the text back — the same reasoning as 文字翻譯's clear button, and
    /// it applies harder here: the text this destroys is usually a selection the user carried in
    /// from somewhere else, and retyping it means going back for it.
    /// </remarks>
    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        SourceTextBox.SelectAll();
        SourceTextBox.SelectedText = "";

        // They cleared it in order to type something else, and the button they clicked has just
        // gone — without this they would have to click into the box before typing.
        SourceTextBox.Focus();
    }

    // ══════════════════════════ Translating ══════════════════════════

    private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderChrome();
        RequestTranslate();
    }

    private void RequestTranslate()
    {
        if (_suppressAuto) return;

        _debounce.Stop();
        if (string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            _seq++;
            _detectedLang = "";
            ShowBody(_settingsOpen);
            return;
        }

        _debounce.Start();
    }

    /// <remarks>
    /// Hedged and with fallbacks, unlike 文字翻譯, which sends to the chosen engine alone. The two
    /// windows are answering different questions: there, a failure is worth reporting because the
    /// user is sitting in a window they opened to translate in and can retry. Here the popup has
    /// about a second of the user's attention and no retry button worth the room, so a free endpoint
    /// having a bad minute should cost a moment rather than the answer.
    /// </remarks>
    private async Task TranslateNowAsync()
    {
        var text = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var settings = SettingsService.Instance.Current;
        var srcLang = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        var tgtLang = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);

        if (AppServices.Translation.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowStatus(LocalizationService.Get("S.Translation.MissingApiKey"), isError: true);
            return;
        }

        var seq = ++_seq;
        ShowStatus(LocalizationService.Get("S.Translation.Translating"), isError: false);

        try
        {
            var (results, detected) = await AppServices.Translation.TranslateAsync(
                [new OcrTextBlock(text, new Rect())], srcLang, tgtLang, settings.ApiKey);

            if (seq != _seq) return;

            _detectedLang = detected ?? "";
            TranslatedText.Text = results.FirstOrDefault()?.TranslatedText ?? "";
            StatusText.Visibility = Visibility.Collapsed;
            ShowResult();
        }
        catch (Exception ex)
        {
            if (seq != _seq) return;

            Log.Warn(ex, "取詞翻譯 could not translate");
            TranslatedText.Text = "";
            ShowStatus(
                LocalizationService.Format(
                    "S.Translation.ProviderUnavailable",
                    LanguageData.GetProviderDisplay(settings.Provider), ex.Message),
                isError: true);
        }
    }

    /// <remarks>
    /// Silent while the gear panel is up. A translation finishing is not a reason to take the user
    /// out of the settings they opened; <see cref="ShowSettings"/> brings the result back when they
    /// are done, by which point it is already there.
    /// </remarks>
    private void ShowResult()
    {
        if (_settingsOpen) return;

        ResultPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        // Both belong to a translation rather than to the panel, so neither is on screen while the
        // status line is still saying 翻譯中.
        var hasResult = TranslatedText.Text.Length > 0;
        ActionRow.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;
        TgtTtsBtn.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;

        // After ActionRow, which the note's own visibility is measured against.
        RenderSourceTtsAvailability();
        ShowBody(true);
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(
            ForegroundProperty, isError ? "AppError" : "AppAccent");
        StatusText.Visibility = Visibility.Visible;
        ShowResult();
    }

    /// <summary>
    /// What language the text in the box is actually in, or empty when nobody knows yet.
    /// </summary>
    /// <remarks>
    /// The picker's own value answers this except when it says 自動, which is its default and the
    /// setting most people never touch — so on its own it is unknown most of the time. The engine's
    /// answer is what fills that in, and the two callers are the two places where 自動 is not an
    /// answer they can use: 朗讀原文 has to pick a voice, and <see cref="SwapBtn_Click"/> has to put
    /// something on the target side.
    /// </remarks>
    /// <inheritdoc cref="_detectedLang"/>
    private string EffectiveSourceLanguage()
    {
        var chosen = SrcLangBox.SelectedValue as string;
        if (!LanguageData.IsAutomaticSource(chosen)) return LanguageData.GetValidSourceCode(chosen);
        return _detectedLang;
    }

    // ══════════════════════════ Reading aloud ══════════════════════════

    /// <summary>
    /// Settles 朗讀原文 against whether there is a language to read the original in.
    /// </summary>
    /// <remarks>
    /// The OpenAI case is called out on its own because it is the one that never resolves. Every
    /// other engine answers "what language was that" as a by-product of translating, so 自動 is a
    /// state the next result gets the popup out of; an OpenAI-compatible server is never asked, and
    /// cannot be — the prompt it is sent is the user's to write, so the reply cannot be made to
    /// carry a second field. Waiting there is not going to help, and telling someone to wait for
    /// something that will not arrive is worse than telling them nothing.
    ///
    /// So that state gets the note under the actions and a matching tooltip, both of which name the
    /// thing that does fix it: choose a source language. The other unresolved state — an engine that
    /// simply has not answered yet — keeps the message that says to wait, which there is true.
    /// </remarks>
    private void RenderSourceTtsAvailability()
    {
        var openAiAutomatic =
            SettingsService.Instance.Current.Provider == TranslationProvider.OpenAI &&
            LanguageData.IsAutomaticSource(SrcLangBox.SelectedValue as string);

        var available = !openAiAutomatic &&
                        EffectiveSourceLanguage().Length > 0 &&
                        SourceTextBox.Text.Length > 0;

        SrcTtsBtn.IsEnabled = available;
        SrcTtsBtn.ToolTip = LocalizationService.Get(
            available ? "S.QuickLookup.SpeakSourceTip"
            : openAiAutomatic ? "S.QuickLookup.SpeakSourceOpenAi"
            : "S.QuickLookup.SpeakSourceUnknown");

        // Beside the actions it explains, so it is absent while they are.
        SourceTtsNote.Visibility = openAiAutomatic && ActionRow.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void SrcTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(SrcTtsBtn, SourceTextBox.Text, EffectiveSourceLanguage());

    private async void TgtTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(
            TgtTtsBtn,
            TranslatedText.Text,
            LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string));

    /// <inheritdoc cref="Views.Translation.TranslationPage"/>
    private async Task ToggleTtsAsync(Button button, string text, string language)
    {
        if (_tts.IsActive && ReferenceEquals(_ttsActiveBtn, button)) { _tts.Stop(); return; }
        if (string.IsNullOrWhiteSpace(text) || language.Length == 0) return;

        _ttsActiveBtn = button;
        RenderTtsGlyphs();
        try
        {
            await _tts.SpeakAsync(text, language);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "取詞翻譯 could not read the text aloud");
            ShowStatus(LocalizationService.Format("S.Translation.SpeakFailed", ex.Message), isError: true);
        }
    }

    private void OnTtsStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tts.IsActive) return;
            _ttsActiveBtn = null;
            RenderTtsGlyphs();
        }));

    private void RenderTtsGlyphs()
    {
        TgtTtsBtn.Content = ReferenceEquals(_ttsActiveBtn, TgtTtsBtn)
            ? Controls.TtsGlyphs.Stop
            : Controls.TtsGlyphs.Speak;

        SrcTtsGlyph.Text = ReferenceEquals(_ttsActiveBtn, SrcTtsBtn)
            ? Controls.TtsGlyphs.Stop
            : Controls.TtsGlyphs.Speak;
    }

    // ══════════════════════════ Copying ══════════════════════════

    /// <remarks>
    /// Confirmed on the button itself rather than with a toast. A toast would appear outside this
    /// window, which is a place the pointer then has to not be for the popup to survive — and the
    /// message would outlive the window it was about.
    /// </remarks>
    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TranslatedText.Text.Length == 0) return;

        try
        {
            Clipboard.SetText(TranslatedText.Text);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not copy the translation");
            return;
        }

        RenderCopyLabel(copied: true);
        _copiedHold.Stop();
        _copiedHold.Start();
    }

    private void RenderCopyLabel(bool copied) =>
        CopyLabel.Text = LocalizationService.Get(
            copied ? "S.QuickLookup.Copied" : "S.QuickLookup.Copy");

    // ══════════════════════════ Settings panel ══════════════════════════

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => ShowSettings(!_settingsOpen);

    /// <summary>
    /// Points the one button in the header that means two things at whichever one it means now.
    /// </summary>
    /// <remarks>
    /// The settings replace the result in place, in the same small window, so this button is the
    /// only way back — and a gear that stayed a gear left that unmarked: the panel read as somewhere
    /// the popup had gone rather than somewhere it could come back from.
    ///
    /// Which glyph shows is the action rather than the state, the way the pin beside it and every
    /// speak button in the application already draw themselves. Segoe MDL2 codepoints, so they
    /// survive Windows 10; see Views/Controls/TtsGlyphs.
    /// </remarks>
    private void RenderSettingsButton()
    {
        SettingsBtn.Content = _settingsOpen ? "\uE72B" : "\uE713";
        SettingsBtn.ToolTip = LocalizationService.Get(
            _settingsOpen ? "S.QuickLookup.BackToResult" : "S.QuickLookup.Settings");
    }

    /// <remarks>
    /// In place rather than in a window of its own: a settings window over a popup that disappears
    /// when the pointer leaves it would be a window whose owner can vanish underneath it.
    /// </remarks>
    private void ShowSettings(bool open)
    {
        _settingsOpen = open;
        RenderSettingsButton();

        if (open)
        {
            _suppressAuto = true;
            LoadSharedPreferences();
            _suppressAuto = false;

            RenderSettingsHint();
            SettingsPanel.Visibility = Visibility.Visible;
            ResultPanel.Visibility = Visibility.Collapsed;
            ShowBody(true);
            return;
        }

        SettingsPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ShowBody(TranslatedText.Text.Length > 0 || StatusText.Visibility == Visibility.Visible);
    }

    private void RenderSettingsHint() =>
        SettingsHint.Text = LocalizationService.Format(
            "S.QuickLookup.SettingsHint",
            SettingsService.Instance.Current.QuickLookupHotkeyDisplay);

    private void OpenFullSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        // The shell takes the foreground, which would close this window a moment later anyway —
        // doing it first means the popup does not flash behind the window that replaced it.
        BeginClose();
        ShellWindow.ShowOrActivate(ShellPage.Settings);
    }

    // ══════════════════════════ Shared preferences ══════════════════════════

    private void LoadSharedPreferences()
    {
        var settings = SettingsService.Instance.Current;

        SrcLangBox.SelectedValue = LanguageData.GetValidSourceCode(settings.SourceLanguage);
        TgtLangBox.SelectedValue = LanguageData.GetValidTargetCode(settings.TargetLanguage);
        ProviderBox.SelectedValue = settings.Provider;
        if (ProviderBox.SelectedValue is null) ProviderBox.SelectedIndex = 0;
    }

    private void LangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuto) return;
        ApplyLanguagePair();
    }

    /// <summary>Saves the pair as the shared preference and re-translates under it.</summary>
    /// <remarks>
    /// Its own method because <see cref="SwapBtn_Click"/> moves both pickers and must not do this
    /// twice: reading either half while the other has already moved would save a pair the user never
    /// chose, and send a translation off under it.
    /// </remarks>
    private void ApplyLanguagePair()
    {
        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        settings.TargetLanguage = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        SettingsService.Instance.Save();

        // The engine's answer belongs to the language pair it was asked about.
        _detectedLang = "";
        RenderSourceTtsAvailability();
        RequestTranslate();
    }

    /// <summary>
    /// Turns the pair around, carrying the translation back into the box as the new original.
    /// </summary>
    /// <remarks>
    /// The languages alone would not be a swap here, which is where this parts company with
    /// 截圖翻譯's toolbar button. There the source text is the screen and cannot move; here it is a
    /// box with a word in it, and turning the pair around without turning the text around leaves the
    /// popup translating 「hello」 out of 繁體中文 — a round trip through the wrong direction, which
    /// is not what anybody presses this for. 文字翻譯 swaps both for the same reason.
    ///
    /// 自動 is the source language most of the time, and it cannot move to the target side: there is
    /// no such thing as translating into "whatever". <see cref="EffectiveSourceLanguage"/> is what
    /// makes the button work from there anyway — the engine has already said what the original was,
    /// and that answer is what goes across.
    /// </remarks>
    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        // Both read before anything moves: the detected language describes the text that is about to
        // stop being the original, and ApplyLanguagePair clears it.
        var srcVal = EffectiveSourceLanguage();
        var tgtVal = TgtLangBox.SelectedValue as string;

        _suppressAuto = true;

        if (TranslatedText.Text.Length > 0)
        {
            var wasOriginal = SourceTextBox.Text;

            // Through the selection, so Ctrl+Z puts the original back — the same reason 清除 does it
            // that way, and the same text at stake: usually something carried in from another window.
            SourceTextBox.SelectAll();
            SourceTextBox.SelectedText = TranslatedText.Text;

            // Shown flipped now rather than left saying the old thing until the round trip lands.
            // Translating a translation back gives the text it came from, so this already is the
            // answer; waiting for the engine to agree would just read as lag.
            TranslatedText.Text = wasOriginal;
        }

        // Anything already in flight was asked under the old pair and would overwrite both.
        _seq++;

        // Target → source. ZH-HANT has to stay traditional rather than collapse to ZH, which is what
        // the mapping is for.
        if (tgtVal != null)
        {
            SrcLangBox.SelectedValue = LanguageData.MapTargetToSourceCode(tgtVal);
            if (SrcLangBox.SelectedValue == null) SrcLangBox.SelectedIndex = 0;
        }

        // Source → target. With 自動 and nothing detected — an empty box, or an engine that has not
        // answered — there is nothing to carry across, and English is the fallback: it is what most
        // of what people point this at is written in, and it is one click to correct.
        TgtLangBox.SelectedValue = LanguageData.MapSourceToTargetCode(
            srcVal.Length > 0 ? srcVal : "EN");
        if (TgtLangBox.SelectedValue == null) TgtLangBox.SelectedIndex = 0;

        _suppressAuto = false;

        ApplyLanguagePair();

        // The box holds text the user may well want to edit now, and the click left the caret on a
        // button. Caret after Focus, which selects the whole box on its own when focus arrives
        // programmatically — see Refill.
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuto || ProviderBox.SelectedValue is not TranslationProvider provider) return;

        SettingsService.Instance.Current.Provider = provider;
        SettingsService.Instance.Save();

        // The engine's answer belongs to the engine that gave it, and the new one may not answer at
        // all — see RenderSourceTtsAvailability.
        _detectedLang = "";
        RenderSourceTtsAvailability();
        RequestTranslate();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => BeginClose();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RenderChrome();
        RenderCopyLabel(copied: false);
        RenderSettingsButton();
        RenderSettingsHint();
        RenderSourceTtsAvailability();
    }

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Stop();
        _copiedHold.Stop();
        _foregroundWatch.Stop();
        _tts.Dispose();

        // Static and outliving every window, so a handler left attached keeps this one alive for as
        // long as the application runs.
        LocalizationService.LanguageChanged -= OnLanguageChanged;

        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }
}
