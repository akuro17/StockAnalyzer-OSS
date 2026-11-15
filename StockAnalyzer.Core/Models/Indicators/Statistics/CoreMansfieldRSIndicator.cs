using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Mansfield Relative Strength (Stan Weinstein): percentage deviation of the date-aligned price ratio (A / B) from its <see cref="Period"/>-bar SMA.
/// Formula: <c>ratio_i = A_i / B_i</c> (null when A or B is missing or B == 0), <c>sma_i = SMA(ratio, Period)</c>,
/// <c>Main_i = (ratio_i / sma_i - 1) * 100</c> (null when <c>ratio_i</c> or <c>sma_i</c> is null or <c>sma_i == 0</c>).
/// Unit: percentage (0-centered). Causal: bar t uses bars &lt;= t only.
/// Note: <see cref="Period"/> is in bars and does not depend on chart timeframe (52 for weekly; users may set 200 etc. on daily charts).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.MansfieldRS)]
public sealed class CoreMansfieldRSIndicator : CorePairIndicatorBase
{
    public CoreMansfieldRSIndicator() : base(IndicatorType.MansfieldRS)
    {
    }

    /// <summary>
    /// SMA baseline period in bars (default 52 for weekly charts per Weinstein; users may set e.g. 200 on daily charts).
    /// </summary>
    public int Period { get; set; } = IndicatorDefaultConstants.MansfieldRsDefaultPeriod;

    public override string Name
        => CoreMansfieldRSParameter.FormatDisplayName(
            IndicatorType.MansfieldRS.GetDescription(),
            Period,
            ComparisonSymbol,
            PriceSource,
            ComparisonPriceSource);

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreMansfieldRSParameter mansfield)
        {
            mansfield.Validate();
            Period = mansfield.Period;
        }

        base.Configure(parameters);
    }

    protected override decimal? Combine(decimal a, decimal b) => b == 0m ? null : a / b;

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (Period < IndicatorDefaultConstants.DefaultPeriodMin || Period > IndicatorDefaultConstants.DefaultPeriodMax)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Period),
                $"Period must be between {IndicatorDefaultConstants.DefaultPeriodMin} and {IndicatorDefaultConstants.DefaultPeriodMax}.");
        }

        base.CalculateCore(candles);

        // Compute each bar's Period-bar baseline SMA directly over [i - Period + 1 .. i] rather than using a running add/subtract sum:
        // ratio_i = A_i / B_i carries up to 28 fractional digits, where running add/subtract accumulates 1-2 ULP drift across different history lengths.
        int count = _values.Count;
        decimal?[] ratios = new decimal?[count];
        for (int i = 0; i < count; i++)
        {
            ratios[i] = _values[i];
        }

        int nullCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (!ratios[i].HasValue)
            {
                nullCount++;
            }

            if (i >= Period && !ratios[i - Period].HasValue)
            {
                nullCount--;
            }

            decimal? ratio = ratios[i];
            if (i < Period - 1 || nullCount > 0 || !ratio.HasValue)
            {
                _values[i] = null;
                continue;
            }

            try
            {
                decimal sum = 0m;
                for (int j = i - Period + 1; j <= i; j++)
                {
                    sum += ratios[j]!.Value;
                }

                decimal baseline = sum / Period;
                _values[i] = baseline == 0m
                    ? null
                    : (ratio.Value / baseline - 1m) * IndicatorDefaultConstants.MansfieldRsPercentScale;
            }
            catch (OverflowException)
            {
                _values[i] = null;
            }
        }

        return IndicatorResult.Success(_values);
    }
}
