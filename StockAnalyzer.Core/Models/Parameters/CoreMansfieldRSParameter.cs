using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameters of <see cref="StockAnalyzer.Core.Models.Indicators.Statistics.CoreMansfieldRSIndicator"/>.
/// Inherits chart/comparison symbol and price source settings from <see cref="CorePairParameterBase"/> and adds <see cref="Period"/> (bars).
/// </summary>
public sealed class CoreMansfieldRSParameter : CorePairParameterBase
{
    private int _period = IndicatorDefaultConstants.MansfieldRsDefaultPeriod;

    /// <summary>
    /// Number of bars for the SMA baseline of the price ratio (A / B).
    /// Indicators are timeframe-agnostic: default 52 matches Weinstein's weekly specification; on daily charts users may adjust (e.g. 200).
    /// </summary>
    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Period")]
    [Description("SMA baseline period in bars (52 for weekly charts per Weinstein; e.g. 200 when used on daily charts).")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override int GetRequiredWarmupBars() => Period - 1;

    public override string GetDisplayName(string indicatorType)
        => FormatDisplayName(indicatorType, Period, ComparisonSymbol, PriceSource, ComparisonPriceSource);

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(Period));
    }

    internal static string FormatDisplayName(
        string typeLabel,
        int period,
        string comparisonSymbol,
        PriceType priceSource,
        PriceType comparisonPriceSource)
    {
        var parts = new List<string>(4)
        {
            period.ToString(CultureInfo.InvariantCulture)
        };
        PairIndicatorLabel.AppendPairParts(parts, comparisonSymbol, priceSource, comparisonPriceSource);

        return $"{typeLabel} ({string.Join(", ", parts)})";
    }
}
