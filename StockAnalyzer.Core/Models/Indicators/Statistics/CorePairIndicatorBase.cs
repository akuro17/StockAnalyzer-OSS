using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Base of the two-symbol Pair Ratio (A / B) and Pair Spread (A - B) indicators. One value per chart bar:
/// A is the chart symbol read at <see cref="CoreIndicatorBase.PriceSource"/>, B is the comparison symbol read at <see cref="ComparisonPriceSource"/>
/// and matched to the chart bar by calendar date (<see cref="PriceDataHelper.ExtractDateAlignedPriceSeries"/>, no look-ahead, no fill).
/// A bar has no value where A or B is missing or the combination is undefined. Without a comparison symbol or without its candles every bar has no value
/// (there is no fallback to another series). The combination itself is the only part a derived indicator supplies (<see cref="Combine"/>).
/// </summary>
public abstract class CorePairIndicatorBase : CoreIndicatorBase, ICrossTickerIndicator
{
    private readonly IndicatorType _type;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    protected CorePairIndicatorBase(IndicatorType type)
    {
        _type = type;
    }

    public string ComparisonSymbol { get; set; } = string.Empty;

    public PriceType ComparisonPriceSource { get; set; } = PriceType.Close;

    public override string Name
        => PairIndicatorLabel.Format(_type.GetDescription(), ComparisonSymbol, PriceSource, ComparisonPriceSource);

    public override bool IsOverlay => false;

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePairParameterBase pair)
        {
            PriceSource = pair.PriceSource;
            ComparisonSymbol = pair.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = pair.ComparisonPriceSource;
        }
    }

    /// <summary>The value of one bar from A and B; null where the combination is undefined. A decimal overflow is handled by the caller (no value for that bar).</summary>
    protected abstract decimal? Combine(decimal a, decimal b);

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        int count = candles.Count;
        if (string.IsNullOrWhiteSpace(ComparisonSymbol) || _secondaryCandles == null || _secondaryCandles.Count == 0)
        {
            return FillNoValue(count);
        }

        IReadOnlyList<decimal?> seriesA = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        IReadOnlyList<decimal?> seriesB = PriceDataHelper.ExtractDateAlignedPriceSeries(candles, _secondaryCandles, ComparisonPriceSource);

        for (int i = 0; i < count; i++)
        {
            decimal? a = seriesA[i];
            decimal? b = seriesB[i];
            if (!a.HasValue || !b.HasValue)
            {
                _values.Add(null);
                continue;
            }

            try
            {
                _values.Add(Combine(a.Value, b.Value));
            }
            catch (OverflowException)
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }

    /// <summary>Indicator on indicator: a single input series has no second series to combine with, so no bar has a value.</summary>
    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        => FillNoValue(series.Count);

    private IIndicatorResult FillNoValue(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
        }

        return IndicatorResult.Success(_values);
    }
}
