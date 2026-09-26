using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Services.Export;

/// <summary>
/// Service interface for rendering and exporting chart images with high resolution, metadata, and custom themes.
/// </summary>
public interface IChartImageExportService
{
    /// <summary>
    /// Renders the chart snapshot to an image byte array according to the specified export options and metadata.
    /// </summary>
    Task<byte[]> RenderChartImageAsync(
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
        ChartImageMetadata metadata);

    /// <summary>
    /// Renders a fast, scaled-down preview image of the chart for display in dialogs.
    /// </summary>
    Task<byte[]> RenderChartPreviewAsync(
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
        int previewMaxWidth = 480);

    /// <summary>
    /// Saves the image bytes to the specified file path atomically.
    /// </summary>
    Task<bool> ExportToFileAsync(string filePath, byte[] imageBytes);

    /// <summary>
    /// Copies the image bytes to the system clipboard.
    /// </summary>
    Task<bool> CopyToClipboardAsync(byte[] imageBytes, ImageExportFormat format);
}
