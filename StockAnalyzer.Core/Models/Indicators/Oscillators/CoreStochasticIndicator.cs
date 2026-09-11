using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.Stoch)]
public class CoreStochasticIndicator : CoreIndicatorBase
{
    public int KPeriod { get; set; } = 14;
    public int DPeriod { get; set; } = 3;
    public int Smooth { get; set; } = 3;

    public IReadOnlyList<decimal?> PercentK { get; private set; } = Array.Empty<decimal?>();
    public IReadOnlyList<decimal?> PercentD { get; private set; } = Array.Empty<decimal?>();

    public override string Name => $"Stochastic ({KPeriod}, {DPeriod}, {Smooth})";
    public override bool IsOverlay => false;


    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreStochasticParameter stochParam)
        {
            KPeriod = stochParam.KPeriod;
            DPeriod = stochParam.DPeriod;
            Smooth = stochParam.Smooth;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        int count = candles.Count;
        var fastK = new decimal?[count];
        var slowK = new decimal?[count];
        var slowD = new decimal?[count];

        // 1. Calculate Fast %K
        for (int i = 0; i < count; i++)
        {
            if (i < KPeriod - 1)
            {
                fastK[i] = null;
                continue;
            }

            decimal highest = decimal.MinValue;
            decimal lowest = decimal.MaxValue;
            for (int j = 0; j < KPeriod; j++)
            {
                highest = Math.Max(highest, candles[i - j].High);
                lowest = Math.Min(lowest, candles[i - j].Low);
            }

            fastK[i] = highest == lowest ? 50 : ((candles[i].Close - lowest) / (highest - lowest)) * 100;
        }

        // 2. Calculate Slow %K (Smooth Fast %K)
        for (int i = 0; i < count; i++)
        {
            if (i < (KPeriod - 1) + (Smooth - 1))
            {
                slowK[i] = null;
            }
            else
            {
                decimal sum = 0;
                bool valid = true;
                for (int j = 0; j < Smooth; j++)
                {
                    if (!fastK[i - j].HasValue) { valid = false; break; }
                    sum += fastK[i - j]!.Value;
                }
                slowK[i] = valid ? sum / Smooth : null;
            }
        }

        // 3. Calculate Slow %D (Smooth Slow %K)
        for (int i = 0; i < count; i++)
        {
            if (i < (KPeriod - 1) + (Smooth - 1) + (DPeriod - 1))
            {
                slowD[i] = null;
            }
            else
            {
                decimal sum = 0;
                bool valid = true;
                for (int j = 0; j < DPeriod; j++)
                {
                    if (!slowK[i - j].HasValue) { valid = false; break; }
                    sum += slowK[i - j]!.Value;
                }
                slowD[i] = valid ? sum / DPeriod : null;
            }
        }

        _values.Clear();
        _values.AddRange(slowK);

        // Return results for grouping (Main is skipped in DataWindow if explicit names exist)
        var results = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, slowK },
            { "Slow %K", slowK },
            { "Slow %D", slowD },
            { "PercentK", slowK },
            { "PercentD", slowD }
        };

        PercentK = slowK;
        PercentD = slowD;

        return IndicatorResult.Success(results);
    }
}
