using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Core indicator implementation for the Fractal Dimension Index (FDI).
/// Categorized under Volatility/Market Regime as an independent subpanel oscillator.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.FDI)]
public class CoreFDIIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.FdiPeriod;
    public int SmoothingPeriod { get; set; } = IndicatorDefaultConstants.FdiSmoothingPeriod;

    public override string Name => SmoothingPeriod > 0
        ? $"FDI ({Period}, {SmoothingPeriod})"
        : $"FDI ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreFDIParameter p)
        {
            Period = p.Period;
            SmoothingPeriod = p.SmoothingPeriod;
        }
        else if (parameters is CoreSmaParameter sp)
        {
            Period = sp.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

        var fdiValues = IndicatorCalculationHelper.CalculateFractalDimensionIndex(candles, Period, SmoothingPeriod);

        _values.Clear();
        _values.AddRange(fdiValues);
        return IndicatorResult.Success(_values);
    }
}
