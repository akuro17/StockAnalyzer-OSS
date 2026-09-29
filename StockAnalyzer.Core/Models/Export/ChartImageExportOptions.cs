using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Core.Models.Export;

/// <summary>
/// Options configuring chart image generation and export.
/// </summary>
public record ChartImageExportOptions
{
    /// <summary>
    /// Target image format (PNG, JPEG, WebP).
    /// </summary>
    public ImageExportFormat Format { get; init; } = ImageExportFormat.Png;

    /// <summary>
    /// Resolution scale factor (1.0 = 100% standard, 2.0 = 200% high-DPI, 3.0 = 300%, 4.0 = 400% ultra-HD).
    /// </summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>
    /// Sizing mode preset or custom.
    /// </summary>
    public ChartExportSizeMode SizeMode { get; init; } = ChartExportSizeMode.CurrentWindow;

    /// <summary>
    /// Compression quality (1-100) for JPEG and WebP formats.
    /// </summary>
    public int Quality { get; init; } = 90;

    /// <summary>
    /// Theme mode applied during export (Current, Dark, Light).
    /// </summary>
    public ChartImageExportThemeMode ThemeMode { get; init; } = ChartImageExportThemeMode.Dark;

    /// <summary>
    /// Optional explicit ThemeColors override to support custom themes from ITemplateService.
    /// </summary>
    public ThemeColors? OverrideThemeColors { get; init; } = null;

    /// <summary>
    /// Whether to render a visual metadata banner/header at the top of the chart image.
    /// </summary>
    public bool IncludeVisualHeader { get; init; } = true;

    /// <summary>
    /// Whether to include the Symbol/Ticker in the header banner.
    /// </summary>
    public bool IncludeSymbol { get; init; } = true;

    /// <summary>
    /// Whether to include the Company Name in the header banner.
    /// </summary>
    public bool IncludeCompanyName { get; init; } = true;

    /// <summary>
    /// Whether to include the Timeframe in the header banner.
    /// </summary>
    public bool IncludeTimeframe { get; init; } = true;

    /// <summary>
    /// Whether to include the Date Range in the header banner.
    /// </summary>
    public bool IncludeDateRange { get; init; } = true;

    /// <summary>
    /// Whether to include the active Indicators summary in the header banner.
    /// </summary>
    public bool IncludeIndicators { get; init; } = true;

    /// <summary>
    /// Whether to include the application brand/watermark and timestamp in the header banner.
    /// </summary>
    public bool IncludeBrand { get; init; } = true;

    /// <summary>
    /// Whether to embed metadata into the image file itself (e.g. PNG iTXt/tEXt chunks, EXIF comments).
    /// </summary>
    public bool EmbedFileMetadata { get; init; } = true;

    /// <summary>
    /// Whether to include the crosshair cursor and price labels in the exported image. Default is false (OFF).
    /// </summary>
    public bool IncludeCrosshair { get; init; } = false;

    /// <summary>
    /// Optional custom width in pixels (if specified, overrides layout width).
    /// </summary>
    public int? CustomWidth { get; init; } = null;

    /// <summary>
    /// Optional custom height in pixels (if specified, overrides layout height).
    /// </summary>
    public int? CustomHeight { get; init; } = null;
}
