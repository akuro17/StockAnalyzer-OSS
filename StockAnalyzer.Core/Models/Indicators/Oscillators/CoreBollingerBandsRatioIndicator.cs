using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.BollingerBandsRatio)]
public class CoreBollingerBandsRatioIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = IndicatorDefaultConstants.BollingerBandsRatioPeriod;
    public decimal StdDevMultiplier { get; set; } = IndicatorDefaultConstants.BollingerBandsRatioStdDevMultiplier;

    public override string Name => $"BBRatio ({Period}, {StdDevMultiplier})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreBollingerBandsParameter p)
        {
            Period = p.Period;
            StdDevMultiplier = p.StdDevMultiplier;
        }
    }

    // Same parameter type and initial values as the DefaultCoreIndicatorSettings entry (the naming rule would find none).
    public override CoreIndicatorSettings GetDefaultSettings()
    {
        var settings = base.GetDefaultSettings();
        settings.ParameterObject = new CoreBollingerBandsParameter
        {
            Period = IndicatorDefaultConstants.BollingerBandsRatioPeriod,
            StdDevMultiplier = IndicatorDefaultConstants.BollingerBandsRatioStdDevMultiplier
        };
        return settings;
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series.Count == 0) return IndicatorResult.Success(_values);
        if (Period <= 0 || series.Count < Period)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, series.Count));
            return IndicatorResult.Success(_values);
        }

        var middleBand = IndicatorCalculationHelper.CalculateSmaWithNulls(series, Period);

        for (int i = 0; i < series.Count; i++)
        {
            if (middleBand[i] is not { } sma || series[i] is not { } currentPrice)
            {
                _values.Add(null);
                continue;
            }

            decimal sumOfSquares = 0m;
            int start = i - Period + 1;
            for (int j = 0; j < Period; j++)
            {
                decimal diff = series[start + j]!.Value - sma;
                sumOfSquares += diff * diff;
            }

            decimal? stdDev = IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt((double)(sumOfSquares / Period)));
            if (!stdDev.HasValue)
            {
                _values.Add(null);
                continue;
            }

            decimal upperBand = sma + StdDevMultiplier * stdDev.Value;
            decimal lowerBand = sma - StdDevMultiplier * stdDev.Value;
            decimal bandRange = upperBand - lowerBand;

            if (bandRange != 0m)
            {
                _values.Add((currentPrice - lowerBand) / bandRange);
            }
            else
            {
                // If range is 0, price is at the middle. Typically represented as 0.5
                _values.Add(0.5m);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
