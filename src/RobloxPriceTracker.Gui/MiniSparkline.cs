using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace RobloxPriceTracker.Gui;

public sealed class MiniSparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable<double>),
        typeof(MiniSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(MiniSparkline),
        new FrameworkPropertyMetadata(CreateBrush(100, 168, 255), FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        var values = Values?.Where(double.IsFinite).TakeLast(40).ToArray() ?? Array.Empty<double>();
        if (values.Length == 0)
        {
            var emptyPen = new Pen(CreateBrush(44, 56, 70), 1);
            drawingContext.DrawLine(emptyPen, new Point(2, height / 2), new Point(width - 2, height / 2));
            return;
        }

        var min = values.Min();
        var max = values.Max();
        var range = Math.Max(1d, max - min);
        const double padX = 2;
        const double padY = 3;
        var plotWidth = Math.Max(1, width - padX * 2);
        var plotHeight = Math.Max(1, height - padY * 2);

        Point Map(int index)
        {
            var x = values.Length == 1 ? width / 2 : padX + plotWidth * index / (values.Length - 1d);
            var normalized = (values[index] - min) / range;
            var y = padY + plotHeight - normalized * plotHeight;
            return new Point(x, y);
        }

        if (values.Length == 1)
        {
            drawingContext.DrawEllipse(Stroke, null, Map(0), 2.2, 2.2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var point = Map(i);
                if (i == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Stroke, 1.55), geometry);

        var last = Map(values.Length - 1);
        drawingContext.DrawEllipse(Stroke, null, last, 1.8, 1.8);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
