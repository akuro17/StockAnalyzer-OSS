using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.WilderSmoothing)]
public class CoreWilderSmoothingIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"Wilder's Smoothing ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles.Count < Period) return IndicatorResult.Success(_values);
        // First value is SMA
        decimal sum = 0;
        for (int i = 0; i < Period; i++)
        {
            sum += candles[i].Close;
        }
        decimal prev = sum / Period;
        
        for(int i=0; i<Period-1; i++) _values.Add(null);
        _values.Add(prev);

        for (int i = Period; i < candles.Count; i++)
        {
            // WSMA = (Prior WSMA * (n-1) + Current Close) / n
            decimal current = (prev * (Period - 1) + candles[i].Close) / Period;
            _values.Add(current);
            prev = current;
        }

        return IndicatorResult.Success(_values);
    }
}
