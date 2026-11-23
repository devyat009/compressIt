using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CompressIt.Controls;

public partial class CircularProgressControl : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(CircularProgressControl),
            new PropertyMetadata(0.0, OnVisualChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(CircularProgressControl),
            new PropertyMetadata(74.0, OnVisualChanged));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(CircularProgressControl),
            new PropertyMetadata(6.0, OnVisualChanged));

    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(CircularProgressControl),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(74, 222, 128))));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(CircularProgressControl),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(64, 255, 255, 255))));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public CircularProgressControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateArc();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CircularProgressControl ctrl) ctrl.UpdateArc();
    }

    private void UpdateArc()
    {
        double pct = Math.Clamp(Value, 0.0, 100.0);
        PercentLabel.Text = $"{Math.Round(pct):0}%";

        if (pct <= 0.01)
        {
            ProgressArcPath.Data = Geometry.Empty;
            return;
        }

        double size = Math.Max(16, Size);
        double thickness = Math.Clamp(StrokeThickness, 1, size / 2.0 - 1);
        double radius = (size - thickness) / 2.0;
        double center = size / 2.0;

        // Avoid start == end degenerate arc at 100% by clamping angle slightly below 360.
        double angleDeg = pct >= 99.99 ? 359.99 : pct / 100.0 * 360.0;
        double startAngleRad = -Math.PI / 2.0;
        double endAngleRad = startAngleRad + angleDeg * Math.PI / 180.0;

        var startPoint = new Point(
            center + radius * Math.Cos(startAngleRad),
            center + radius * Math.Sin(startAngleRad));
        var endPoint = new Point(
            center + radius * Math.Cos(endAngleRad),
            center + radius * Math.Sin(endAngleRad));

        bool isLargeArc = angleDeg > 180.0;

        var segment = new ArcSegment(
            endPoint,
            new Size(radius, radius),
            rotationAngle: 0,
            isLargeArc: isLargeArc,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true);

        var figure = new PathFigure(startPoint, [segment], closed: false);
        ProgressArcPath.Data = new PathGeometry([figure]);
    }
}
