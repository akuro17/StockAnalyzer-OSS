namespace StockAnalyzer.Core.Models.Export;

/// <summary>
/// Defines the supported image formats for chart export.
/// </summary>
public enum ImageExportFormat
{
    /// <summary>
    /// Portable Network Graphics (lossless, supports transparency and metadata chunks).
    /// </summary>
    Png = 0,

    /// <summary>
    /// Joint Photographic Experts Group (lossy, compact, EXIF metadata).
    /// </summary>
    Jpeg = 1,

    /// <summary>
    /// WebP image format (modern high compression).
    /// </summary>
    Webp = 2
}
