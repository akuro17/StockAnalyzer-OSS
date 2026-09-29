using System.Collections.Generic;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Click-to-place drawing tool behavior for Icon stamps.
/// A single click on the chart immediately creates and places the IconObject centered at the clicked coordinate.
/// </summary>
public sealed class IconBehavior : ClickToPlaceBehavior<IconObject>
{
    protected override IconObject CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        var service = DrawingThemeContext.IconDrawingService;
        var active = service?.ActiveIcon;

        if (active != null)
        {
            return new IconObject(chartPoint, active.RelativePath, active.Name);
        }

        return new IconObject(chartPoint, string.Empty, "Icon");
    }
}
