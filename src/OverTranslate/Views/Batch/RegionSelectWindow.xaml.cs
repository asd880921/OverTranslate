using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OverTranslate.Services.Batch;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Rectangle = System.Windows.Shapes.Rectangle;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Views.Batch;

/// <summary>
/// Walks the user through the queue one page at a time so they can mark which parts to translate.
/// Its own window rather than a panel in the shell: comic pages are tall, and the shell's content
/// area would shrink a page to the point where you cannot see which bubble you are drawing around.
/// </summary>
public partial class RegionSelectWindow : Window
{
    private const double MinRegionSize = 8;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;

    private readonly List<BatchImage> _queue;
    private readonly List<List<Rect>> _regions;   // image pixels, parallel to _queue

    private int _index;
    private BitmapImage? _page;
    private double _scale = 1;
    private bool _fitToWindow = true;

    private Point _dragStart;
    private Rectangle? _dragPreview;
    private bool _isDragging;

    /// <summary>Null until the user finishes; set to the queue with regions attached.</summary>
    public IReadOnlyList<BatchImage>? Result { get; private set; }

    public RegionSelectWindow(IReadOnlyList<BatchImage> queue)
    {
        InitializeComponent();

        _queue = [.. queue];
        _regions = queue.Select(image => new List<Rect>(image.Regions)).ToList();

        Loaded += (_, _) => LoadCurrent();
        SizeChanged += (_, _) => { if (_fitToWindow) ApplyFit(); };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc backs out of the whole run — nothing has been written yet, so there is nothing to undo.
        if (e.Key == Key.Escape)
        {
            Result = null;
            Close();
            e.Handled = true;
        }
    }

    private void LoadCurrent()
    {
        var image = _queue[_index];

        try
        {
            _page = new BitmapImage();
            _page.BeginInit();
            _page.CacheOption = BitmapCacheOption.OnLoad;   // do not hold the user's file open
            _page.UriSource = new Uri(image.Path);
            _page.EndInit();
            PageImage.Source = _page;
        }
        catch (Exception)
        {
            // A file that cannot be shown also cannot be marked up; let the batch run report it.
            _page = null;
            PageImage.Source = null;
        }

        PositionText.Text = $"第 {_index + 1} 張，共 {_queue.Count} 張 · {image.FileName}";
        NextBtn.Content = _index == _queue.Count - 1 ? "完成並開始翻譯" : "下一張";
        PrevBtn.IsEnabled = _index > 0;

        if (_fitToWindow) ApplyFit(); else ApplyScale();
        Redraw();
    }

    private void ApplyFit()
    {
        if (_page is null) return;

        // Padding (12 each side) plus a little slack so the fitted page never triggers scrollbars.
        double availableW = Math.Max(50, Scroller.ActualWidth - 34);
        double availableH = Math.Max(50, Scroller.ActualHeight - 34);
        _scale = Math.Clamp(
            Math.Min(availableW / _page.PixelWidth, availableH / _page.PixelHeight), MinZoom, MaxZoom);
        ApplyScale();
    }

    private void ApplyScale()
    {
        if (_page is null) return;

        PageHost.Width = _page.PixelWidth * _scale;
        PageHost.Height = _page.PixelHeight * _scale;
        ZoomText.Text = $"{_scale * 100:0}%";
        Redraw();
    }

    private void Zoom(double factor)
    {
        _fitToWindow = false;
        _scale = Math.Clamp(_scale * factor, MinZoom, MaxZoom);
        ApplyScale();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / 1.25);

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _fitToWindow = true;
        ApplyFit();
    }

    // ── Drawing regions ──────────────────────────────────────────────────────────────────────

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_page is null) return;

        _dragStart = e.GetPosition(RegionCanvas);
        _isDragging = true;
        RegionCanvas.CaptureMouse();

        // Feedback starts on press, not on release, so the box is visibly "being drawn" at once.
        _dragPreview = new Rectangle
        {
            Stroke = (Brush)FindResource("AppAccent"),
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(38, 0, 120, 212)),
        };
        Canvas.SetLeft(_dragPreview, _dragStart.X);
        Canvas.SetTop(_dragPreview, _dragStart.Y);
        RegionCanvas.Children.Add(_dragPreview);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dragPreview is null) return;

        var box = Normalize(_dragStart, e.GetPosition(RegionCanvas));
        Canvas.SetLeft(_dragPreview, box.X);
        Canvas.SetTop(_dragPreview, box.Y);
        _dragPreview.Width = box.Width;
        _dragPreview.Height = box.Height;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        RegionCanvas.ReleaseMouseCapture();

        var box = Normalize(_dragStart, e.GetPosition(RegionCanvas));
        _dragPreview = null;

        // A click, or a slip of the hand, should not leave a useless sliver behind.
        if (box.Width >= MinRegionSize && box.Height >= MinRegionSize)
            _regions[_index].Add(new Rect(
                box.X / _scale, box.Y / _scale, box.Width / _scale, box.Height / _scale));

        Redraw();
    }

    private Rect Normalize(Point a, Point b)
    {
        double x = Math.Max(0, Math.Min(a.X, b.X));
        double y = Math.Max(0, Math.Min(a.Y, b.Y));
        double right = Math.Min(RegionCanvas.ActualWidth, Math.Max(a.X, b.X));
        double bottom = Math.Min(RegionCanvas.ActualHeight, Math.Max(a.Y, b.Y));
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private void Redraw()
    {
        RegionCanvas.Children.Clear();

        var regions = _regions[_index];
        for (int i = 0; i < regions.Count; i++)
            RegionCanvas.Children.Add(BuildRegionVisual(regions[i], i));

        SummaryText.Text = regions.Count == 0
            ? "未框選任何區域，此張圖片將整張辨識翻譯"
            : $"已框選 {regions.Count} 個區域";

        HintText.Visibility = _page is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement BuildRegionVisual(Rect region, int index)
    {
        double left = region.X * _scale;
        double top = region.Y * _scale;
        double width = region.Width * _scale;
        double height = region.Height * _scale;

        var accent = (Brush)FindResource("AppAccent");

        var box = new Border
        {
            Width = width,
            Height = height,
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(30, 0, 120, 212)),
            CornerRadius = new CornerRadius(2),
        };

        var remove = new Button
        {
            Content = "✕",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            FontSize = 10,
            Cursor = Cursors.Hand,
            ToolTip = "移除此區域",
            Foreground = Brushes.White,
            Background = accent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        remove.Click += (_, e) =>
        {
            e.Handled = true;   // must not also start a new drag underneath
            _regions[_index].RemoveAt(index);
            Redraw();
        };

        var host = new Grid { Width = width, Height = height };
        host.Children.Add(box);
        host.Children.Add(remove);

        Canvas.SetLeft(host, left);
        Canvas.SetTop(host, top);
        return host;
    }

    // ── Moving through the queue ─────────────────────────────────────────────────────────────

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _regions[_index].Clear();
        Redraw();
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_index == 0) return;
        _index--;
        LoadCurrent();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _queue.Count - 1)
        {
            _index++;
            LoadCurrent();
            return;
        }

        Result = _queue
            .Select((image, i) => image with { Regions = _regions[i] })
            .ToList();
        Close();
    }
}
