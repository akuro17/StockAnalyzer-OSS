using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Attributes;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;

namespace StockAnalyzer.Core.Services;

/// <summary>Defines display aliases and their canonical indicator-result series names.</summary>
public static class IndicatorOutputSeriesResolver
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
    private static readonly HashSet<IndicatorType> DisplayAliasesBackedByMain = new()
    {
        IndicatorType.ADX,
        IndicatorType.BB,
        IndicatorType.Donchian,
        IndicatorType.ElderRay,
        IndicatorType.FisherTransform,
        IndicatorType.Keltner,
        IndicatorType.MurreyMath,
        IndicatorType.PrimeNumberBands,
        IndicatorType.RVI,
        IndicatorType.SSAAnomaly,
        IndicatorType.SSAResidualBand,
        IndicatorType.SSASNR,
        IndicatorType.ThreeLineBreakSignal,
        IndicatorType.Vortex,
    };

    public static IReadOnlyList<string> GetDisplayNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null)
    {
        ICoreIndicator? indicator = (indicatorFactory ?? IndicatorFactory.Default).Create(type);
        if (indicator is null) return new[] { type.ToString() };

        (List<string> names, _) = Inspect(type, indicator);
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    public static string Normalize(
        IndicatorType type,
        string? outputName,
        PriceType? priceSource = null,
        IIndicatorFactory? indicatorFactory = null)
    {
        if (string.IsNullOrWhiteSpace(outputName) ||
            string.Equals(outputName, IndicatorResult.MainSeriesName, StringComparison.Ordinal) ||
            DisplayAliasesBackedByMain.Contains(type) ||
            (type == IndicatorType.Price && priceSource is { } source &&
             string.Equals(outputName, source.ToString(), StringComparison.Ordinal)))
        {
            return IndicatorResult.MainSeriesName;
        }

        ICoreIndicator? indicator = (indicatorFactory ?? IndicatorFactory.Default).Create(type);
        if (indicator is null) return outputName;

        (_, HashSet<string> mainAliases) = Inspect(type, indicator);
        return mainAliases.Contains(outputName) ? IndicatorResult.MainSeriesName : outputName;
    }

    private static (List<string> Names, HashSet<string> MainAliases) Inspect(IndicatorType type, ICoreIndicator indicator)
    {
        var names = new List<string>();
        var mainAliases = new HashSet<string>(StringComparer.Ordinal);
        PropertyInfo[] properties = PropertyCache.GetOrAdd(
            indicator.GetType(),
            static runtimeType => runtimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance));

        PropertyInfo? mainMatchingProperty = null;
        foreach (PropertyInfo property in properties)
        {
            if (Attribute.IsDefined(property, typeof(IndicatorResultIgnoreAttribute)) ||
                property.Name == nameof(ICoreIndicator.Values) ||
                property.Name is "BullishSignals" or "BearishSignals" or "BuySignals" or "SellSignals" ||
                property.Name.EndsWith("Signals", StringComparison.OrdinalIgnoreCase) ||
                !typeof(IEnumerable<decimal?>).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }

            if (indicator.Values is not null && mainMatchingProperty is null &&
                ReferenceEquals(property.GetValue(indicator), indicator.Values))
            {
                mainMatchingProperty = property;
                mainAliases.Add(property.Name);
            }

            if (!names.Contains(property.Name, StringComparer.Ordinal)) names.Add(property.Name);
        }

        if (mainMatchingProperty is null && names.Count == 0 && indicator.Values is not null)
        {
            names.Add(type.ToString());
            mainAliases.Add(type.ToString());
        }

        AddFriendlyMainAlias(type, names, mainAliases);
        if (DisplayAliasesBackedByMain.Contains(type)) mainAliases.UnionWith(names);
        if (names.Count == 0)
        {
            names.Add(type.ToString());
            mainAliases.Add(type.ToString());
        }

        return (names, mainAliases);
    }

    private static void AddFriendlyMainAlias(IndicatorType type, List<string> names, HashSet<string> mainAliases)
    {
        string? alias = type switch
        {
            IndicatorType.IFFTInstantaneousPhase => CoreIfftInstantaneousPhaseIndicator.ScreenerPhaseAngleOutputName,
            IndicatorType.PolarPhase => CorePolarPhaseIndicator.ScreenerPhaseAngleOutputName,
            IndicatorType.PolarCycleAngularFrequency => CorePolarCycleAngularFrequencyIndicator.ScreenerCycleAngularFrequencyOutputName,
            _ => null,
        };
        if (alias is null) return;

        names.Remove(alias);
        names.Insert(0, alias);
        mainAliases.Add(alias);
    }
}
