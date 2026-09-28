using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

/// <summary>
/// Regression coverage for the Objects layer panel row title
/// (<c>DrawingObjectItemViewModel.DisplayName</c>, which looks up localization key
/// <c>$"DrawTool_{ChartObjectType}"</c>). Two categories of ChartObjectType were found showing
/// the raw bracketed key (e.g. "[DrawTool_FibonacciArc]") instead of a friendly name: the 9
/// Fibonacci-family values and GannSquareOfNine (plus FixedRangeVolumeProfile and NurbsConicArc,
/// found via the same horizontal search) — their localization entries existed only under an
/// abbreviated key (e.g. "DrawTool_FibArc", "DrawTool_GannSquare9") that the toolbar's
/// DrawingToolCategoryService still relies on, while DisplayName's lookup uses the ChartObjectType
/// enum's *full* name. The fix added the full-name key as an additional entry (not a rename, to
/// avoid touching the toolbar's existing key usage) with the same translated text.
/// </summary>
// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class DrawingObjectDisplayNameLocalizationTests
{
    // ChartObjectType values with no concrete IChartObject implementer (never appear in the
    // Objects panel, so they don't need a DrawTool_* key): ElliottWave (superseded by
    // AutoElliottWave), MeasurementRuler (the Ruler tool never creates a persisted IChartObject),
    // General (documented fallback value).
    private static readonly HashSet<ChartObjectType> UnusedTypes = new()
    {
        ChartObjectType.ElliottWave,
        ChartObjectType.MeasurementRuler,
        ChartObjectType.General,
    };

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void AllUsedChartObjectTypes_ResolveToALocalizedDisplayName_NotARawKey(string languageCode)
    {
        LocalizationManager.Instance.Initialize(languageCode);

        var missing = new List<string>();
        foreach (ChartObjectType type in Enum.GetValues(typeof(ChartObjectType)))
        {
            if (UnusedTypes.Contains(type)) continue;

            string key = $"DrawTool_{type}";
            string resolved = LocalizationManager.Instance[key];
            if (resolved == $"[{key}]")
            {
                missing.Add(key);
            }
        }

        Assert.True(missing.Count == 0,
            $"[{languageCode}] The following DrawTool_* keys are missing (DisplayName would show the raw bracketed key): {string.Join(", ", missing)}");
    }
}
