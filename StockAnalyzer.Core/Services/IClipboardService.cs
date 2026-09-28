using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Provides a platform-agnostic interface for clipboard operations.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Sets the text contents of the clipboard asynchronously.
    /// </summary>
    /// <param name="text">The text to copy to the clipboard.</param>
    Task SetTextAsync(string text);
}
