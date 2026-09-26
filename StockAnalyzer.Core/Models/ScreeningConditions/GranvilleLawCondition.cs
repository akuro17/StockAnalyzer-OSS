using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition based on Granville's 8 Laws.
/// Filters stocks that currently exhibit the specified buy or sell signal.
/// </summary>
public class GranvilleLawCondition : IScreeningCondition
{
    private readonly GranvilleLawConditionType _targetType;
    private readonly CoreGranvilleLawParameter _parameters;

    /// <summary>
    /// Creates a new Granville's Law screening condition.
    /// </summary>
    /// <param name="targetType">The specific signal or group of signals to screen for.</param>
    /// <param name="parameters">The parameters to use for the indicator calculation. If null, default parameters are used.</param>
    public GranvilleLawCondition(GranvilleLawConditionType targetType, CoreGranvilleLawParameter? parameters = null)
    {
        _targetType = targetType;
        _parameters = parameters ?? new CoreGranvilleLawParameter();
    }

    public override string ToString()
    {
        return $"Granville's Law ({_targetType})";
    }

    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        // Need minimum required candles: MaPeriod + SlopePeriod
        if (candles == null || candles.Count < _parameters.MaPeriod + _parameters.SlopePeriod) return false;

        // Note: Screening condition takes CandleData (external model)
        // We need to map it to CoreCandleData to pass into the Core indicator
        var coreCandles = candles.Select(c => new CoreCandleData(
            c.Time, c.Open, c.High, c.Low, c.Close, c.Volume
        )).ToList();

        // 1. Instantiate the indicator
        var indicator = new CoreGranvilleLawIndicator();
        
        // 2. Configure with parameters
        indicator.Configure(_parameters);

        // 3. Calculate
        var result = indicator.Calculate(coreCandles);
        if (!result.IsSuccessful) return false;

        // 4. Check the latest signal
        var latestSignal = result.GetSeries("Signals").LastOrDefault(s => s.HasValue);

        // Evaluate target condition
        return _targetType switch
        {
            GranvilleLawConditionType.AnyBuy => latestSignal.HasValue && latestSignal.Value > 0,
            GranvilleLawConditionType.AnySell => latestSignal.HasValue && latestSignal.Value < 0,
            
            // For specific buy signals
            GranvilleLawConditionType.Buy1_NewBuy or 
            GranvilleLawConditionType.Buy2_PullbackBuy or 
            GranvilleLawConditionType.Buy3_BounceBuy or 
            GranvilleLawConditionType.Buy4_ReversalBuy =>
                latestSignal.HasValue && (int)latestSignal.Value == (int)_targetType,

            // For specific sell signals
            GranvilleLawConditionType.Sell1_NewSell or 
            GranvilleLawConditionType.Sell2_ReturnSell or 
            GranvilleLawConditionType.Sell3_RejectionSell or 
            GranvilleLawConditionType.Sell4_ReversalSell =>
                latestSignal.HasValue && (int)latestSignal.Value == (int)_targetType,

            _ => false
        };
    }
}
