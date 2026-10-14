using System;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class DrawingFontHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(DrawingFontHelper.DefaultFontDisplayName)]
    public void ResolveTypeface_NullOrEmptyOrWhitespaceOrDefault_ReturnsDefaultTypeface(string? input)
    {
        var result = DrawingFontHelper.ResolveTypeface(input);
        Assert.NotNull(result);
        Assert.Same(DrawingThemeContext.DefaultTextTypeface, result);
    }

    [Fact]
    public void ResolveTypeface_UnknownFontName_FallbacksToDefaultTypeface()
    {
        var result = DrawingFontHelper.ResolveTypeface("NonExistentFont_XYZ_12345");
        Assert.NotNull(result);
        Assert.Same(DrawingThemeContext.DefaultTextTypeface, result);
    }

    [Fact]
    public void ResolveTypeface_ValidFont_ReturnsCachedSameReference()
    {
        // Pick an existing font family from available list if one exists beyond "(Default)"
        var sampleFont = DrawingFontHelper.AvailableFontFamilies.FirstOrDefault(f => f != DrawingFontHelper.DefaultFontDisplayName);
        if (sampleFont == null) return; // In headless environment where no fonts exist

        DrawingFontHelper.ClearCacheForTesting();

        var tf1 = DrawingFontHelper.ResolveTypeface(sampleFont);
        var tf2 = DrawingFontHelper.ResolveTypeface(sampleFont);

        Assert.NotNull(tf1);
        Assert.Same(tf1, tf2);
    }

    [Fact]
    public void AvailableFontFamilies_Index0IsDefault_AndHasNoDuplicates()
    {
        var families = DrawingFontHelper.AvailableFontFamilies;
        Assert.NotEmpty(families);
        Assert.Equal(DrawingFontHelper.DefaultFontDisplayName, families[0]);

        // No nulls or empty strings
        Assert.All(families, f => Assert.False(string.IsNullOrWhiteSpace(f)));

        // No duplicates
        var distinctCount = families.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(families.Count, distinctCount);
    }
}
