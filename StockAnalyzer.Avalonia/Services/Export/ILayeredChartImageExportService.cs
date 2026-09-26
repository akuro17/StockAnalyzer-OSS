using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Services.Export;

/// <summary>
/// Chart image export that draws user drawings through a <see cref="LayeredDrawingScene"/>, so layer
/// visibility and layer order match the on-screen chart. When <c>layeredScene</c> is null the result is
/// identical to <see cref="IChartImageExportService"/>.
/// </summary>
public interface ILayeredChartImageExportService
{
    Task<byte[]> RenderLayeredChartImageAsync(
        ChartDataSnapshot snapshot,
        ChartLayoutContext layout,
        ICoordinateTransform transform,
        ChartImageExportOptions options,
        IThemeManager themeManager,
        ChartObjectManager objectManager,
        IChartRenderer mainRenderer,
        IChartRenderConfig renderConfig,
        ChartType chartType,
        RulerRenderer rulerRenderer,
        ChartImageMetadata metadata,
        LayeredDrawingScene? layeredScene);

    Task<byte[]> RenderLayeredChartPreviewAsync(
        ChartDataSnapshot snapshot,
        ChartLayoutContext layout,
        ICoordinateTransform transform,
        ChartImageExportOptions options,
        IThemeManager themeManager,
        ChartObjectManager objectManager,
        IChartRenderer mainRenderer,
        IChartRenderConfig renderConfig,
        ChartType chartType,
        RulerRenderer rulerRenderer,
        ChartImageMetadata metadata,
        LayeredDrawingScene? layeredScene,
        int previewMaxWidth = 480);
}
