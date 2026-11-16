using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CompressIt.Core.Models;
using CompressIt.Core.Services;

namespace CompressIt.Controls;

public partial class TimelineControl : UserControl
{
    private bool _isDragging;
    private DateTime _lastClickTime = DateTime.MinValue;
    private Guid? _lastClickSegmentId;

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(TimelineControl),
            new PropertyMetadata(TimeSpan.Zero, (_, _) => { }));

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(TimeSpan), typeof(TimelineControl),
            new PropertyMetadata(TimeSpan.Zero, OnPositionChanged));

    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(nameof(Segments), typeof(IReadOnlyList<VideoSegment>), typeof(TimelineControl),
            new PropertyMetadata(Array.Empty<VideoSegment>(), OnVisualDataChanged));

    public static readonly DependencyProperty SelectedSegmentIdProperty =
        DependencyProperty.Register(nameof(SelectedSegmentId), typeof(Guid?), typeof(TimelineControl),
            new PropertyMetadata(null, OnVisualDataChanged));

    public static readonly DependencyProperty ThumbnailPathsProperty =
        DependencyProperty.Register(nameof(ThumbnailPaths), typeof(IReadOnlyList<string>), typeof(TimelineControl),
            new PropertyMetadata(Array.Empty<string>(), OnThumbnailsChanged));

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public TimeSpan Position
    {
        get => (TimeSpan)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IReadOnlyList<VideoSegment> Segments
    {
        get => (IReadOnlyList<VideoSegment>)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Guid? SelectedSegmentId
    {
        get => (Guid?)GetValue(SelectedSegmentIdProperty);
        set => SetValue(SelectedSegmentIdProperty, value);
    }

    public IReadOnlyList<string> ThumbnailPaths
    {
        get => (IReadOnlyList<string>)GetValue(ThumbnailPathsProperty);
        set => SetValue(ThumbnailPathsProperty, value);
    }

    public event Action<TimeSpan>? SeekRequested;
    public event Action<Guid>? SegmentSelected;
    public event Action<Guid>? SegmentIncludeToggled;

    public TimelineControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RedrawAll();
        TrackHost.SizeChanged += (_, _) => RedrawAll();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl) ctrl.UpdatePlayhead();
    }

    private static void OnVisualDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl) ctrl.RedrawAll();
    }

    private static void OnThumbnailsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl) ctrl.LoadThumbnails();
    }

    private void LoadThumbnails()
    {
        var paths = ThumbnailPaths;
        if (paths is null || paths.Count == 0)
        {
            ThumbnailsStrip.ItemsSource = null;
            return;
        }

        var images = new List<BitmapImage>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                images.Add(bmp);
            }
            catch
            {
                // Ignore broken thumbnail frames.
            }
        }
        ThumbnailsStrip.ItemsSource = images;
    }

    public void RedrawAll()
    {
        DrawRuler();
        DrawSegments();
        UpdatePlayhead();
    }

    private void DrawRuler()
    {
        RulerCanvas.Children.Clear();
        double width = TrackHost.ActualWidth;
        if (width <= 20 || Duration <= TimeSpan.Zero) return;

        int tickCount = Math.Clamp((int)(width / 95), 3, 14);
        for (int i = 0; i <= tickCount; i++)
        {
            double frac = (double)i / tickCount;
            double x = frac * width;
            var ts = TimeSpan.FromSeconds(Duration.TotalSeconds * frac);

            var tick = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 14,
                Y2 = 20,
                Stroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
                StrokeThickness = 1
            };
            RulerCanvas.Children.Add(tick);

            var label = new TextBlock
            {
                Text = FormatHelper.FormatTimecode(ts, includeMilliseconds: false),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(170, 226, 232, 240))
            };
            double left = Math.Clamp(x - 16, 0, Math.Max(0, width - 44));
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, 0);
            RulerCanvas.Children.Add(label);
        }
    }

    private void DrawSegments()
    {
        SegmentsCanvas.Children.Clear();
        double width = TrackHost.ActualWidth;
        double height = TrackHost.ActualHeight;
        if (width <= 0 || height <= 0 || Duration <= TimeSpan.Zero || Segments is null) return;

        double totalSec = Duration.TotalSeconds;

        foreach (var seg in Segments)
        {
            double startX = seg.Start.TotalSeconds / totalSec * width;
            double endX = seg.End.TotalSeconds / totalSec * width;
            double segWidth = Math.Max(2, endX - startX);
            bool isSelected = SelectedSegmentId == seg.Id;

            Color fillColor = seg.IsIncluded
                ? (isSelected ? Color.FromArgb(88, 34, 197, 94) : Color.FromArgb(42, 34, 197, 94))
                : (isSelected ? Color.FromArgb(90, 239, 68, 68) : Color.FromArgb(65, 30, 41, 59));

            Color borderColor = seg.IsIncluded
                ? (isSelected ? Color.FromRgb(74, 222, 128) : Color.FromArgb(170, 74, 222, 128))
                : (isSelected ? Color.FromRgb(248, 113, 113) : Color.FromArgb(130, 148, 163, 184));

            var border = new Border
            {
                Width = segWidth,
                Height = height,
                Background = new SolidColorBrush(fillColor),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(isSelected ? 2 : 1, isSelected ? 2 : 1, isSelected ? 2 : 1, isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(4)
            };

            if (segWidth >= 48)
            {
                var stack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(6, 0, 4, 5)
                };

                var title = new TextBlock
                {
                    Text = seg.IsIncluded ? $"#{seg.Index}" : $"#{seg.Index} (Cut)",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = seg.IsIncluded ? Brushes.White : new SolidColorBrush(Color.FromRgb(252, 165, 165))
                };
                stack.Children.Add(title);

                if (segWidth >= 82)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = FormatHelper.FormatTimecode(seg.Duration, includeMilliseconds: false),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromArgb(200, 226, 232, 240))
                    });
                }

                border.Child = stack;
            }

            Canvas.SetLeft(border, startX);
            Canvas.SetTop(border, 0);
            SegmentsCanvas.Children.Add(border);
        }
    }

    private void UpdatePlayhead()
    {
        double width = TrackHost.ActualWidth;
        double height = TrackHost.ActualHeight;
        if (width <= 0 || height <= 0 || Duration <= TimeSpan.Zero)
        {
            PlayheadLine.Visibility = Visibility.Collapsed;
            PlayheadHead.Visibility = Visibility.Collapsed;
            PlayheadTimePill.Visibility = Visibility.Collapsed;
            return;
        }

        PlayheadLine.Visibility = Visibility.Visible;
        PlayheadHead.Visibility = Visibility.Visible;
        PlayheadTimePill.Visibility = Visibility.Visible;

        double frac = Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0, 1);
        double x = frac * width;

        PlayheadLine.X1 = x;
        PlayheadLine.X2 = x;
        PlayheadLine.Y1 = 0;
        PlayheadLine.Y2 = height;

        Canvas.SetLeft(PlayheadHead, x);
        Canvas.SetTop(PlayheadHead, 0);

        PlayheadTimeText.Text = FormatHelper.FormatTimecode(Position);
        double pillWidth = PlayheadTimePill.ActualWidth > 0 ? PlayheadTimePill.ActualWidth : 58;
        double pillLeft = Math.Clamp(x + 6, 4, Math.Max(4, width - pillWidth - 4));
        Canvas.SetLeft(PlayheadTimePill, pillLeft);
    }

    private void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Duration <= TimeSpan.Zero) return;

        var pos = e.GetPosition(TrackHost);
        var clickedTime = PositionFromX(pos.X);

        // Select segment under cursor (and toggle on double click).
        var hitSegment = Segments?.FirstOrDefault(s => clickedTime >= s.Start && clickedTime <= s.End);
        if (hitSegment is not null)
        {
            var now = DateTime.UtcNow;
            if (_lastClickSegmentId == hitSegment.Id && (now - _lastClickTime).TotalMilliseconds < 350)
            {
                SegmentIncludeToggled?.Invoke(hitSegment.Id);
                _lastClickTime = DateTime.MinValue;
            }
            else
            {
                SegmentSelected?.Invoke(hitSegment.Id);
                _lastClickSegmentId = hitSegment.Id;
                _lastClickTime = now;
            }
        }

        _isDragging = true;
        TrackHost.CaptureMouse();
        SeekToX(pos.X);
    }

    private void Track_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || Duration <= TimeSpan.Zero) return;
        SeekToX(e.GetPosition(TrackHost).X);
    }

    private void Track_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        TrackHost.ReleaseMouseCapture();
    }

    private TimeSpan PositionFromX(double x)
    {
        double width = Math.Max(1, TrackHost.ActualWidth);
        double frac = Math.Clamp(x / width, 0, 1);
        return TimeSpan.FromSeconds(Duration.TotalSeconds * frac);
    }

    private void SeekToX(double x)
    {
        var ts = PositionFromX(x);
        Position = ts;
        SeekRequested?.Invoke(ts);
    }
}
