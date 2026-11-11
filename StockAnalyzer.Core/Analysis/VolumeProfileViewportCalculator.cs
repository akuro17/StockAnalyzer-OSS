using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Analysis;

public static class VolumeProfileViewportCalculator
{
    public static VolumeProfileViewportResult Calculate(
        string requestKey,
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        TimeframeType timeframe,
        CoreVolumeProfileParameter parameter,
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
            "VolumeProfile",
            (slice, _) => VolumeAnalysis.CalculateProfile(slice, parameter.RowCount, parameter.Mode),
            bins => VolumeAnalysis.CalculateValueArea(bins),
            cancellationToken);
    }
}
