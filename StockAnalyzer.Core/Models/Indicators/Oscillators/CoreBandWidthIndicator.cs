using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.BandWidth)]
public class CoreBandWidthIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = IndicatorDefaultConstants.BandWidthPeriod;
    public decimal StdDevMultiplier { get; set; } = IndicatorDefaultConstants.BandWidthStdDevMultiplier;

    public override string Name => $"BandWidth ({Period}, {StdDevMultiplier})";

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
            Period = IndicatorDefaultConstants.BandWidthPeriod,
            StdDevMultiplier = IndicatorDefaultConstants.BandWidthStdDevMultiplier
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
            if (middleBand[i] is not { } sma || sma == 0m)
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
            _values.Add((upperBand - lowerBand) / sma * 100m);
        }

        return IndicatorResult.Success(_values);
    }
}
