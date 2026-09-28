namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Service interface for opening files and URLs using the host operating system's default applications.
/// </summary>
public interface IExternalFileOpener
{
    /// <summary>
    /// Opens the specified file using the OS default application associated with its file extension.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the file.</param>
    /// <returns><c>true</c> if the process was successfully launched; otherwise, <c>false</c>.</returns>
    bool OpenFile(string filePath);

    /// <summary>
    /// Opens the specified web URL in the host OS default browser.
    /// </summary>
    /// <param name="url">The URL to open.</param>
    /// <returns><c>true</c> if the browser was successfully launched; otherwise, <c>false</c>.</returns>
    bool OpenUrl(string url);
}
