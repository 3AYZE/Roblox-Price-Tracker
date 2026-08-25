using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RobloxPriceTracker.Infrastructure;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace RobloxPriceTracker.Gui;

public sealed class PriceChart : FrameworkElement
{
    private IReadOnlyList<JsonFileRepository.PriceHistoryEntry> _points = Array.Empty<JsonFileRepository.PriceHistoryEntry>();
    private long? _targetPrice;
    private int? _hoverIndex;

    public PriceChart()
    {
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    public void SetPoints(IEnumerable<JsonFileRepository.PriceHistoryEntry> points)
    {
        _points = points
            .Where(x => x.Price is > 0)
            .OrderBy(x => x.ObservedAtUtc)
            .TakeLast(500)
            .ToArray();
        _hoverIndex = null;
        InvalidateVisual();
    }

    public void SetTargetPrice(long? targetPrice)
    {
        _targetPrice = targetPrice is > 0 ? targetPrice : null;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_points.Count == 0)
        {
            _hoverIndex = null;
            return;
        }

        const double left = 62;
        const double right = 22;
        var plotWidth = Math.Max(1, ActualWidth - left - right);
        var mouseX = Math.Clamp(e.GetPosition(this).X, left, left + plotWidth);
        var ratio = (mouseX - left) / plotWidth;
        var start = _points[0].ObservedAtUtc;
        var end = _points[^1].ObservedAtUtc;
        var targetTicks = start.UtcTicks + (long)((end.UtcTicks - start.UtcTicks) * ratio);

        var bestIndex = 0;
        var bestDistance = long.MaxValue;
        for (var i = 0; i < _points.Count; i++)
        {
            var distance = Math.Abs(_points[i].ObservedAtUtc.UtcTicks - targetTicks);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestIndex = i;
        }

        if (_hoverIndex != bestIndex)
        {
            _hoverIndex = bestIndex;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex is null) return;
        _hoverIndex = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        var background = FrozenBrush(10, 15, 21);
        drawingContext.DrawRectangle(background, null, new Rect(0, 0, width, height));

        const double left = 62;
        const double right = 22;
        const double top = 18;
        const double bottom = 34;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);

        var gridPen = new Pen(FrozenBrush(28, 38, 49), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight * i / 4.0;
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
        }

        if (_points.Count == 0)
        {
            DrawText(drawingContext, "Price history will appear after successful checks.", new Point(left, top + plotHeight / 2 - 8), 11, Color.FromRgb(111, 124, 141));
            return;
        }

        var prices = _points.Select(x => x.Price!.Value).ToList();
        if (_targetPrice is > 0) prices.Add(_targetPrice.Value);

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
            DrawText(drawingContext, FormatCompact(price), new Point(6, top + plotHeight * i / 4.0 - 8), 9.5, Color.FromRgb(111, 124, 141));
        }

        var start = _points[0].ObservedAtUtc;
        var end = _points[^1].ObservedAtUtc;
        var totalSeconds = Math.Max(1, (end - start).TotalSeconds);
        var range = Math.Max(1, max - min);

        Point MapPoint(JsonFileRepository.PriceHistoryEntry item)
        {
            var x = left + ((item.ObservedAtUtc - start).TotalSeconds / totalSeconds) * plotWidth;
            var normalized = (item.Price!.Value - min) / (double)range;
            var y = top + plotHeight - normalized * plotHeight;
            return new Point(x, y);
        }

        if (_targetPrice is > 0)
        {
            var targetNormalized = (_targetPrice.Value - min) / (double)range;
            var targetY = top + plotHeight - targetNormalized * plotHeight;
            var targetBrush = FrozenBrush(240, 185, 11);
            var targetPen = new Pen(targetBrush, 1.05) { DashStyle = DashStyles.Dash };
            drawingContext.DrawLine(targetPen, new Point(left, targetY), new Point(left + plotWidth, targetY));
            var targetLabel = CreateText($"TARGET {FormatCompact(_targetPrice.Value)}", 8.5, Color.FromRgb(240, 185, 11));
            drawingContext.DrawRectangle(background, null, new Rect(left + 5, targetY - 9, targetLabel.Width + 9, 18));
            drawingContext.DrawText(targetLabel, new Point(left + 9, targetY - 7));
        }

        var firstPrice = _points[0].Price!.Value;
        var lastPrice = _points[^1].Price!.Value;
        var lineColor = lastPrice switch
        {
            var value when value > firstPrice => Color.FromRgb(0, 192, 118),
            var value when value < firstPrice => Color.FromRgb(246, 70, 93),
            _ => Color.FromRgb(76, 141, 255)
        };
        var lineBrush = FrozenBrush(lineColor.R, lineColor.G, lineColor.B);

        if (_points.Count >= 2)
        {
            var area = new StreamGeometry();
            using (var context = area.Open())
            {
                var first = MapPoint(_points[0]);
                context.BeginFigure(new Point(first.X, top + plotHeight), true, true);
                context.LineTo(first, true, false);
                for (var i = 1; i < _points.Count; i++) context.LineTo(MapPoint(_points[i]), true, false);
                var last = MapPoint(_points[^1]);
                context.LineTo(new Point(last.X, top + plotHeight), true, false);
            }
            area.Freeze();
            var areaBrush = new SolidColorBrush(Color.FromArgb(26, lineColor.R, lineColor.G, lineColor.B));
            areaBrush.Freeze();
            drawingContext.DrawGeometry(areaBrush, null, area);

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (var i = 0; i < _points.Count; i++)
                {
                    var point = MapPoint(_points[i]);
                    if (i == 0) context.BeginFigure(point, false, false);
                    else context.LineTo(point, true, false);
                }
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, new Pen(lineBrush, 1.8), geometry);
        }
        else
        {
            var point = MapPoint(_points[0]);
            drawingContext.DrawEllipse(lineBrush, null, point, 3.5, 3.5);
        }

        DrawText(drawingContext, start.ToLocalTime().ToString("MMM d, h:mm tt"), new Point(left, top + plotHeight + 9), 8.5, Color.FromRgb(96, 108, 123));
        var endText = end.ToLocalTime().ToString("MMM d, h:mm tt");
        var formattedEnd = CreateText(endText, 8.5, Color.FromRgb(96, 108, 123));
        drawingContext.DrawText(formattedEnd, new Point(left + plotWidth - formattedEnd.Width, top + plotHeight + 9));

        DrawHover(drawingContext, MapPoint, left, top, plotWidth, plotHeight, width);
    }

    private void DrawHover(
        DrawingContext drawingContext,
        Func<JsonFileRepository.PriceHistoryEntry, Point> mapPoint,
        double left,
        double top,
        double plotWidth,
        double plotHeight,
        double width)
    {
        if (_hoverIndex is not { } hover || hover < 0 || hover >= _points.Count) return;
        var entry = _points[hover];
        var point = mapPoint(entry);
        var crossPen = new Pen(FrozenBrush(76, 91, 110), 1) { DashStyle = DashStyles.Dot };
        drawingContext.DrawLine(crossPen, new Point(point.X, top), new Point(point.X, top + plotHeight));
        drawingContext.DrawEllipse(FrozenBrush(238, 244, 249), new Pen(FrozenBrush(20, 28, 38), 1), point, 3.2, 3.2);

        var priceText = CreateText($"{entry.Price!.Value:N0} R$", 11, Color.FromRgb(240, 244, 248));
        var timeText = CreateText(entry.ObservedAtUtc.ToLocalTime().ToString("MMM d · h:mm:ss tt"), 8.5, Color.FromRgb(142, 154, 169));
        var boxWidth = Math.Max(priceText.Width, timeText.Width) + 20;
        const double boxHeight = 47;
        var desiredX = point.X + 10;
        var boxX = desiredX + boxWidth > width - 8 ? point.X - boxWidth - 10 : desiredX;
        boxX = Math.Max(left, Math.Min(boxX, left + plotWidth - boxWidth));
        var boxY = Math.Max(top + 5, point.Y - boxHeight - 10);

        var rect = new Rect(boxX, boxY, boxWidth, boxHeight);
        drawingContext.DrawRoundedRectangle(FrozenBrush(15, 22, 30), new Pen(FrozenBrush(47, 61, 78), 1), rect, 4, 4);
        drawingContext.DrawText(priceText, new Point(boxX + 10, boxY + 7));
        drawingContext.DrawText(timeText, new Point(boxX + 10, boxY + 25));
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

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
