using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;

namespace OverTranslate.Views.Capture;

/// <summary>
/// The tools behind 標記, in a panel hung under the button that opened it.
/// </summary>
/// <remarks>
/// A second bar rather than more buttons on the first one. What is on the capture toolbar is the
/// shape of one capture — the languages, the engine, the six things to do with the result — and it is
/// on screen for the whole session. These seven controls only mean anything while a tool is in hand,
/// and putting them on the bar would make everyone who never draws pay for them on every capture.
/// </remarks>
public partial class AnnotationPanelWindow : Window
{
    /// <summary>
    /// The colours 標記 offers, in the order they are shown.
    /// </summary>
    /// <remarks>
    /// A fixed set, not a picker. Someone marking up a screenshot mid-task wants a colour that shows
    /// against what is underneath, and that decision is between about eight answers — a full picker
    /// would be a second window, a mode, and a decision to make before the first stroke.
    ///
    /// White and black are the two ends, and both are here because the ground is somebody else's
    /// screen: a mark on a dark game and a mark on a white document cannot be the same colour.
    /// </remarks>
    private static readonly Color[] PaletteColors =
    [
        Color.FromRgb(0x00, 0x00, 0x00), // 黑
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 白
        Color.FromRgb(0xFA, 0xCC, 0x15), // 黃
        Color.FromRgb(0x22, 0xC5, 0x5E), // 綠
        Color.FromRgb(0x38, 0xBD, 0xF8), // 藍
        Color.FromRgb(0xEC, 0x48, 0x99), // 粉
        Color.FromRgb(0xF9, 0x73, 0x16), // 橘
        Color.FromRgb(0x7C, 0x3A, 0xED), // 紫
    ];

    /// <summary>What every capture starts with, before the user has touched anything.</summary>
    /// <remarks>
    /// Fixed rather than remembered. Nothing about 標記 outlives the capture it was used in — see
    /// CaptureSettings — so these are the only starting values there are, and they are named here
    /// rather than at the caller because this is the file that knows what the palette contains.
    /// </remarks>
    public static AnnotationTool DefaultTool => AnnotationTool.Pen;

    public static Color DefaultColor => PaletteColors[0];

    /// <summary>Halfway along the slider: no tool's range has a better place to start.</summary>
    public const double DefaultThickness = 0.5;

    /// <summary>Likewise halfway, which lands on a highlight you can still read through.</summary>
    public const double DefaultOpacity = 0.5;

    /// <summary>
    /// What the 透明度 slider means, as (faintest, strongest).
    /// </summary>
    /// <remarks>
    /// Neither end is allowed to be useless. At 0 the highlight would not exist, and at 1 it would
    /// cover the words it was drawn to pick out — so the range stops short of both, and every
    /// position on the slider is one somebody might actually want.
    /// </remarks>
    private const double MinOpacity = 0.15;
    private const double MaxOpacity = 0.75;

    /// <summary>
    /// What the one slider means for each tool, as (thinnest, thickest).
    /// </summary>
    /// <remarks>
    /// Three ranges rather than one, because the three tools are not the same kind of mark. A pen at
    /// 30 is a blob; a highlighter at 3 does not cover a line of text; an eraser has no width at all,
    /// only a reach. Mapping one slider position onto each tool's own range is what lets the control
    /// stay a single "粗細" the whole time.
    /// </remarks>
    private static (double Min, double Max) RangeFor(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlighter => (10, 34),
        AnnotationTool.Eraser      => (6, 26),
        _                          => (2, 14),
    };

    private readonly List<ToggleButton> _swatches = [];
    private bool _initializing = true;

    /// <summary>Raised whenever the tool, the colour or the width changed.</summary>
    public event EventHandler? SettingsChanged;

    public event EventHandler? UndoRequested;
    public event EventHandler? RedoRequested;

    public AnnotationTool Tool { get; private set; }
    public Color InkColor { get; private set; }

    /// <summary>Where the 透明度 slider sits, 0 to 1.</summary>
    public double OpacityFraction { get; private set; }

    /// <summary>How see-through a highlight drawn now would be.</summary>
    public double Opacity => MinOpacity + (MaxOpacity - MinOpacity) * OpacityFraction;

    /// <summary>Where the slider sits, 0 to 1. The width itself depends on the tool — see <see cref="Thickness"/>.</summary>
    public double ThicknessFraction { get; private set; }

    public double Thickness
    {
        get
        {
            var (min, max) = RangeFor(Tool);
            return min + (max - min) * ThicknessFraction;
        }
    }

    public AnnotationPanelWindow(
        AnnotationTool tool, Color color, double thicknessFraction, double opacityFraction)
    {
        InitializeComponent();

        Tool              = tool;
        InkColor          = color;
        ThicknessFraction = Math.Clamp(thicknessFraction, 0, 1);
        OpacityFraction   = Math.Clamp(opacityFraction, 0, 1);

        BuildPalette();
        RenderToolSelection();
        ThicknessSlider.Value = ThicknessFraction;
        OpacitySlider.Value   = OpacityFraction;
        _initializing = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Never takes focus. The capture toolbar above it does not either, and a panel that stole
        // activation from the application being captured would be the one piece of this session that
        // interrupted what the user was doing.
        WindowStyles.ApplyNoActivate(this);
    }

    /// <summary>Reflects whether there is anything left to undo or redo.</summary>
    public void SetHistoryState(bool canUndo, bool canRedo)
    {
        UndoBtn.IsEnabled = canUndo;
        RedoBtn.IsEnabled = canRedo;
    }

    /// <summary>The gap left between the capture toolbar and this panel, in DIP.</summary>
    /// <remarks>
    /// Small on purpose. The two bars are one control opened in two pieces, and the distance between
    /// them is what says so: far enough apart to read as two surfaces rather than one tall one,
    /// close enough that the eye does not have to decide whether they belong together.
    /// </remarks>
    private const double GapFromToolbar = 6;

    /// <summary>
    /// Puts the panel under the capture toolbar, centred on it — or above it where there is no room.
    /// </summary>
    /// <remarks>
    /// <para>Measured against the toolbar's visible surface and this panel's own, not against either
    /// window's edges: both windows are larger than the bars they show, because each leaves a margin
    /// for its shadow to fade out in. Placing window to window would leave a gap of both margins
    /// added together, which is most of a centimetre of nothing.</para>
    ///
    /// <para>Below first because that is where a thing opened from a bar is looked for, and the
    /// toolbar itself has already been placed with the same preference. Above is the fallback, not a
    /// second option: it is used only when the panel would otherwise hang off the bottom of the
    /// monitor.</para>
    /// </remarks>
    public void PlaceNear(Rect toolbarVisiblePhys, double scale)
    {
        UpdateLayout();

        // Where the visible panel starts inside its own window, and how big it is, in pixels.
        var inset = PanelSurface.TranslatePoint(new Point(0, 0), this);
        double visW = PanelSurface.ActualWidth  * scale;
        double visH = PanelSurface.ActualHeight * scale;
        double gap  = GapFromToolbar * scale;

        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)(toolbarVisiblePhys.Left + toolbarVisiblePhys.Width / 2),
            (int)(toolbarVisiblePhys.Top  + toolbarVisiblePhys.Height / 2))).WorkingArea;

        double margin  = 4 * scale;
        double minLeft = wa.Left + margin;
        double maxLeft = Math.Max(minLeft, wa.Right - visW - margin);
        double visLeft = Math.Clamp(
            toolbarVisiblePhys.Left + (toolbarVisiblePhys.Width - visW) / 2, minLeft, maxLeft);

        double visTop = toolbarVisiblePhys.Bottom + gap;
        if (visTop + visH > wa.Bottom) visTop = toolbarVisiblePhys.Top - gap - visH;

        // On a monitor with room for neither, the panel goes wherever it fits rather than off the
        // top. It is the only way to change tools, so it can never be the thing that ends up out of
        // reach — losing the gap to the toolbar is the cheaper failure.
        visTop = Math.Clamp(visTop, wa.Top + margin, Math.Max(wa.Top + margin, wa.Bottom - visH - margin));

        ScreenGeometry.MoveToPhysical(this,
            (int)Math.Round(visLeft - inset.X * scale),
            (int)Math.Round(visTop  - inset.Y * scale));
    }

    private void BuildPalette()
    {
        foreach (var color in PaletteColors)
        {
            var swatch = new ToggleButton
            {
                Style      = (Style)FindResource("AnnotationSwatchButton"),
                Background = new SolidColorBrush(color),
                IsChecked  = color == InkColor,
                Tag        = color,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, color.ToString());
            swatch.Click += Swatch_Click;
            _swatches.Add(swatch);
            Palette.Children.Add(swatch);
        }

        // A palette showing eight unchecked swatches is a state the user cannot get out of by
        // looking at it. Nothing should reach here — the caller only ever hands back a colour this
        // list gave it — but the ring is the only thing saying what is in hand, and it has to be on
        // something.
        if (!_swatches.Any(s => s.IsChecked == true))
        {
            InkColor = PaletteColors[0];
            _swatches[0].IsChecked = true;
        }
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not Color color) return;

        // Clicking the one already chosen must leave it chosen: a ToggleButton unchecks itself on the
        // second press, and a palette with nothing selected is not a state this control has.
        InkColor = color;
        foreach (var swatch in _swatches) swatch.IsChecked = ReferenceEquals(swatch, clicked);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToolBtn_Click(object sender, RoutedEventArgs e)
    {
        Tool = sender switch
        {
            _ when ReferenceEquals(sender, HighlighterTool) => AnnotationTool.Highlighter,
            _ when ReferenceEquals(sender, EraserTool)      => AnnotationTool.Eraser,
            _                                               => AnnotationTool.Pen,
        };

        RenderToolSelection();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderToolSelection()
    {
        PenTool.IsChecked         = Tool == AnnotationTool.Pen;
        HighlighterTool.IsChecked = Tool == AnnotationTool.Highlighter;
        EraserTool.IsChecked      = Tool == AnnotationTool.Eraser;

        // The eraser takes a colour from nothing and gives one to nothing. Left enabled it would be
        // eight buttons that quietly do not apply to what is in hand.
        Palette.IsEnabled = Tool != AnnotationTool.Eraser;
        Palette.Opacity   = Tool == AnnotationTool.Eraser ? 0.35 : 1.0;

        // Taken away rather than greyed out, unlike the palette. A dimmed control says "not right
        // now", which is true of the swatches — put the pen back in hand and they apply again to the
        // very same marks. 透明度 is not that: for a pen there is no faded answer to be had, so the
        // control is not unavailable, it is inapplicable, and the panel is simply shorter without it.
        var opacityVisibility = Tool == AnnotationTool.Highlighter
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpacityBlock.Visibility   = opacityVisibility;
        OpacityDivider.Visibility = opacityVisibility;
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        ThicknessFraction = e.NewValue;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        OpacityFraction = e.NewValue;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e)
        => UndoRequested?.Invoke(this, EventArgs.Empty);

    private void RedoBtn_Click(object sender, RoutedEventArgs e)
        => RedoRequested?.Invoke(this, EventArgs.Empty);

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
