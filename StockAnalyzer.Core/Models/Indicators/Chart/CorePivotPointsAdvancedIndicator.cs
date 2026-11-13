using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    public enum PivotPointType { Standard, Fibonacci, Woodie, Camarilla }

    /// <summary>
    /// Pivot levels of each bar from the high H, low L and close C of its source (D = H - L): by default (SourcePeriod PreviousBar) the PREVIOUS bar, otherwise the last
    /// completed ISO week or calendar month (Week / Month). Bar 0 and every bar without a completed source are null in every series.
    /// Unit: price-native. Series: Main (P), R1..R4, S1..S4; R4/S4 exist for Camarilla only (null otherwise).
    /// Standard: P=(H+L+C)/3, R1=2P-L, S1=2P-H, R2/S2=P+-D, R3=R1+D, S3=S1-D.
    /// Fibonacci: P=(H+L+C)/3, R/S = P +- 0.382D, 0.618D, 1.000D.
    /// Woodie: P=(H+L+2C)/4, R1/S1/R2/S2 as Standard, R3=H+2(P-L), S3=L-2(H-P).
    /// Camarilla: P=(H+L+C)/3, R/S = C +- D*1.1/12, D*1.1/6, D*1.1/4, D*1.1/2.
    /// </summary>
    [StockAnalyzerIndicator(IndicatorType.PivotPointsAdvanced)]
    public class CorePivotPointsAdvancedIndicator : CoreIndicatorBase
    {
        public override string Name => "Pivot Points Advanced";
        public override bool IsOverlay => true;

        public const string PivotSeriesName = IndicatorResult.MainSeriesName;
        public const string R1SeriesName = "R1";
        public const string R2SeriesName = "R2";
        public const string R3SeriesName = "R3";
        public const string R4SeriesName = "R4";
        public const string S1SeriesName = "S1";
        public const string S2SeriesName = "S2";
        public const string S3SeriesName = "S3";
        public const string S4SeriesName = "S4";

        private const decimal FibonacciRatio1 = 0.382m;
        private const decimal FibonacciRatio2 = 0.618m;
        private const decimal FibonacciRatio3 = 1.000m;
        private const decimal CamarillaFactor = 1.1m;
        private const decimal CamarillaDivisor1 = 12m;
        private const decimal CamarillaDivisor2 = 6m;
        private const decimal CamarillaDivisor3 = 4m;
        private const decimal CamarillaDivisor4 = 2m;
        private const decimal PivotAverageDivisor3 = 3m;
        private const decimal WoodieCloseWeight = 2m;
        private const decimal WoodiePivotDivisor = 4m;
        private const decimal TwoTimes = 2m;

        public PivotPointType PivotType { get; set; } = PivotPointType.Standard;

        /// <summary>
        /// Bars whose H/L/C give the levels: PreviousBar (default, the previous bar), or the last completed ISO week / calendar month
        /// (scan shared with ClassicPivotPoints). Woodie keeps using the close of the source (D-08=A), never the open of the current period.
        /// </summary>
        public PivotSourcePeriod SourcePeriod { get; set; } = PivotSourcePeriod.PreviousBar;

        public IReadOnlyList<decimal?> Pivot => _values;
        public List<decimal?> R1 { get; } = new();
        public List<decimal?> R2 { get; } = new();
        public List<decimal?> R3 { get; } = new();
        // R4/S4 are drawn on the chart and returned in the result, but they are null for every type except Camarilla, so they are not offered as
        // an output of the screener, the backtest or the registration dialogs (IndicatorResultIgnore hides them from the output catalog only).
        [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
        public List<decimal?> R4 { get; } = new();
        public List<decimal?> S1 { get; } = new();
        public List<decimal?> S2 { get; } = new();
        public List<decimal?> S3 { get; } = new();
        [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
        public List<decimal?> S4 { get; } = new();

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CorePivotPointsAdvancedParameter p)
            {
                PivotType = p.PivotType;
                SourcePeriod = p.SourcePeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            R1.Clear();
            R2.Clear();
            R3.Clear();
            R4.Clear();
            S1.Clear();
            S2.Clear();
            S3.Clear();
            S4.Clear();

            int count = candles.Count;
            if (count == 0)
            {
                return IndicatorResult.Empty();
            }

            if (_values is List<decimal?> vList) vList.Capacity = count;
            R1.Capacity = count;
            R2.Capacity = count;
            R3.Capacity = count;
            R4.Capacity = count;
            S1.Capacity = count;
            S2.Capacity = count;
            S3.Capacity = count;
            S4.Capacity = count;

            if (SourcePeriod != PivotSourcePeriod.PreviousBar)
            {
                PivotPeriodHlc?[] sources = PivotSourcePeriodScan.ScanPreviousPeriods(candles, SourcePeriod);
                for (int i = 0; i < count; i++)
                {
                    if (sources[i] is { } source)
                    {
                        AddLevels(source.High, source.Low, source.Close);
                    }
                    else
                    {
                        AddAllNull();
                    }
                }
            }
            else
            {
                AddAllNull();

                for (int i = 1; i < count; i++)
                {
                    var previous = candles[i - 1];
                    AddLevels(previous.High, previous.Low, previous.Close);
                }
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { PivotSeriesName, _values },
                { R1SeriesName, R1 },
                { R2SeriesName, R2 },
                { R3SeriesName, R3 },
                { R4SeriesName, R4 },
                { S1SeriesName, S1 },
                { S2SeriesName, S2 },
                { S3SeriesName, S3 },
                { S4SeriesName, S4 }
            });
        }

        // Appends the levels of one bar from the H/L/C of its source (the previous bar, or the previous week / month).
        private void AddLevels(decimal high, decimal low, decimal close)
        {
            decimal range = high - low;

            switch (PivotType)
            {
                case PivotPointType.Fibonacci:
                {
                    decimal p = (high + low + close) / PivotAverageDivisor3;
                    Add(p, p + FibonacciRatio1 * range, p + FibonacciRatio2 * range, p + FibonacciRatio3 * range, null,
                        p - FibonacciRatio1 * range, p - FibonacciRatio2 * range, p - FibonacciRatio3 * range, null);
                    break;
                }
                case PivotPointType.Woodie:
                {
                    decimal p = (high + low + WoodieCloseWeight * close) / WoodiePivotDivisor;
                    decimal r1 = TwoTimes * p - low;
                    decimal s1 = TwoTimes * p - high;
                    Add(p, r1, p + range, high + TwoTimes * (p - low), null,
                        s1, p - range, low - TwoTimes * (high - p), null);
                    break;
                }
                case PivotPointType.Camarilla:
                {
                    decimal p = (high + low + close) / PivotAverageDivisor3;
                    decimal unit = range * CamarillaFactor;
                    Add(p, close + unit / CamarillaDivisor1, close + unit / CamarillaDivisor2, close + unit / CamarillaDivisor3, close + unit / CamarillaDivisor4,
                        close - unit / CamarillaDivisor1, close - unit / CamarillaDivisor2, close - unit / CamarillaDivisor3, close - unit / CamarillaDivisor4);
                    break;
                }
                default:
                {
                    decimal p = (high + low + close) / PivotAverageDivisor3;
                    decimal r1 = TwoTimes * p - low;
                    decimal s1 = TwoTimes * p - high;
                    Add(p, r1, p + range, r1 + range, null,
                        s1, p - range, s1 - range, null);
                    break;
                }
            }
        }

        private void AddAllNull() => Add(null, null, null, null, null, null, null, null, null);

        private void Add(decimal? pivot, decimal? r1, decimal? r2, decimal? r3, decimal? r4, decimal? s1, decimal? s2, decimal? s3, decimal? s4)
        {
            _values.Add(pivot);
            R1.Add(r1);
            R2.Add(r2);
            R3.Add(r3);
            R4.Add(r4);
            S1.Add(s1);
            S2.Add(s2);
            S3.Add(s3);
            S4.Add(s4);
        }
    }
}
