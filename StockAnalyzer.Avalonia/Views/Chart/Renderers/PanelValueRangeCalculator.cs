using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Calculates the value range (Min/Max) for indicator panels.
/// Handles fixed ranges (if specified in settings) or auto-scaling based on data.
/// </summary>
public static class PanelValueRangeCalculator
{
    private const decimal DefaultMin = 0m;
    private const decimal DefaultMax = 100m;
    private const decimal BufferRatio = 0.05m;

    /// <summary>
    /// Calculates the min and max values for the specified indicator setting.
    /// </summary>
    /// <param name="snapshot">The current data snapshot containing indicator results.</param>
    /// <param name="setting">The indicator setting definition.</param>
    /// <returns>A tuple containing (Min, Max).</returns>
    public static (decimal Min, decimal Max) Calculate(ChartDataSnapshot snapshot, CoreIndicatorSettings setting)
    {
        if (setting.MinValue.HasValue && setting.MaxValue.HasValue)
        {
            return (setting.MinValue.Value, setting.MaxValue.Value);
        }

        if (snapshot.IndicatorResults != null && 
            snapshot.IndicatorResults.TryGetValue(setting.Id, out var result) &&
            result.IsSuccessful)
        {
            decimal minVal = decimal.MaxValue;
            decimal maxVal = decimal.MinValue;
            bool hasValue = false;
            
            foreach (var seriesName in result.SeriesNames)
            {
                if (IsSignalSeries(seriesName)) continue;

                // IFFT Instantaneous Phase's "Main" series (0-360 deg) and its degree/bar-scaled
                // diagnostic series (PhaseDelta/LocalPeriod/PhaseStability) are intentionally not
                // charted (see IndicatorRenderer's matching skip); their scales must not pollute
                // the SineWave/LeadSine (-1..1) auto-range either.
                if (setting.TypeEnum == IndicatorType.IFFTInstantaneousPhase &&
                    (seriesName == IndicatorResult.MainSeriesName || seriesName == "PhaseDelta" ||
                     seriesName == "LocalPeriod" || seriesName == "PhaseStability")) continue;

                var seriesValues = result.GetSeries(seriesName);
                if (seriesValues == null) continue;
                
                foreach (var v in seriesValues)
                {
                    if (v.HasValue)
                    {
                        if (v.Value < minVal) minVal = v.Value;
                        if (v.Value > maxVal) maxVal = v.Value;
                        hasValue = true;
                    }
                }
            }
            
            if (!hasValue) 
            {
                return (0m, 1m);
            }
            
            decimal buffer = (maxVal - minVal) * BufferRatio;
            if (buffer == 0) buffer = 1m;
            
            return (minVal - buffer, maxVal + buffer);
        }

        return (DefaultMin, DefaultMax);
    }

    /// <summary>
    /// Checks whether the given series represents discrete trading signals rather than continuous indicator lines.
    /// Signal series are excluded from panel value range calculations to prevent price coordinates from polluting oscillator scales.
    /// </summary>
    public static bool IsSignalSeries(string seriesName)
    {
        return seriesName == "BullishSignals" ||
               seriesName == "BearishSignals" ||
               seriesName == "BuySignals" ||
               seriesName == "SellSignals" ||
               seriesName == "Signals";
    }

    /// <summary>
    /// Calculates the merged value range for a group of indicators sharing the same panel.
    /// The returned range covers all series from all indicators in the group.
    /// </summary>
    public static (decimal Min, decimal Max) CalculateGroup(
        ChartDataSnapshot snapshot, IEnumerable<CoreIndicatorSettings> settings)
    {
        decimal groupMin = decimal.MaxValue;
        decimal groupMax = decimal.MinValue;
        bool hasValue = false;

        foreach (var setting in settings)
        {
            var (sMin, sMax) = Calculate(snapshot, setting);
            if (sMin < groupMin) groupMin = sMin;
            if (sMax > groupMax) groupMax = sMax;
            hasValue = true;
        }

        return hasValue ? (groupMin, groupMax) : (DefaultMin, DefaultMax);
    }
}
