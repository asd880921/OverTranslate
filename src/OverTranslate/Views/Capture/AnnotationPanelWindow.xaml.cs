using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in the implicit usings
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
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
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 白
        Color.FromRgb(0xFA, 0xCC, 0x15), // 黃
        Color.FromRgb(0x22, 0xC5, 0x5E), // 綠
        Color.FromRgb(0x38, 0xBD, 0xF8), // 藍
        Color.FromRgb(0xEC, 0x48, 0x99), // 粉
        Color.FromRgb(0xF9, 0x73, 0x16), // 橘
        Color.FromRgb(0x7C, 0x3A, 0xED), // 紫
        Color.FromRgb(0x00, 0x00, 0x00), // 黑
    ];

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

    /// <summary>The chosen colour as it is written to the settings file.</summary>
    public string InkColorText => $"#{InkColor.R:X2}{InkColor.G:X2}{InkColor.B:X2}";

    /// <summary>
    /// Reads a colour back out of the settings file, falling back to the palette's default.
    /// </summary>
    /// <remarks>
    /// That file is editable by hand and survives upgrades, so this is handed arbitrary text often
    /// enough to be worth catching rather than checking: ColorConverter throws on anything it cannot
    /// read, and a capture session is the wrong place to find out.
    /// </remarks>
    private static Color ParseColor(string text)
    {
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color color) return color;
        }
        catch (FormatException) { }
        return PaletteColors[5];
    }

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

    public AnnotationPanelWindow(AnnotationTool tool, string colorText, double thicknessFraction)
    {
        InitializeComponent();

        Tool              = tool;
        InkColor          = ParseColor(colorText);
        ThicknessFraction = Math.Clamp(thicknessFraction, 0, 1);

        BuildPalette();
        RenderToolSelection();
        ThicknessSlider.Value = ThicknessFraction;
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

    /// <summary>
    /// Places the panel under <paramref name="anchorPhysCentreX"/> with the caret pointing at it.
    /// </summary>
    /// <remarks>
    /// The panel is centred on the button where there is room and slides along the monitor's edge
    /// where there is not — but the caret stays on the button either way, because the caret is the
    /// only thing saying which button this belongs to. It is clamped to the panel's own corners so a
    /// heavily offset panel does not end up with an arrow hanging off its end.
    /// </remarks>
    public void PlaceUnder(double anchorPhysCentreX, double barPhysBottom, double scale)
    {
        UpdateLayout();

        double panelPhysW = (ActualWidth  > 0 ? ActualWidth  : 700) * scale;
        var wa = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point((int)anchorPhysCentreX, (int)barPhysBottom)).WorkingArea;

        double margin  = 4 * scale;
        double minLeft = wa.Left + margin;
        double maxLeft = Math.Max(minLeft, wa.Right - panelPhysW - margin);
        double left    = Math.Clamp(anchorPhysCentreX - panelPhysW / 2, minLeft, maxLeft);

        // The caret lives inside the window's own margin, so its coordinates are window-relative DIP.
        double caretDip = (anchorPhysCentreX - left) / scale - Caret.Data.Bounds.Width / 2;
        Canvas.SetLeft(Caret, Math.Clamp(caretDip - 12, 6, Math.Max(6, ActualWidth - 42)));

        ScreenGeometry.MoveToPhysical(this, (int)Math.Round(left), (int)Math.Round(barPhysBottom));
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

        // A settings file from before this panel existed, or one hand-edited, can name a colour that
        // is not on the palette. Rather than show eight unchecked swatches — a palette with no
        // current colour, which is a state the user cannot get out of by looking at it — fall back to
        // the first one and let the ring say what is in hand.
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
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        ThicknessFraction = e.NewValue;
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
