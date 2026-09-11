using System;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Platform-agnostic URL display formatting (strip scheme, cap length), shared between the View-layer
/// <see cref="StockAnalyzer.Avalonia.Converters.UrlDisplayConverter"/> (XAML binding) and
/// <c>NoteTimelineViewModel.BodySegments</c> (inline body rendering) - sa_constraint_check flagged the
/// ViewModel calling the Avalonia <c>IValueConverter</c>-implementing class directly as a layering
/// violation (SA_ARCHITECTURE_RULES.md §1 Platform Agnosticism), since that class's own supertype is an
/// Avalonia-specific type even though this particular method is pure string logic.
/// </summary>
public static class UrlDisplayFormatter
{
    public const int MaxDisplayLength = 40;

    /// <summary>Strips a leading "http://" or "https://" (case-insensitive, matching how browsers/
    /// UrlExtractor themselves treat the scheme), then truncates to <paramref name="maxLength"/>
    /// characters with a trailing "..." if the remainder is still too long.</summary>
    public static string Format(string? url, int maxLength = MaxDisplayLength)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var withoutScheme = url;
        foreach (var scheme in SchemesPrefixes)
        {
            if (withoutScheme.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                withoutScheme = withoutScheme[scheme.Length..];
                break;
            }
        }

        return withoutScheme.Length > maxLength
            ? withoutScheme[..maxLength] + "..."
            : withoutScheme;
    }

    private static readonly string[] SchemesPrefixes = { "https://", "http://" };
}
