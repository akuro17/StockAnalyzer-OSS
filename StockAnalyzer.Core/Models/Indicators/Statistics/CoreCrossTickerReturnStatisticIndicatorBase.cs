using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Base of the two-symbol statistics of the returns of the chart symbol A and a comparison symbol B over a window of <see cref="Period"/> return pairs (Treynor ratio, Information Ratio,
/// Tracking Error, Rolling Covariance). A is the chart symbol read at <see cref="CoreIndicatorBase.PriceSource"/>; B is the comparison symbol read at <see cref="ComparisonPriceSource"/> and matched to the
/// chart bar by calendar date (<see cref="PriceDataHelper.ExtractDateAlignedPriceSeries"/>, no look-ahead, no fill, the path of <see cref="CoreRollingBetaIndicator"/>). The returns of A and of B are built
/// independently (<see cref="ReturnSeriesWindow"/>, simple or log by <see cref="CalculationMode"/>); the window of bar i holds the returns of the bars [i - Period + 1, i] and needs ALL
/// <see cref="Period"/> pairs valid (a missing price of either symbol invalidates the return of that bar and of the next bar), so the first usable bar is <see cref="Period"/>.
/// Without a comparison symbol or without its candles every bar has no value (there is no fallback to another series). Causal: bar t uses bars &lt;= t only.
/// The statistic itself is the only part a derived indicator supplies (<see cref="Compute"/>).
/// </summary>
public abstract class CoreCrossTickerReturnStatisticIndicatorBase : CoreIndicatorBase, ICrossTickerIndicator
{
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period { get; set; }

    public string ComparisonSymbol { get; set; } = string.Empty;

    public PriceType ComparisonPriceSource { get; set; } = PriceType.Close;

    public BetaCalculationMode CalculationMode { get; set; } = BetaCalculationMode.SimpleReturn;

    public override bool IsOverlay => false;

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    /// <summary>Copies the comparison settings of a parameter object (the derived class copies its own settings).</summary>
    protected void ConfigureComparison(CoreCrossTickerReturnParameterBase parameters)
    {
        ComparisonSymbol = parameters.ComparisonSymbol?.Trim() ?? string.Empty;
        ComparisonPriceSource = parameters.ComparisonPriceSource;
        CalculationMode = parameters.CalculationMode;
    }

    /// <summary>The label suffix of the comparison: the upper-case symbol and, for log returns, ", Log".</summary>
    protected string ComparisonLabel()
    {
        string mode = CalculationMode == BetaCalculationMode.LogReturn ? ", Log" : string.Empty;
        return string.IsNullOrWhiteSpace(ComparisonSymbol) ? mode : $", {ComparisonSymbol.Trim().ToUpperInvariant()}{mode}";
    }

    /// <summary>A parameter object with the current settings; its <c>Validate()</c> decides whether the indicator calculates.</summary>
    protected abstract CoreIndicatorParameterBase CreateParameters();

    /// <summary>The statistic of one window of <see cref="Period"/> pairs, or null where it is undefined. The returns are valid and finite.</summary>
    protected abstract decimal? Compute(ReadOnlySpan<double> returnsA, ReadOnlySpan<double> returnsB);

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        try
        {
            CreateParameters().Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        int count = candles.Count;
        if (string.IsNullOrWhiteSpace(ComparisonSymbol) || _secondaryCandles == null || _secondaryCandles.Count == 0)
        {
            return FillNoValue(count);
        }

        bool logReturn = CalculationMode == BetaCalculationMode.LogReturn;
        var returnsA = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), logReturn);
        var returnsB = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractDateAlignedPriceSeries(candles, _secondaryCandles, ComparisonPriceSource), logReturn);
        for (int i = 0; i < count; i++)
        {
            decimal? value = null;
            int first = i - Period + 1;
            if (i >= Period && returnsA.IsValid(first, i) && returnsB.IsValid(first, i))
            {
                value = Compute(returnsA.Returns.AsSpan(first, Period), returnsB.Returns.AsSpan(first, Period));
            }

            _values.Add(value);
        }

        return CreateAutomaticResult();
    }

    /// <summary>Indicator on indicator: a single input series has no second series to compare with, so no bar has a value.</summary>
    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        return FillNoValue(series.Count);
    }

    private IIndicatorResult FillNoValue(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
        }

        return CreateAutomaticResult();
    }
}
