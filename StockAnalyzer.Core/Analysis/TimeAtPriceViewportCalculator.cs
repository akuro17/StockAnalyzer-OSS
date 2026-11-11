using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Analysis;

public static class TimeAtPriceViewportCalculator
{
    public static VolumeProfileViewportResult Calculate(
        string requestKey,
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        TimeframeType timeframe,
        CoreTimeAtPriceParameter parameter,
        CancellationToken cancellationToken = default)
    {
        return Calculate(
            requestKey,
            candles,
            visibleStartIndex,
            visibleCount,
            timeframe,
            parameter,
            null,
            parameter?.PriceSource ?? PriceType.Close,
            cancellationToken);
    }

    public static VolumeProfileViewportResult Calculate(
        string requestKey,
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        TimeframeType timeframe,
        CoreTimeAtPriceParameter parameter,
        IReadOnlyList<decimal?>? sourceSeries,
        PriceType priceSource = PriceType.Close,
        CancellationToken cancellationToken = default)
    {
        if (parameter == null)
        {
            return ProfileViewportCalculationTemplate.Empty(requestKey);
        }

        return ProfileViewportCalculationTemplate.Execute(
            requestKey,
            candles,
            visibleStartIndex,
            visibleCount,
            parameter.Period,
            "TimeAtPrice",
            (slice, range) =>
            {
                if (sourceSeries != null && sourceSeries.Count > 0)
                {
                    var seriesSlice = new List<decimal?>(range.Count);
                    int endIndex = range.StartIndex + range.Count;
                    for (int i = range.StartIndex; i < endIndex; i++)
                    {
                        seriesSlice.Add(i < sourceSeries.Count ? sourceSeries[i] : null);
                    }

                    return TimeAtPriceAnalysis.CalculateProfile(seriesSlice, parameter.RowCount, slice);
                }

                var effectivePriceType = priceSource != PriceType.Close ? priceSource : parameter.PriceSource;
                return TimeAtPriceAnalysis.CalculateProfile(slice, parameter.RowCount, effectivePriceType);
            },
            bins => TimeAtPriceAnalysis.CalculateValueArea(bins),
            cancellationToken);
    }
}
