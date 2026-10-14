namespace StockAnalyzer.Core.Common;

using System;
using System.Text.RegularExpressions;

/// <summary>
/// Provides utility methods to sanitize sensitive information (e.g., user home directory paths)
/// from exceptions and log messages before display or persistence.
/// </summary>
public static class ExceptionSanitizer
{
    private const int MaxSanitizedLength = 65536;
    private const string TruncationNotice = "\n[... TRUNCATED FOR SECURITY]";

    private static readonly Regex UserDirectoryRegex = new(
        @"(?i)(?:[a-zA-Z]:\\(?:Users|Documents and Settings)\\[^\\/\r\n\t]+)|(?:\/(?:Users|home)\/[^\/\r\n\t]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes raw text by masking user profile paths and truncating excessively large strings.
    /// </summary>
    /// <param name="rawText">The input text to sanitize.</param>
    /// <returns>The sanitized string, or an empty string if input is null or empty.</returns>
    public static string Sanitize(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return string.Empty;
        }

        string text = rawText;
        if (text.Length > MaxSanitizedLength)
        {
            text = text.Substring(0, MaxSanitizedLength) + TruncationNotice;
        }

        return UserDirectoryRegex.Replace(text, "<USER_DIR>");
    }

    /// <summary>
    /// Formats a startup exception into a safe, user-friendly diagnostic message
    /// that does not expose raw stack traces or private paths.
    /// </summary>
    /// <param name="ex">The startup exception.</param>
    /// <returns>A formatted diagnostic message directing user to structured logs.</returns>
    public static string FormatStartupException(Exception? ex)
    {
        if (ex == null)
        {
            return "An unknown error occurred during startup.";
        }

        var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
        var sanitizedMessage = Sanitize(ex.Message);

        return $"Application Startup Failed\n\n" +
               $"Error Type: {exceptionType}\n" +
               $"Message: {sanitizedMessage}\n\n" +
               $"For full diagnostics and stack trace, please inspect the log files in the 'logs/' directory.";
    }
}
