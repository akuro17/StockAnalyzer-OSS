using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.RviVolatility)]
    public class CoreRviVolatilityIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 10;
        public override string Name => $"RVI Vol ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period; 
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            var stdDev = new CoreStandardDeviationIndicator { Period = Period, PriceSource = PriceSource };
            stdDev.Calculate(candles);

            decimal? prevUpEma = null;
            decimal? prevDnEma = null;
            decimal multiplier = 2m / (Period + 1);

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period)
                {
                    _values.Add(null);
                    continue;
                }

                decimal upSum = 0, dnSum = 0;
                if (stdDev.Values[i].HasValue && stdDev.Values[i-1].HasValue)
                {
                    decimal diff = stdDev.Values[i]!.Value - stdDev.Values[i-1]!.Value;
                    if (diff > 0) upSum = diff;
                    else dnSum = -diff;
                }

                if (prevUpEma == null) // First calculation
                {
                    // Need to calculate SMA for the first EMA value
                    decimal upSma = 0, dnSma = 0;
                    int count = 0;
                    for(int j=i-Period+1; j<=i; j++)
                    {
                        if(stdDev.Values[j].HasValue && stdDev.Values[j-1].HasValue)
                        {
                            decimal diffSma = stdDev.Values[j]!.Value - stdDev.Values[j-1]!.Value;
                            if (diffSma > 0) upSma += diffSma;
                            else dnSma -= diffSma;
                            count++;
                        }
                    }
                    if(count == Period) // Ensure we have a full period of data for the initial SMA
                    {
                        prevUpEma = upSma / Period;
                        prevDnEma = dnSma / Period;
                    }
                }

                if (prevUpEma.HasValue)
                {
                    prevUpEma = (upSum - prevUpEma.Value) * multiplier + prevUpEma.Value;
                    prevDnEma = (dnSum - prevDnEma!.Value) * multiplier + prevDnEma!.Value;

                    decimal total = prevUpEma.Value + prevDnEma.Value;
                    _values.Add(total == 0 ? 50 : prevUpEma.Value / total * 100);
                }
                else
                {
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
