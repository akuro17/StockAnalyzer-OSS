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
    private static readonly HashSet<IndicatorType> DisplayAliasesBackedByMain = new();

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

    /// <summary>
    /// Pure function that resolves a requested output series from an <see cref="IndicatorResult"/> in a fail-closed manner.
    /// Returns true and sets <paramref name="series"/> if resolved; otherwise returns false and sets <paramref name="series"/> to empty.
    /// </summary>
    public static bool TryResolveSeries(
        IIndicatorResult? result,
        IndicatorType type,
        string? requestedOutputName,
        PriceType? priceSource,
        out IReadOnlyList<decimal?> series)
    {
        if (result == null || !result.IsSuccessful)
        {
            series = Array.Empty<decimal?>();
            return false;
        }

        string normalized = Normalize(type, requestedOutputName, priceSource);
        if (string.Equals(normalized, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase))
        {
            series = result.MainValues;
            return true;
        }

        if (result.HasSeries(normalized))
        {
            series = result.GetSeries(normalized);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(requestedOutputName) && result.HasSeries(requestedOutputName.Trim()))
        {
            series = result.GetSeries(requestedOutputName.Trim());
            return true;
        }

        series = Array.Empty<decimal?>();
        return false;
    }

    /// <inheritdoc cref="TryResolveSeries(IIndicatorResult?, IndicatorType, string?, PriceType?, out IReadOnlyList{decimal?})"/>
    public static bool TryResolveSeries(
        IIndicatorResult? result,
        IndicatorType type,
        string? requestedOutputName,
        string? priceSource,
        out IReadOnlyList<decimal?> series)
    {
        PriceType? pt = null;
        if (!string.IsNullOrWhiteSpace(priceSource) && Enum.TryParse<PriceType>(priceSource, true, out var parsed))
        {
            pt = parsed;
        }
        return TryResolveSeries(result, type, requestedOutputName, pt, out series);
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
