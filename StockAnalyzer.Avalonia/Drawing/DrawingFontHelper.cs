using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Central provider and resolver for chart drawing tool font families.
/// Provides Zero-Allocation typeface caching, deterministic fallback handling,
/// and an immutable list of available system fonts.
/// </summary>
public static class DrawingFontHelper
{
    public const string DefaultFontDisplayName = "(Default)";

    private static readonly ConcurrentDictionary<string, SKTypeface> s_typefaceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_knownFontFamilies =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AvailableFontFamilies { get; } = InitializeFontFamilies();

    private static IReadOnlyList<string> InitializeFontFamilies()
    {
        var list = new List<string> { DefaultFontDisplayName };
        var systemFamilies = SKFontManager.Default.FontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var family in systemFamilies)
        {
            s_knownFontFamilies.Add(family);
            list.Add(family);
        }

        return list.AsReadOnly();
    }

    /// <summary>
    /// Resolves the specified font family name to a cached <see cref="SKTypeface"/>.
    /// Returns <see cref="DrawingThemeContext.DefaultTextTypeface"/> if the family name is null,
    /// empty, whitespace, equal to "(Default)", or does not exist on the current OS.
    /// </summary>
    public static SKTypeface ResolveTypeface(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName) || familyName == DefaultFontDisplayName)
        {
            return DrawingThemeContext.DefaultTextTypeface;
        }

        return s_typefaceCache.GetOrAdd(familyName, name =>
        {
            if (!s_knownFontFamilies.Contains(name))
            {
                return DrawingThemeContext.DefaultTextTypeface;
            }

            var tf = SKTypeface.FromFamilyName(name);
            return tf ?? DrawingThemeContext.DefaultTextTypeface;
        });
    }

    /// <summary>
    /// Testability seam: clears the internal typeface cache.
    /// </summary>
    internal static void ClearCacheForTesting() => s_typefaceCache.Clear();
}
