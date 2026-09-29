using StockAnalyzer.Core.Models.Parameters;
using Math = System.Math;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.ParabolicSAR)]
public class CoreParabolicSarIndicator : CoreIndicatorBase
{
    public decimal AccelerationStart { get; set; } = 0.02m;
    public decimal AccelerationMax { get; set; } = 0.2m;
    public decimal AccelerationStep { get; set; } = 0.02m;

    public override string Name => "Parabolic SAR";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreParabolicSarParameter p)
        {
            AccelerationStart = p.AccelerationStart;
            AccelerationStep = p.AccelerationStep;
            AccelerationMax = p.AccelerationMax;
        }
    }

    private readonly List<decimal?> _isUpTrendValues = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _isUpTrendValues.Clear();
        if (candles.Count < 2) return IndicatorResult.Success(_values);
        bool isUpTrend = candles[1].Close > candles[0].Close;
        decimal sar = isUpTrend ? candles[0].Low : candles[0].High;
        decimal ep = isUpTrend ? candles[0].High : candles[0].Low;
        decimal af = AccelerationStart;

        _values.Add(null);
        _isUpTrendValues.Add(null);

        for (int i = 1; i < candles.Count; i++)
        {
            decimal prevSar = sar;
            sar = prevSar + af * (ep - prevSar);

            if (isUpTrend)
            {
                if (candles[i].Low < sar)
                {
                    isUpTrend = false;
                    sar = ep;
                    ep = candles[i].Low;
                    af = AccelerationStart;
                }
                else
                {
                    if (candles[i].High > ep) { ep = candles[i].High; af = Math.Min(af + AccelerationStep, AccelerationMax); }
                    sar = Math.Min(sar, Math.Min(candles[i - 1].Low, i > 1 ? candles[i - 2].Low : candles[i - 1].Low));
                }
            }
            else
            {
                if (candles[i].High > sar)
                {
                    isUpTrend = true;
                    sar = ep;
                    ep = candles[i].High;
                    af = AccelerationStart;
                }
                else
                {
                    if (candles[i].Low < ep) { ep = candles[i].Low; af = Math.Min(af + AccelerationStep, AccelerationMax); }
                    sar = Math.Max(sar, Math.Max(candles[i - 1].High, i > 1 ? candles[i - 2].High : candles[i - 1].High));
                }
            }

            _values.Add(sar);
            _isUpTrendValues.Add(isUpTrend ? 1m : 0m);
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { "IsUpTrend", _isUpTrendValues }
        });
    }
}
