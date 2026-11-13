using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.KST)]
    public class CoreKstIndicator : CoreIndicatorBase
    {
        public override string Name => "KST";

        // ROC Periods
        public int Roc1 { get; set; } = 10;
        public int Roc2 { get; set; } = 15;
        public int Roc3 { get; set; } = 20;
        public int Roc4 { get; set; } = 30;

        // SMA Periods
        public int Sma1 { get; set; } = 10;
        public int Sma2 { get; set; } = 10;
        public int Sma3 { get; set; } = 10;
        public int Sma4 { get; set; } = 15;

        // Signal line (Pring): SMA of KST over SignalPeriod bars. Unit: oscillator (same as Main).
        public int SignalPeriod { get; set; } = IndicatorDefaultConstants.KstSignalPeriod;

        // ShowSignal exists only on CoreKstParameter: it is a chart switch read by IndicatorChartSuppression, not an input of the calculation.
        // The Signal series is always calculated and returned, so the Data Window, Screener and Backtest can use it regardless of that value.

        /// <summary>
        /// The Main series under its output name "KST". Output-name discovery (IndicatorOutputSeriesResolver) lists the public series properties,
        /// so without this alias the new Signal property would hide the main output and break saved references to the output "KST".
        /// </summary>
        public IReadOnlyList<decimal?> KST => _values;

        /// <summary>The KST signal line, one value per bar (null where it is undefined).</summary>
        public List<decimal?> Signal { get; } = new();

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreKstParameter p)
            {
                Roc1 = p.Roc1; Roc2 = p.Roc2; Roc3 = p.Roc3; Roc4 = p.Roc4;
                Sma1 = p.Sma1; Sma2 = p.Sma2; Sma3 = p.Sma3; Sma4 = p.Sma4;
                SignalPeriod = p.SignalPeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            Signal.Clear();
            if (candles == null || candles.Count == 0) return BuildResult();

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var roc1 = CalculateRoc(priceSeries, Roc1);
            var roc2 = CalculateRoc(priceSeries, Roc2);
            var roc3 = CalculateRoc(priceSeries, Roc3);
            var roc4 = CalculateRoc(priceSeries, Roc4);

            var sma1 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc1, Sma1);
            var sma2 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc2, Sma2);
            var sma3 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc3, Sma3);
            var sma4 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc4, Sma4);

            for (int i = 0; i < candles.Count; i++)
            {
                if (!sma1[i].HasValue || !sma2[i].HasValue || !sma3[i].HasValue || !sma4[i].HasValue)
                {
                    _values.Add(null);
                    continue;
                }
                _values.Add(sma1[i]!.Value + 2m * sma2[i]!.Value + 3m * sma3[i]!.Value + 4m * sma4[i]!.Value);
            }

            // Signal = SMA(KST, SignalPeriod); the result length is forced to the bar count (null padding).
            var signal = IndicatorCalculationHelper.CalculateSmaWithNulls(_values, SignalPeriod);
            for (int i = 0; i < candles.Count; i++)
            {
                Signal.Add(i < signal.Count ? signal[i] : null);
            }

            return BuildResult();
        }

        private IIndicatorResult BuildResult()
        {
            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { IndicatorResult.MainSeriesName, _values },
                { nameof(Signal), Signal },
            });
        }

        private static List<decimal?> CalculateRoc(IReadOnlyList<decimal?> priceSeries, int period)
        {
            var result = new List<decimal?>(priceSeries.Count);
            for (int i = 0; i < priceSeries.Count; i++)
            {
                if (i < period || !priceSeries[i].HasValue || !priceSeries[i - period].HasValue)
                {
                    result.Add(null);
                    continue;
                }
                decimal past = priceSeries[i - period]!.Value;
                if (past == 0m)
                {
                    result.Add(null);
                }
                else
                {
                    result.Add(((priceSeries[i]!.Value - past) / past) * 100m);
                }
            }
            return result;
        }
    }
}
