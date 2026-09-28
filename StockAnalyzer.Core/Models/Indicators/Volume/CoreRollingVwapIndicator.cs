using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volume;

[StockAnalyzerIndicator(IndicatorType.RollingVWAP)]
public class CoreRollingVwapIndicator : CoreIndicatorBase
{
    public override string Name => $"Rolling VWAP ({Period})";
    public override bool IsOverlay => true;

    /// <summary>
    /// Price source used to compute the Rolling VWAP numerator.
    /// Defaults to Typical Price ((High + Low + Close) / 3), matching industry-standard VWAP definitions.
    /// </summary>
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

    public int Period { get; set; } = IndicatorDefaultConstants.RollingVwapPeriod;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRollingVwapParameter p)
        {
            Period = p.Period;
        }
        else if (parameters is CoreSmaParameter sma)
        {
            Period = sma.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        var vwapValues = IndicatorCalculationHelper.CalculateRollingVwap(candles, Period, PriceSource);

        _values.Clear();
        _values.AddRange(vwapValues);
        return IndicatorResult.Success(_values);
    }
}
