using Avalonia;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using System.Collections.Generic;
using System.Linq;
using System;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// マージンとパネル高さを計算するレイアウトプロバイダーの共通基底クラス。
/// サブクラスにてマージンやボリューム表示要件をオーバーライドする。
/// </summary>
public abstract class ChartLayoutProviderBase : IChartLayoutProvider
{
    protected abstract float GetMarginTop(float? customMarginTop);
    protected abstract float GetMarginBottom(float? customMarginBottom);
    protected abstract bool SupportsVolume { get; }
    protected abstract bool SupportsIndicators { get; }

    public ChartLayoutContext CreateLayout(
        Rect bounds, 
        IEnumerable<CoreIndicatorSettings>? indicators = null,
        bool showIndicators = true,
        bool isMainWindowVisible = true,
        float? customMarginTop = null, 
        float? customMarginBottom = null, 
        float? customMarginRight = null)
    {
        // FIX: Calculate actual physical margins based on custom values if provided.
        // Previously, standardMarginTop was forced here to 'prevent jitter', but this caused 
        // coordinate mismatches with the coordinate transform which uses the custom margins.
        var actualMarginTop = GetMarginTop(customMarginTop);
        var actualMarginBottom = GetMarginBottom(customMarginBottom);
        var marginLeft = ChartTheme.MarginLeft;
        var marginRight = customMarginRight ?? ChartTheme.MarginRight;

        bool showVolume = SupportsVolume && showIndicators && isMainWindowVisible;

        var layoutBaseHeight = Math.Max(0, bounds.Height - actualMarginTop - actualMarginBottom);
        var chartWidth = Math.Max(0, bounds.Width - marginLeft - marginRight);
        
        // Count panels
        int panelCount = 0;
        if (indicators != null && SupportsIndicators && showIndicators)
        {
            var standardIndicators = indicators.Where(i => i.IsEnabled && !i.IsOverlay && i.TypeEnum != IndicatorType.GranvilleLaw && i.TypeEnum != IndicatorType.VolumeProfile);
            int standalone = standardIndicators.Count(i => string.IsNullOrEmpty(i.OverlayPanelId));

            int grouped = standardIndicators
                .Where(i => !string.IsNullOrEmpty(i.OverlayPanelId))
                .Select(i => i.OverlayPanelId)
                .Distinct()
                .Count();

            panelCount = standalone + grouped;
            
            panelCount += indicators.Count(i => 
                i.IsEnabled &&
                i.TypeEnum == IndicatorType.GranvilleLaw && 
                i.ParameterObject is CoreGranvilleLawParameter p && 
                p.ShowSubWindowBar);
        }

        // Calculate heights
        double panelHeight = layoutBaseHeight * ChartTheme.PanelHeightPercentage;
        
        // If main window is hidden, indicators expand to take available space
        if (!isMainWindowVisible && panelCount > 0)
        {
             panelHeight = layoutBaseHeight / panelCount;
        }

        double totalPanelHeight = panelCount * panelHeight;
        double totalGapHeight = (panelCount > 0) ? (panelCount * ChartTheme.PanelGap) : 0;
        
        if (isMainWindowVisible && totalPanelHeight + totalGapHeight > layoutBaseHeight * ChartTheme.MaxPanelHeightRatio)
        {
            double maxTotalPanelSpace = (layoutBaseHeight * ChartTheme.MaxPanelHeightRatio) - totalGapHeight;
            if (maxTotalPanelSpace < 0) maxTotalPanelSpace = 0; 
            
            panelHeight = maxTotalPanelSpace / panelCount;
            totalPanelHeight = panelCount * panelHeight;
        }

        // Step 68-1-4: Explicit Layout Collapse
        // Total available height excluding panels and their gaps
        double remainingHeight = Math.Max(0, layoutBaseHeight - totalPanelHeight - totalGapHeight);
        
        double standardMainHeight;
        double volumeHeight;

        if (!isMainWindowVisible)
        {
             standardMainHeight = 0;
             volumeHeight = 0;
        }
        else if (showVolume)
        {
            standardMainHeight = remainingHeight * ChartTheme.MainChartHeightRatio;
            volumeHeight = remainingHeight * ChartTheme.VolumeChartHeightRatio;
        }
        else
        {
            // Case 1: Indicators OFF -> Main takes full space, Volume = 0
            // Case 2: FX/Compact Chart -> Volume = 0
            standardMainHeight = remainingHeight;
            volumeHeight = 0;
        }

        // 固定物理座標
        double actualMainY = bounds.Y + actualMarginTop;
        double volumeY = actualMainY + standardMainHeight;
        double actualMainHeight = Math.Max(0, volumeY - actualMainY);

        var chartArea = new Rect(bounds.X + marginLeft, actualMainY, chartWidth, actualMainHeight);
        var volumeArea = new Rect(bounds.X + marginLeft, volumeY, chartWidth, volumeHeight);

        var panelAreas = new List<Rect>();
        double currentY = volumeY + volumeHeight + (panelCount > 0 ? ChartTheme.PanelGap : 0); 

        for (int i = 0; i < panelCount; i++)
        {
            panelAreas.Add(new Rect(bounds.X + marginLeft, currentY, chartWidth, panelHeight));
            currentY += panelHeight + ChartTheme.PanelGap;
        }

        return new ChartLayoutContext(
            bounds, 
            chartArea, 
            volumeArea, 
            panelAreas,
            actualMarginTop, 
            actualMarginBottom, 
            marginLeft,
            marginRight);
    }
}
