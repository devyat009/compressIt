using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CompressIt.Core.Models;
using CompressIt.Core.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CompressIt.Controls;

public partial class TimelineControl : UserControl
{
    public const double MinZoom = 1.0;
    public const double MaxZoom = 24.0;

    private bool _isDraggingPlayhead;
    private Guid? _draggingHandleSegmentId;
    private bool _draggingStartHandle;
    private DateTime _lastClickTime = DateTime.MinValue;
    private Guid? _lastClickSegmentId;

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(TimelineControl),
            new PropertyMetadata(TimeSpan.Zero, OnVisualDataChanged));

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

    public static readonly DependencyProperty ZoomLevelProperty =
        DependencyProperty.Register(nameof(ZoomLevel), typeof(double), typeof(TimelineControl),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnZoomLevelChanged));

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

    public double ZoomLevel
    {
        get => (double)GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, Math.Clamp(value, MinZoom, MaxZoom));
    }

    public event Action<TimeSpan>? SeekRequested;
    public event Action<Guid>? SegmentSelected;
    public event Action<Guid>? SegmentIncludeToggled;
    public event Action<Guid, bool, TimeSpan>? SegmentBoundaryAdjustRequested;

    public TimelineControl()
    {
        InitializeComponent();
        TimelineScrollViewer.SizeChanged += (_, _) => UpdateZoomWidthAndRedraw();
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

    private static void OnZoomLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl) ctrl.UpdateZoomWidthAndRedraw();
    }

    private void UpdateZoomWidthAndRedraw()
    {
        double clampedZoom = Math.Clamp(ZoomLevel, MinZoom, MaxZoom);
        if (clampedZoom <= 1.001)
        {
            TimelineScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ZoomContentHost.Width = double.NaN;
        }
        else
        {
            double viewportW = TimelineScrollViewer.ActualWidth;
            if (viewportW <= 10) return;

            TimelineScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            ZoomContentHost.Width = viewportW * clampedZoom;
        }

        ZoomContentHost.UpdateLayout();
        RedrawAll();
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

        int tickCount = Math.Clamp((int)(width / 95), 3, 240);
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
                Text = FormatHelper.FormatTimecode(ts, includeMilliseconds: ZoomLevel >= 4.0),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(170, 226, 232, 240))
            };
            double left = Math.Clamp(x - 16, 0, Math.Max(0, width - 54));
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

        // 1. Draw gray LosslessCut-style cut-out zones for any trimmed gaps outside/between segments
        var ordered = Segments.OrderBy(s => s.Start).ToList();
        TimeSpan cursor = TimeSpan.Zero;
        foreach (var seg in ordered)
        {
            if (seg.Start > cursor)
            {
                double gapStartX = cursor.TotalSeconds / totalSec * width;
                double gapEndX = seg.Start.TotalSeconds / totalSec * width;
                DrawCutOutRegion(gapStartX, gapEndX - gapStartX, height, isSelected: false);
            }
            if (seg.End > cursor) cursor = seg.End;
        }
        if (cursor < Duration)
        {
            double gapStartX = cursor.TotalSeconds / totalSec * width;
            DrawCutOutRegion(gapStartX, width - gapStartX, height, isSelected: false);
        }

        // 2. Draw segments (included blocks + excluded gray cut-out blocks) and draggable < / > handles
        foreach (var seg in ordered)
        {
            double startX = seg.Start.TotalSeconds / totalSec * width;
            double endX = seg.End.TotalSeconds / totalSec * width;
            double segWidth = Math.Max(4, endX - startX);
            bool isSelected = SelectedSegmentId == seg.Id;

            if (!seg.IsIncluded)
            {
                DrawCutOutRegion(startX, segWidth, height, isSelected, seg.Index);
            }
            else
            {
                Color fillColor = isSelected
                    ? Color.FromArgb(90, 34, 197, 94)
                    : Color.FromArgb(44, 34, 197, 94);

                Color borderColor = isSelected
                    ? Color.FromRgb(74, 222, 128)
                    : Color.FromArgb(180, 74, 222, 128);

                var border = new Border
                {
                    Width = segWidth,
                    Height = height,
                    Background = new SolidColorBrush(fillColor),
                    BorderBrush = new SolidColorBrush(borderColor),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    CornerRadius = new CornerRadius(4),
                    IsHitTestVisible = false
                };

                if (segWidth >= 64)
                {
                    var stack = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(18, 0, 18, 5)
                    };

                    stack.Children.Add(new TextBlock
                    {
                        Text = $"#{seg.Index}",
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White
                    });

                    if (segWidth >= 96)
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

            // Draggable "<" (start) and ">" (end) arrow handles on each end of the segment
            double handleWidth = Math.Clamp(segWidth * 0.28, 8, 15);
            Color handleBg = seg.IsIncluded ? Color.FromArgb(200, 22, 163, 74) : Color.FromArgb(195, 71, 85, 105);
            Color handleHoverBg = seg.IsIncluded ? Color.FromRgb(74, 222, 128) : Color.FromRgb(148, 163, 184);

            var leftHandle = CreateEdgeArrowHandle(seg.Id, isStart: true, "<", handleWidth, height, handleBg, handleHoverBg);
            Canvas.SetLeft(leftHandle, startX);
            Canvas.SetTop(leftHandle, 0);
            SegmentsCanvas.Children.Add(leftHandle);

            var rightHandle = CreateEdgeArrowHandle(seg.Id, isStart: false, ">", handleWidth, height, handleBg, handleHoverBg);
            Canvas.SetLeft(rightHandle, Math.Max(startX + handleWidth, endX - handleWidth));
            Canvas.SetTop(rightHandle, 0);
            SegmentsCanvas.Children.Add(rightHandle);
        }
    }

    private void DrawCutOutRegion(double startX, double regionWidth, double height, bool isSelected, int? excludedIndex = null)
    {
        if (regionWidth < 2) return;

        var container = new Grid
        {
            Width = regionWidth,
            Height = height,
            IsHitTestVisible = false,
            ClipToBounds = true
        };

        var bg = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 18, 22, 29)),
            BorderBrush = new SolidColorBrush(isSelected ? Color.FromRgb(148, 163, 184) : Color.FromArgb(90, 100, 116, 139)),
            BorderThickness = new Thickness(isSelected ? 1.5 : 1)
        };
        container.Children.Add(bg);

        // LosslessCut-style dashed center line + trash icon
        if (regionWidth >= 14)
        {
            var dashBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            double midY = height / 2.0;

            if (regionWidth >= 34)
            {
                double gapHalf = 14;
                double leftLineEnd = Math.Max(4, regionWidth / 2.0 - gapHalf);
                double rightLineStart = Math.Min(regionWidth - 4, regionWidth / 2.0 + gapHalf);

                if (leftLineEnd > 4)
                {
                    container.Children.Add(new Line
                    {
                        X1 = 4,
                        X2 = leftLineEnd,
                        Y1 = midY,
                        Y2 = midY,
                        Stroke = dashBrush,
                        StrokeThickness = 1.5,
                        StrokeDashArray = [3, 3]
                    });
                }

                if (rightLineStart < regionWidth - 4)
                {
                    container.Children.Add(new Line
                    {
                        X1 = rightLineStart,
                        X2 = regionWidth - 4,
                        Y1 = midY,
                        Y2 = midY,
                        Stroke = dashBrush,
                        StrokeThickness = 1.5,
                        StrokeDashArray = [3, 3]
                    });
                }

                var trashIcon = new SymbolIcon
                {
                    Symbol = SymbolRegular.Delete20,
                    FontSize = 15,
                    Foreground = dashBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                container.Children.Add(trashIcon);
            }
            else
            {
                container.Children.Add(new Line
                {
                    X1 = 2,
                    X2 = regionWidth - 2,
                    Y1 = midY,
                    Y2 = midY,
                    Stroke = dashBrush,
                    StrokeThickness = 1.5,
                    StrokeDashArray = [3, 3]
                });
            }
        }

        if (excludedIndex is int idx && regionWidth >= 64)
        {
            container.Children.Add(new TextBlock
            {
                Text = $"#{idx} (Cut)",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(18, 0, 18, 4)
            });
        }

        Canvas.SetLeft(container, startX);
        Canvas.SetTop(container, 0);
        SegmentsCanvas.Children.Add(container);
    }

    private Border CreateEdgeArrowHandle(
        Guid segmentId,
        bool isStart,
        string arrowText,
        double width,
        double height,
        Color normalBg,
        Color hoverBg)
    {
        var handle = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(normalBg),
            CornerRadius = isStart ? new CornerRadius(4, 0, 0, 4) : new CornerRadius(0, 4, 4, 0),
            Cursor = Cursors.SizeWE,
            ToolTip = isStart ? "Drag < to adjust start / cut" : "Drag > to adjust end / cut",
            Child = new TextBlock
            {
                Text = arrowText,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            }
        };

        handle.MouseEnter += (_, _) => handle.Background = new SolidColorBrush(hoverBg);
        handle.MouseLeave += (_, _) => handle.Background = new SolidColorBrush(normalBg);
        handle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _draggingHandleSegmentId = segmentId;
            _draggingStartHandle = isStart;
            SegmentSelected?.Invoke(segmentId);
            // Capture on TrackHost so dragging stays uninterrupted even as SegmentsCanvas redraws.
            TrackHost.CaptureMouse();
        };

        return handle;
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

    private void UserControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Duration <= TimeSpan.Zero) return;

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            e.Handled = true;

            double viewportW = Math.Max(1, TimelineScrollViewer.ActualWidth);
            double oldWidth = Math.Max(viewportW, ZoomContentHost.ActualWidth);
            double mouseXInViewport = e.GetPosition(TimelineScrollViewer).X;
            double oldScroll = TimelineScrollViewer.HorizontalOffset;
            double anchorFraction = Math.Clamp((oldScroll + mouseXInViewport) / oldWidth, 0, 1);

            double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
            double nextZoom = Math.Clamp(ZoomLevel * factor, MinZoom, MaxZoom);
            if (Math.Abs(nextZoom - 1.0) < 0.08) nextZoom = 1.0;

            ZoomLevel = nextZoom;

            if (nextZoom > 1.001)
            {
                double newWidth = Math.Max(viewportW, viewportW * nextZoom);
                double newOffset = Math.Max(0, newWidth * anchorFraction - mouseXInViewport);
                TimelineScrollViewer.ScrollToHorizontalOffset(newOffset);
            }
            return;
        }

        // When zoomed in, standard mouse wheel scrolls the timeline horizontally
        if (ZoomLevel > 1.001)
        {
            e.Handled = true;
            double nextOffset = TimelineScrollViewer.HorizontalOffset - e.Delta * 0.6;
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, nextOffset));
        }
    }

    private void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Duration <= TimeSpan.Zero) return;

        var pos = e.GetPosition(TrackHost);
        var clickedTime = PositionFromX(pos.X);

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

        _isDraggingPlayhead = true;
        TrackHost.CaptureMouse();
        SeekToX(pos.X);
    }

    private void Track_MouseMove(object sender, MouseEventArgs e)
    {
        if (Duration <= TimeSpan.Zero) return;

        if (_draggingHandleSegmentId is Guid segId)
        {
            var newTime = PositionFromX(e.GetPosition(TrackHost).X);
            SegmentBoundaryAdjustRequested?.Invoke(segId, _draggingStartHandle, newTime);
            return;
        }

        if (_isDraggingPlayhead)
        {
            SeekToX(e.GetPosition(TrackHost).X);
        }
    }

    private void Track_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingHandleSegmentId is not null)
        {
            _draggingHandleSegmentId = null;
            TrackHost.ReleaseMouseCapture();
            return;
        }

        if (_isDraggingPlayhead)
        {
            _isDraggingPlayhead = false;
            TrackHost.ReleaseMouseCapture();
        }
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
