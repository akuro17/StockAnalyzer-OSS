using Avalonia;
using StockAnalyzer.Core.Models;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Encapsulates the layout information for a chart render pass.
/// </summary>
public readonly struct ChartLayoutContext
{
    public Rect TotalBounds { get; }
    public Rect ChartArea { get; }
    public Rect VolumeArea { get; }
    public IReadOnlyList<Rect> PanelAreas { get; }
    public float MarginTop { get; }
    public float MarginBottom { get; }
    public float MarginLeft { get; }
    public float MarginRight { get; }

    public ChartLayoutContext(
        Rect totalBounds, 
        Rect chartArea, 
        Rect volumeArea, 
        IReadOnlyList<Rect> panelAreas,
        float marginTop, 
        float marginBottom, 
        float marginLeft,
        float marginRight)
    {
        TotalBounds = totalBounds;
        ChartArea = chartArea;
        VolumeArea = volumeArea;
        PanelAreas = panelAreas;
        MarginTop = marginTop;
        MarginBottom = marginBottom;
        MarginLeft = marginLeft;
        MarginRight = marginRight;
    }

}
