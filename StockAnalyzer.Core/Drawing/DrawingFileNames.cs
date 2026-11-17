using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>Why a ticker has no drawing file name.</summary>
public enum DrawingFileNameRefusal
{
    /// <summary>The ticker has a file name.</summary>
    None = 0,

    /// <summary>The ticker is empty or white space.</summary>
    Empty,

    /// <summary>The ticker is a synthetic expression (a formula of symbols), which is never stored as a drawing document.</summary>
    SyntheticExpression,

    /// <summary>The normalized ticker contains a character a file name or path cannot hold.</summary>
    InvalidCharacters,

    /// <summary>The normalized ticker is a Windows reserved device name (CON, NUL, COM1 ...).</summary>
    ReservedDeviceName,
}

/// <summary>
/// The one definition of the file name of a drawing document: <c>{normalized ticker}.{timeframe}.json</c> in the drawings folder
/// (the chart's repository and the read-only reader both use it).
/// </summary>
public static class DrawingFileNames
{
    // Windows reserved device names (case-insensitive); a ticker like "CON" or "NUL" would pass ticker-format validation but produce an unusable file name.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The data folder (below Data) the drawing documents live in, as <c>PathDiscovery</c> resolves it.</summary>
    public const string FolderName = "Data/Drawings";

    /// <summary>The normalized ticker and file name, or the reason there is none (then both are empty).</summary>
    public static DrawingFileNameRefusal TryCreate(string? ticker, TimeframeType timeframe, out string normalizedTicker, out string fileName)
    {
        normalizedTicker = string.Empty;
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return DrawingFileNameRefusal.Empty;
        }

        if (SyntheticSymbolParser.IsSyntheticExpression(ticker))
        {
            return DrawingFileNameRefusal.SyntheticExpression;
        }

        string normalized = TickerHelper.NormalizeTicker(ticker);
        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return DrawingFileNameRefusal.InvalidCharacters;
        }

        if (ReservedDeviceNames.Contains(normalized))
        {
            return DrawingFileNameRefusal.ReservedDeviceName;
        }

        normalizedTicker = normalized;
        fileName = $"{normalized}.{timeframe}.json";
        return DrawingFileNameRefusal.None;
    }
}
