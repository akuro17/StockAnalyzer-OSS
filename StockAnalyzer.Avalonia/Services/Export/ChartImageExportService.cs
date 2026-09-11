using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Services.Export;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Services.Export;

/// <summary>
/// High-performance SkiaSharp-based chart image export service.
/// </summary>
public sealed class ChartImageExportService : IChartImageExportService
{
    private const float HeaderBannerHeight = 52f;
    private const int MaxDimensionLimit = 8192;
    private readonly ILogger<ChartImageExportService> _logger;

    public ChartImageExportService(ILogger<ChartImageExportService>? logger = null)
    {
        _logger = logger ?? NullLogger<ChartImageExportService>.Instance;
    }

    public Task<byte[]> RenderChartImageAsync(
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
        ChartImageMetadata metadata)
    {
        return Task.Run(() =>
        {
            var effectiveThemeManager = ResolveThemeManager(options, themeManager);
            var effectiveConfig = new ExportChartRenderConfigProxy(renderConfig, effectiveThemeManager);
            var theme = effectiveThemeManager.CurrentTheme;

            double baseWidth = layout.TotalBounds.Width > 0 ? layout.TotalBounds.Width : 800;
            double baseHeight = layout.TotalBounds.Height > 0 ? layout.TotalBounds.Height : 600;
            float headerHeight = options.IncludeVisualHeader ? HeaderBannerHeight : 0f;

            double totalLogicalWidth = options.CustomWidth.HasValue ? options.CustomWidth.Value : baseWidth;
            double totalLogicalHeight = options.CustomHeight.HasValue ? options.CustomHeight.Value : (baseHeight + headerHeight);

            float scale = Math.Clamp(options.Scale, 0.1f, 10.0f);
            int pixelWidth = (int)Math.Max(1, Math.Round(totalLogicalWidth * scale));
            int pixelHeight = (int)Math.Max(1, Math.Round(totalLogicalHeight * scale));

            // Guard against extreme dimensions (OOM defense)
            if (pixelWidth > MaxDimensionLimit || pixelHeight > MaxDimensionLimit)
            {
                double downscale = Math.Min((double)MaxDimensionLimit / pixelWidth, (double)MaxDimensionLimit / pixelHeight);
                pixelWidth = (int)Math.Max(1, Math.Round(pixelWidth * downscale));
                pixelHeight = (int)Math.Max(1, Math.Round(pixelHeight * downscale));
                scale = (float)(scale * downscale);
            }

            SKSurface? surface = null;
            try
            {
                surface = SKSurface.Create(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception creating SKSurface for dimensions {Width}x{Height}", pixelWidth, pixelHeight);
                return Array.Empty<byte>();
            }

            using (surface)
            {
                if (surface == null)
                {
                    _logger.LogError("Failed to create SKSurface for dimensions {Width}x{Height}", pixelWidth, pixelHeight);
                    return Array.Empty<byte>();
                }

                var canvas = surface.Canvas;
                canvas.Clear(ToSKColor(theme.ChartBackground));
                canvas.Scale(scale);

                if (options.IncludeVisualHeader)
                {
                    DrawVisualHeader(canvas, (float)baseWidth, HeaderBannerHeight, theme, metadata, options);
                    canvas.Save();
                    canvas.Translate(0, HeaderBannerHeight);
                }

                using (var pipeline = new ChartRenderPipeline())
                {
                    pipeline.Execute(
                        canvas,
                        layout,
                        snapshot,
                        mainRenderer,
                        effectiveConfig,
                        chartType,
                        transform,
                        objectManager,
                        rulerRenderer,
                        new global::Avalonia.Point(0, 0),
                        options.IncludeCrosshair,
                        new global::Avalonia.Rect(0, 0, layout.TotalBounds.Width, layout.TotalBounds.Height),
                        null,
                        null);
                }

                if (options.IncludeVisualHeader)
                {
                    canvas.Restore();
                }

                using var image = surface.Snapshot();
                var skFormat = options.Format switch
                {
                    ImageExportFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                    ImageExportFormat.Webp => SKEncodedImageFormat.Webp,
                    _ => SKEncodedImageFormat.Png
                };

                using var data = image.Encode(skFormat, Math.Clamp(options.Quality, 1, 100));
                if (data == null)
                {
                    _logger.LogError("Failed to encode image to format {Format}", options.Format);
                    return Array.Empty<byte>();
                }

                var rawBytes = data.ToArray();

                if (options.Format == ImageExportFormat.Png && options.EmbedFileMetadata)
                {
                    rawBytes = PngMetadataEncoder.InjectMetadata(rawBytes, metadata);
                }

                return rawBytes;
            }
        });
    }

    public Task<byte[]> RenderChartPreviewAsync(
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
        int previewMaxWidth = 480)
    {
        return Task.Run(() =>
        {
            double baseWidth = layout.TotalBounds.Width > 0 ? layout.TotalBounds.Width : 800;
            float previewScale = baseWidth > 0 ? (float)Math.Min(1.0, (double)previewMaxWidth / baseWidth) : 0.5f;

            var previewOptions = options with
            {
                Scale = previewScale,
                Quality = 75,
                EmbedFileMetadata = false
            };

            return RenderChartImageAsync(
                snapshot, layout, transform, previewOptions, themeManager,
                objectManager, mainRenderer, renderConfig, chartType, rulerRenderer, metadata);
        });
    }

    public async Task<bool> ExportToFileAsync(string filePath, byte[] imageBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath) || imageBytes == null || imageBytes.Length == 0)
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(filePath, imageBytes).ConfigureAwait(false);
            _logger.LogInformation("Successfully saved exported chart image to {FilePath} ({Length} bytes)", filePath, imageBytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save exported chart image to {FilePath}", filePath);
            return false;
        }
    }

    public async Task<bool> CopyToClipboardAsync(byte[] imageBytes, ImageExportFormat format)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            return false;
        }

        try
        {
            if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard != null)
            {
                var clipboard = desktop.MainWindow.Clipboard;
                using var stream = new MemoryStream(imageBytes);
                using var bitmap = new global::Avalonia.Media.Imaging.Bitmap(stream);

                var dataObject = new DataObject();
                dataObject.Set("image/png", imageBytes);
                await clipboard.SetDataObjectAsync(dataObject).ConfigureAwait(false);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy image to clipboard.");
        }

        return false;
    }

    private static IThemeManager ResolveThemeManager(ChartImageExportOptions options, IThemeManager fallback)
    {
        if (options.OverrideThemeColors != null)
        {
            return new ExportThemeManager(options.OverrideThemeColors, options.ThemeMode == ChartImageExportThemeMode.Light ? AppThemeMode.Light : AppThemeMode.Dark);
        }

        return options.ThemeMode switch
        {
            ChartImageExportThemeMode.Dark => new ExportThemeManager(ThemeColors.Dark, AppThemeMode.Dark),
            ChartImageExportThemeMode.Light => new ExportThemeManager(ThemeColors.Light, AppThemeMode.Light),
            _ => fallback
        };
    }

    private static SKColor ToSKColor(IndicatorColor c) => new SKColor(c.R, c.G, c.B, c.A);
    private static SKColor ToSKColor(IndicatorColor c, byte alpha) => new SKColor(c.R, c.G, c.B, alpha);

    private static void DrawVisualHeader(SKCanvas canvas, float width, float height, ThemeColors theme, ChartImageMetadata metadata, ChartImageExportOptions options)
    {
        using var paint = new SKPaint { IsAntialias = true };

        // 1. Background Bar
        paint.Style = SKPaintStyle.Fill;
        paint.Color = ToSKColor(theme.ChartBackground);
        canvas.DrawRect(0, 0, width, height, paint);

        // 2. Bottom Border
        paint.Style = SKPaintStyle.Stroke;
        paint.Color = ToSKColor(theme.GridLine, 120);
        paint.StrokeWidth = 1f;
        canvas.DrawLine(0, height, width, height, paint);

        // Reset style for text rendering
        paint.Style = SKPaintStyle.Fill;
        paint.StrokeWidth = 0;

        // 3. Left Text Elements
        // Row 1: Symbol (Bold) + CompanyName + Timeframe
        float currentX = 14f;

        if (options.IncludeSymbol && !string.IsNullOrWhiteSpace(metadata.Symbol))
        {
            using var boldTypeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
            paint.Color = ToSKColor(theme.SemanticPlus);
            paint.TextSize = 15f;
            paint.Typeface = boldTypeface;
            paint.TextAlign = SKTextAlign.Left;
            canvas.DrawText(metadata.Symbol, currentX, 21f, paint);
            currentX += paint.MeasureText(metadata.Symbol) + 8f;
            paint.Typeface = null;
        }

        if (options.IncludeCompanyName && !string.IsNullOrWhiteSpace(metadata.CompanyName))
        {
            paint.Color = ToSKColor(theme.AxisText);
            paint.TextSize = 13f;
            paint.TextAlign = SKTextAlign.Left;
            canvas.DrawText(metadata.CompanyName, currentX, 21f, paint);
            currentX += paint.MeasureText(metadata.CompanyName) + 8f;
        }

        if (options.IncludeTimeframe && !string.IsNullOrWhiteSpace(metadata.Timeframe))
        {
            paint.Color = ToSKColor(theme.AxisText, 180);
            paint.TextSize = 11f;
            paint.TextAlign = SKTextAlign.Left;
            canvas.DrawText($"[{metadata.Timeframe}]", currentX, 20f, paint);
        }

        // Row 2: Date Range
        if (options.IncludeDateRange && metadata.StartDate.HasValue && metadata.EndDate.HasValue)
        {
            paint.Color = ToSKColor(theme.AxisText, 190);
            paint.TextSize = 11f;
            paint.TextAlign = SKTextAlign.Left;
            var dateText = $"{metadata.StartDate:yyyy-MM-dd} ~ {metadata.EndDate:yyyy-MM-dd}";
            canvas.DrawText(dateText, 14f, 40f, paint);
        }

        // 4. Right Text Elements
        // Row 1: Indicators Summary
        if (options.IncludeIndicators && !string.IsNullOrWhiteSpace(metadata.IndicatorsSummary))
        {
            paint.Color = ToSKColor(theme.AxisText, 210);
            paint.TextSize = 11f;
            paint.TextAlign = SKTextAlign.Right;
            canvas.DrawText(metadata.IndicatorsSummary, width - 14f, 21f, paint);
        }

        // Row 2: App Watermark & Timestamp
        if (options.IncludeBrand)
        {
            paint.Color = ToSKColor(theme.AxisText, 140);
            paint.TextSize = 10f;
            paint.TextAlign = SKTextAlign.Right;
            var brandText = $"{metadata.ApplicationName} • {metadata.GeneratedAt:yyyy-MM-dd HH:mm}";
            canvas.DrawText(brandText, width - 14f, 40f, paint);
        }
    }
}
