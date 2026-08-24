using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RobloxPriceTracker.Infrastructure;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace RobloxPriceTracker.Gui;

public sealed class PriceChart : FrameworkElement
{
    private IReadOnlyList<JsonFileRepository.PriceHistoryEntry> _points = Array.Empty<JsonFileRepository.PriceHistoryEntry>();
    private long? _targetPrice;

    public void SetPoints(IEnumerable<JsonFileRepository.PriceHistoryEntry> points)
    {
        _points = points
            .Where(x => x.Price is > 0)
            .OrderBy(x => x.ObservedAtUtc)
            .TakeLast(500)
            .ToArray();
        InvalidateVisual();
    }

    public void SetTargetPrice(long? targetPrice)
    {
        _targetPrice = targetPrice is > 0 ? targetPrice : null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        const double left = 62;
        const double right = 22;
        const double top = 18;
        const double bottom = 34;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(234, 236, 240)), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight * i / 4.0;
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
        }

        if (_points.Count == 0)
        {
            DrawText(drawingContext, "Price history will appear after successful checks.", new Point(left, top + plotHeight / 2 - 8), 12, Color.FromRgb(102, 112, 133));
            return;
        }

        var prices = _points.Select(x => x.Price!.Value).ToList();
        if (_targetPrice is > 0)
        {
            prices.Add(_targetPrice.Value);
        }

        var min = prices.Min();
        var max = prices.Max();
        if (min == max)
        {
            var pad = Math.Max(1, min / 100);
            min = Math.Max(0, min - pad);
            max += pad;
        }
        else
        {
            var pad = Math.Max(1, (long)Math.Ceiling((max - min) * 0.08));
            min = Math.Max(0, min - pad);
            max += pad;
        }

        for (var i = 0; i <= 4; i++)
        {
            var price = max - (max - min) * i / 4.0;
            DrawText(drawingContext, FormatCompact(price), new Point(6, top + plotHeight * i / 4.0 - 8), 10, Color.FromRgb(102, 112, 133));
        }

        var start = _points[0].ObservedAtUtc;
        var end = _points[^1].ObservedAtUtc;
        var totalSeconds = Math.Max(1, (end - start).TotalSeconds);
        var range = Math.Max(1, max - min);

        if (_targetPrice is > 0)
        {
            var targetNormalized = (_targetPrice.Value - min) / (double)range;
            var targetY = top + plotHeight - targetNormalized * plotHeight;
            var targetPen = new Pen(new SolidColorBrush(Color.FromRgb(181, 71, 8)), 1.2) { DashStyle = DashStyles.Dash };
            drawingContext.DrawLine(targetPen, new Point(left, targetY), new Point(left + plotWidth, targetY));
            var targetLabel = CreateText($"Target {FormatCompact(_targetPrice.Value)}", 9.5, Color.FromRgb(181, 71, 8));
            drawingContext.DrawRectangle(Brushes.White, null, new Rect(left + 5, targetY - 9, targetLabel.Width + 8, 18));
            drawingContext.DrawText(targetLabel, new Point(left + 9, targetY - 7));
        }

        if (_points.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (var i = 0; i < _points.Count; i++)
                {
                    var item = _points[i];
                    var x = left + ((item.ObservedAtUtc - start).TotalSeconds / totalSeconds) * plotWidth;
                    var normalized = (item.Price!.Value - min) / (double)range;
                    var y = top + plotHeight - normalized * plotHeight;
                    if (i == 0) context.BeginFigure(new Point(x, y), false, false);
                    else context.LineTo(new Point(x, y), true, false);
                }
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), 2.1), geometry);
        }
        else
        {
            var normalized = (_points[0].Price!.Value - min) / (double)range;
            var y = top + plotHeight - normalized * plotHeight;
            drawingContext.DrawEllipse(new SolidColorBrush(Color.FromRgb(37, 99, 235)), null, new Point(left + plotWidth / 2, y), 3.5, 3.5);
        }

        DrawText(drawingContext, start.ToLocalTime().ToString("MMM d, h:mm tt"), new Point(left, top + plotHeight + 9), 9.5, Color.FromRgb(102, 112, 133));
        var endText = end.ToLocalTime().ToString("MMM d, h:mm tt");
        var formattedEnd = CreateText(endText, 9.5, Color.FromRgb(102, 112, 133));
        drawingContext.DrawText(formattedEnd, new Point(left + plotWidth - formattedEnd.Width, top + plotHeight + 9));
    }

    private static string FormatCompact(double price) => price switch
    {
        >= 1_000_000 => $"{price / 1_000_000:0.##}M",
        >= 1_000 => $"{price / 1_000:0.#}K",
        _ => $"{price:0}"
    };

    private static void DrawText(DrawingContext drawingContext, string text, Point point, double size, Color color) =>
        drawingContext.DrawText(CreateText(text, size, color), point);

    private static FormattedText CreateText(string text, double size, Color color) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        size,
        new SolidColorBrush(color),
        1.0);
}
