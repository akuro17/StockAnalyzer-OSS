using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Services.Export;

/// <summary>
/// Utility for generating safe, OS-compliant file names for exported chart images.
/// </summary>
public static class ChartExportFileNameGenerator
{
    private static readonly Regex InvalidCharRegex = new(@"[\\/:*?""<>|\p{C}]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Generates a standardized, sanitized file name in the format: {Ticker}-{CompanyName}-{Date}.{ext}
    /// </summary>
    public static string GenerateFileName(string symbol, string? companyName, DateTime date, ImageExportFormat format)
    {
        var cleanSymbol = SanitizeComponent(symbol, "CHART");
        var cleanCompany = string.IsNullOrWhiteSpace(companyName) ? string.Empty : SanitizeComponent(companyName, string.Empty);

        string baseName;
        if (string.IsNullOrEmpty(cleanCompany) || string.Equals(cleanSymbol, cleanCompany, StringComparison.OrdinalIgnoreCase))
        {
            baseName = $"{cleanSymbol}-{date:yyyy-MM-dd}";
        }
        else
        {
            baseName = $"{cleanSymbol}-{cleanCompany}-{date:yyyy-MM-dd}";
        }

        baseName = EnsureNotReserved(baseName);
        var extension = GetFileExtension(format);

        return $"{baseName}{extension}";
    }

    /// <summary>
    /// Sanitizes an arbitrary string so it can be safely used as a file name component.
    /// </summary>
    public static string SanitizeComponent(string raw, string fallback = "chart")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        // Replace invalid chars and control characters with underscore
        var sanitized = InvalidCharRegex.Replace(raw.Trim(), "_");

        // Replace whitespace with underscore
        sanitized = WhitespaceRegex.Replace(sanitized, "_");

        // Replace any remaining Path.GetInvalidFileNameChars()
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(sanitized.Length);
        foreach (var c in sanitized)
        {
            if (Array.IndexOf(invalidChars, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString().Trim('.', ' ', '_');
        return string.IsNullOrEmpty(result) ? fallback : result;
    }

    /// <summary>
    /// Checks if the name matches a Windows reserved file name and prefixes it if necessary.
    /// </summary>
    public static string EnsureNotReserved(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "chart";
        }

        return WindowsDeviceFileName.IsReserved(name) ? $"_{name}" : name;
    }

    /// <summary>
    /// Returns the standard file extension for the given format.
    /// </summary>
    public static string GetFileExtension(ImageExportFormat format) => format switch
    {
        ImageExportFormat.Png => ".png",
        ImageExportFormat.Jpeg => ".jpg",
        ImageExportFormat.Webp => ".webp",
        _ => ".png"
    };

    /// <summary>
    /// Returns the default export directory (Data\Notes\Attachments) using PathDiscovery SSoT and ensures it exists.
    /// </summary>
    public static string GetDefaultExportDirectory()
    {
        try
        {
            var notesDir = StockAnalyzer.Core.Common.PathDiscovery.ResolveDataPath(null, "Data/Notes");
            var targetDir = Path.Combine(notesDir, "Attachments");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
            return targetDir;
        }
        catch
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return string.IsNullOrEmpty(fallback) ? Directory.GetCurrentDirectory() : fallback;
        }
    }
}
