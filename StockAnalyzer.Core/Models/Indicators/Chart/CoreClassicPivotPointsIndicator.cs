using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    [StockAnalyzerIndicator(IndicatorType.ClassicPivotPoints)]
    public class CoreClassicPivotPointsIndicator : CoreIndicatorBase
    {
        public override string Name => "Classic Pivot Points";

        public const string PivotSeriesName = IndicatorResult.MainSeriesName;
        public const string R1SeriesName = "R1";
        public const string R2SeriesName = "R2";
        public const string R3SeriesName = "R3";
        public const string S1SeriesName = "S1";
        public const string S2SeriesName = "S2";
        public const string S3SeriesName = "S3";

        public IReadOnlyList<decimal?> Pivot => _values;
        public List<decimal?> R1 { get; } = new();
        public List<decimal?> R2 { get; } = new();
        public List<decimal?> R3 { get; } = new();
        public List<decimal?> S1 { get; } = new();
        public List<decimal?> S2 { get; } = new();
        public List<decimal?> S3 { get; } = new();

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // Parameterless indicator
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            R1.Clear();
            R2.Clear();
            R3.Clear();
            S1.Clear();
            S2.Clear();
            S3.Clear();

            int count = candles.Count;
            if (count == 0)
            {
                return IndicatorResult.Empty();
            }

            if (_values is List<decimal?> vList) vList.Capacity = count;
            R1.Capacity = count;
            R2.Capacity = count;
            R3.Capacity = count;
            S1.Capacity = count;
            S2.Capacity = count;
            S3.Capacity = count;

            if (count < 2)
            {
                for (int i = 0; i < count; i++)
                {
                    _values.Add(null);
                    R1.Add(null);
                    R2.Add(null);
                    R3.Add(null);
                    S1.Add(null);
                    S2.Add(null);
                    S3.Add(null);
                }
                return BuildResult();
            }

            _values.Add(null);
            R1.Add(null);
            R2.Add(null);
            R3.Add(null);
            S1.Add(null);
            S2.Add(null);
            S3.Add(null);

            for (int i = 1; i < count; i++)
            {
                var prevCandle = candles[i - 1];
                decimal high = prevCandle.High;
                decimal low = prevCandle.Low;
                decimal close = prevCandle.Close;

                // Central Pivot Point
                decimal pivot = (high + low + close) / 3m;

                // Resistance 1 & Support 1
                decimal r1 = 2m * pivot - low;
                decimal s1 = 2m * pivot - high;

                // Range
                decimal diff = high - low;

                // Resistance 2 & Support 2
                decimal r2 = pivot + diff;
                decimal s2 = pivot - diff;

                // Resistance 3 (HBOP) & Support 3 (LBOP)
                // R3 = R1 + (H - L) == 2P - 2L + H == H + 2(P - L)
                // S3 = S1 - (H - L) == 2P - 2H + L == L - 2(H - P)
                decimal r3 = r1 + diff;
                decimal s3 = s1 - diff;

                _values.Add(pivot);
                R1.Add(r1);
                R2.Add(r2);
                R3.Add(r3);
                S1.Add(s1);
                S2.Add(s2);
                S3.Add(s3);
            }

            return BuildResult();
        }

        private IIndicatorResult BuildResult()
        {
            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { PivotSeriesName, _values },
                { R1SeriesName, R1 },
                { R2SeriesName, R2 },
                { R3SeriesName, R3 },
                { S1SeriesName, S1 },
                { S2SeriesName, S2 },
                { S3SeriesName, S3 }
            });
        }
    }
}
