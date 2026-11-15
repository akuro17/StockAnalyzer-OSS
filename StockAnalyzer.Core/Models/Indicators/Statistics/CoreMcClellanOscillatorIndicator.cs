using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// McClellan Oscillator across a workspace ticker universe, with companion Summation Index and cumulative A/D Line outputs.
/// Output unit: issue count dimension; sub-panel with zero baseline. SummationIndex and AdLine are suppressed from chart rendering
/// via <see cref="IndicatorChartSuppression"/> (their scales differ greatly from the oscillator) and shown in DataWindow / screener output.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.McClellanOscillator)]
public sealed class CoreMcClellanOscillatorIndicator : CoreIndicatorBase, IUniverseBreadthIndicator
{
    public const string SummationIndexSeriesName = nameof(SummationIndex);
    public const string AdLineSeriesName = nameof(AdLine);

    private MarketBreadthResult? _breadthResult;
    private TimeFrame _breadthTimeFrame = TimeFrame.D1;

    public string UniverseGroupId { get; set; } = string.Empty;
    public int FastPeriod { get; set; } = IndicatorDefaultConstants.McClellanFastDefaultPeriod;
    public int SlowPeriod { get; set; } = IndicatorDefaultConstants.McClellanSlowDefaultPeriod;

    public List<decimal?> SummationIndex { get; } = new();
    public List<decimal?> AdLine { get; } = new();

    public override bool IsOverlay => false;

    public override string Name => $"{IndicatorType.McClellanOscillator.GetDescription()} ({FastPeriod}, {SlowPeriod})";

    public MarketBreadthParameters BreadthParameters => new()
    {
        McClellanFast = FastPeriod,
        McClellanSlow = SlowPeriod,
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
        if (parameters is CoreMcClellanOscillatorParameter p)
        {
            UniverseGroupId = p.UniverseGroupId;
            FastPeriod = p.FastPeriod;
            SlowPeriod = p.SlowPeriod;
        }
    }

    public override CoreIndicatorSettings GetDefaultSettings()
    {
        CoreIndicatorSettings settings = base.GetDefaultSettings();
        settings.IncludeZeroBaseline = true;
        return settings;
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        MarketBreadthDateAligner.ValidateUniverseGroupId(UniverseGroupId, nameof(UniverseGroupId));
        MarketBreadthDateAligner.ValidatePeriod(FastPeriod, IndicatorDefaultConstants.McClellanFastMinPeriod, IndicatorDefaultConstants.McClellanFastMaxPeriod, nameof(FastPeriod));
        MarketBreadthDateAligner.ValidatePeriod(SlowPeriod, IndicatorDefaultConstants.McClellanSlowMinPeriod, IndicatorDefaultConstants.McClellanSlowMaxPeriod, nameof(SlowPeriod));

        int[]? mapped = MarketBreadthDateAligner.MapCandleIndices(candles, _breadthResult, UniverseGroupId, _breadthTimeFrame);
        MarketBreadthSeries? series = _breadthResult?.Series;

        MarketBreadthDateAligner.PopulateFromDoubles(_values, candles.Count, mapped, series?.McClellan ?? Array.Empty<double?>());
        MarketBreadthDateAligner.PopulateFromDoubles(SummationIndex, candles.Count, mapped, series?.SummationIndex ?? Array.Empty<double?>());
        MarketBreadthDateAligner.PopulateFromInts(AdLine, candles.Count, mapped, series?.AdLine ?? Array.Empty<int?>());

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [IndicatorResult.MainSeriesName] = _values,
            [SummationIndexSeriesName] = SummationIndex,
            [AdLineSeriesName] = AdLine
        });
    }
}
