using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Universe market breadth sub-panel indicator projecting a selected metric from <see cref="MarketBreadthResult"/>
/// aligned onto the chart symbol's trading dates.
/// <list type="bullet">
/// <item><see cref="MarketBreadthMetricMode.AdRatio"/>: converts <c>MarketBreadthCalculator.Series.AdRatio</c> (ratio <c>sumA / sumD</c>) by <c>* 100</c> into percentage (<c>%</c>).</item>
/// <item><see cref="MarketBreadthMetricMode.PctAboveMa"/>: converts <c>MarketBreadthCalculator.Series.PctAboveMa</c> (<c>[0, 1]</c> fraction) by <c>* 100</c> into percentage <c>[0, 100]</c>.</item>
/// <item><see cref="MarketBreadthMetricMode.AdLine"/>: direct cumulative issue count.</item>
/// <item><see cref="MarketBreadthMetricMode.NetNewHighs"/>: direct net new-high issue count.</item>
/// <item><see cref="MarketBreadthMetricMode.Trin"/>: direct Arms Index ratio.</item>
/// <item><see cref="MarketBreadthMetricMode.Cvi"/>: direct cumulative net advancing volume.</item>
/// </list>
/// </summary>
[StockAnalyzerIndicator(IndicatorType.MarketBreadth)]
public sealed class CoreMarketBreadthIndicator : CoreIndicatorBase, IUniverseBreadthIndicator
{
    private MarketBreadthResult? _breadthResult;
    private TimeFrame _breadthTimeFrame = TimeFrame.D1;

    public string UniverseGroupId { get; set; } = string.Empty;
    public MarketBreadthMetricMode MetricMode { get; set; } = MarketBreadthMetricMode.AdRatio;
    public int AdRatioBars { get; set; } = IndicatorDefaultConstants.MarketBreadthAdRatioDefaultBars;
    public int MaBars { get; set; } = IndicatorDefaultConstants.MarketBreadthMaDefaultBars;
    public int HighLowBars { get; set; } = IndicatorDefaultConstants.MarketBreadthHighLowDefaultBars;

    public override bool IsOverlay => false;

    public override string Name
        => CoreMarketBreadthParameter.FormatDisplayName(IndicatorType.MarketBreadth.GetDescription(), MetricMode, AdRatioBars, MaBars, HighLowBars);

    public MarketBreadthParameters BreadthParameters => new()
    {
        AdRatioBars = AdRatioBars,
        MaBars = MaBars,
        HighLowBars = HighLowBars,
        TimeFrame = _breadthTimeFrame
    };

    public void SetUniverseBreadth(MarketBreadthResult? result, TimeFrame timeFrame)
    {
        _breadthResult = result;
        _breadthTimeFrame = timeFrame;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);
        if (parameters is CoreMarketBreadthParameter p)
        {
            UniverseGroupId = p.UniverseGroupId;
            MetricMode = p.MetricMode;
            AdRatioBars = p.AdRatioBars;
            MaBars = p.MaBars;
            HighLowBars = p.HighLowBars;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        MarketBreadthDateAligner.ValidateUniverseGroupId(UniverseGroupId, nameof(UniverseGroupId));

        if (!Enum.IsDefined(MetricMode))
        {
            throw new ArgumentOutOfRangeException(nameof(MetricMode), MetricMode, "Unsupported MarketBreadthMetricMode value.");
        }

        MarketBreadthDateAligner.ValidatePeriod(AdRatioBars, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(AdRatioBars));
        MarketBreadthDateAligner.ValidatePeriod(MaBars, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(MaBars));
        MarketBreadthDateAligner.ValidatePeriod(HighLowBars, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(HighLowBars));

        int[]? mapped = MarketBreadthDateAligner.MapCandleIndices(candles, _breadthResult, UniverseGroupId, _breadthTimeFrame);
        MarketBreadthSeries? series = _breadthResult?.Series;

        switch (MetricMode)
        {
            case MarketBreadthMetricMode.AdRatio:
                MarketBreadthDateAligner.PopulateFromDoubles(
                    _values,
                    candles.Count,
                    mapped,
                    series?.AdRatio ?? Array.Empty<double?>(),
                    IndicatorDefaultConstants.PercentScale);
                break;

            case MarketBreadthMetricMode.PctAboveMa:
                MarketBreadthDateAligner.PopulateFromDoubles(
                    _values,
                    candles.Count,
                    mapped,
                    series?.PctAboveMa ?? Array.Empty<double?>(),
                    IndicatorDefaultConstants.PercentScale);
                break;

            case MarketBreadthMetricMode.AdLine:
                MarketBreadthDateAligner.PopulateFromInts(
                    _values,
                    candles.Count,
                    mapped,
                    series?.AdLine ?? Array.Empty<int?>());
                break;

            case MarketBreadthMetricMode.NetNewHighs:
                MarketBreadthDateAligner.PopulateFromInts(
                    _values,
                    candles.Count,
                    mapped,
                    series?.NetNewHighs ?? Array.Empty<int?>());
                break;

            case MarketBreadthMetricMode.Trin:
                MarketBreadthDateAligner.PopulateFromDoubles(
                    _values,
                    candles.Count,
                    mapped,
                    series?.Trin ?? Array.Empty<double?>());
                break;

            case MarketBreadthMetricMode.Cvi:
                MarketBreadthDateAligner.PopulateFromDecimals(
                    _values,
                    candles.Count,
                    mapped,
                    series?.Cvi ?? Array.Empty<decimal?>());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(MetricMode), MetricMode, "Unsupported MarketBreadthMetricMode value.");
        }

        return IndicatorResult.Success(_values);
    }
}
