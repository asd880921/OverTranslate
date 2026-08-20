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
    /// Per window and stored nowhere. Pinning answers "keep this one on screen while I work", which
    /// is a statement about the popup in front of the user right now — a remembered pin would mean
    /// every later summon opened a window that never goes away, which is the opposite of what this
    /// feature is.
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
    /// Brings the popup up over the foreground application, carrying whatever is selected there.
    /// </summary>
    /// <remarks>
    /// The selection is read before anything is shown: putting a window on the screen takes the
    /// foreground away from the application holding it, and the copy would then be sent here.
    ///
    /// An already-open popup is refilled rather than replaced, so pressing the shortcut twice does
    /// not throw away a pin or a position the user has set.
    /// </remarks>
    public static async Task SummonAsync()
    {
        var selection = await SelectedTextReader.ReadAsync();

        if (_current is { _closing: false } open)
        {
            open.Refill(selection);
            open.ReacquireForeground();
            return;
        }

        var window = new QuickLookupWindow();
        _current = window;
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

        var x = Math.Clamp(pointer.X - 36 * scale, minX, maxX);
        var y = Math.Clamp(pointer.Y - 8 * scale, minY, maxY);

        ScreenGeometry.MoveToPhysical(this, (int)Math.Round(x), (int)Math.Round(y));

        // Anchored to the pointer rather than to a corner: the popup should look like it came out of
        // the place it was asked for, and go back into it.
        Surface.RenderTransformOrigin = new Point(
            Math.Clamp((pointer.X - x) / Math.Max(w, 1), 0, 1),
            Math.Clamp((pointer.Y - y) / Math.Max(h, 1), 0, 1));
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
    /// The relationship runs the other way instead: pinning stops the dragging.
    /// </remarks>
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Pinning fixes the window where it is, so it also takes dragging away: the same pin already
        // stops a later summon from moving the popup to the pointer, and a pin that held against one
        // way of moving it but not the other would mean two different things.
        if (_pinned) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws when the button is already up by the time it runs, which a fast click
            // can manage. There is nothing to move and nothing to report.
        }
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        RenderChrome();
    }

    /// <summary>
    /// Settles the header's quiet controls against where the pointer is and whether it is pinned.
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

        Placeholder.Visibility = SourceTextBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        RenderSourceTtsAvailability();
        if (_settingsOpen) return;

        ResultPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = TranslatedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    // ══════════════════════════ Reading aloud ══════════════════════════

    /// <summary>
    /// The language 朗讀原文 would read in, or empty when there is not one yet.
    /// </summary>
    /// <inheritdoc cref="_detectedLang"/>
    private string SourceVoiceLanguage()
    {
        var chosen = SrcLangBox.SelectedValue as string;
        if (!LanguageData.IsAutomaticSource(chosen)) return LanguageData.GetValidSourceCode(chosen);
        return _detectedLang;
    }

    private void RenderSourceTtsAvailability()
    {
        var available = SourceVoiceLanguage().Length > 0 && SourceTextBox.Text.Length > 0;

        SrcTtsBtn.IsEnabled = available;
        SrcTtsBtn.ToolTip = LocalizationService.Get(
            available ? "S.QuickLookup.SpeakSourceTip" : "S.QuickLookup.SpeakSourceUnknown");
    }

    private async void SrcTtsBtn_Click(object sender, RoutedEventArgs e)
        => await ToggleTtsAsync(SrcTtsBtn, SourceTextBox.Text, SourceVoiceLanguage());

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

    /// <remarks>
    /// In place rather than in a window of its own: a settings window over a popup that disappears
    /// when the pointer leaves it would be a window whose owner can vanish underneath it.
    /// </remarks>
    private void ShowSettings(bool open)
    {
        _settingsOpen = open;

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

        var settings = SettingsService.Instance.Current;
        settings.SourceLanguage = LanguageData.GetValidSourceCode(SrcLangBox.SelectedValue as string);
        settings.TargetLanguage = LanguageData.GetValidTargetCode(TgtLangBox.SelectedValue as string);
        SettingsService.Instance.Save();

        // The engine's answer belongs to the language pair it was asked about.
        _detectedLang = "";
        RequestTranslate();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuto || ProviderBox.SelectedValue is not TranslationProvider provider) return;

        SettingsService.Instance.Current.Provider = provider;
        SettingsService.Instance.Save();
        RequestTranslate();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => BeginClose();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RenderChrome();
        RenderCopyLabel(copied: false);
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
