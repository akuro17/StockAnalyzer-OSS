namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// The shared body of the four block chart overlays (Renko, Point and Figure, Kagi, Three Line Break): the latest state of the block chart after every time bar, one entry per candle, from
/// <see cref="BlockChartOverlayMapper"/> (the contract of invalid bars, the anchor-fixed size, the null before the first block and the no-repaint guarantee are documented there).
/// <list type="bullet">
/// <item>Unit: price-native. Causal: the value of bar t uses bars &lt;= t only. Output length equals the candle count.</item>
/// <item><c>Main</c> (the public alias <see cref="Close"/>): the current confirmed level of the chart. <see cref="Reversal"/>: the price whose break reverses it. <c>Update</c> (Renko and Point and Figure only):
/// the price of the next same-direction step. <c>IsUpTrend</c> (1 up, 0 down) is a helper series for colouring; it is deliberately NOT a public property, so that it is never offered as an output or a feature channel.</item>
/// <item>The result carries NO <c>CustomData</c>: the chart coordinator adopts any result whose <c>CustomData</c> is a Point and Figure analysis as the analysis of the non-time-series chart, and the overlay (with its own,
/// anchor-fixed size) must never feed that chart. The overlay settings are independent of the settings of the non-time-series charts.</item>
/// </list>
/// </summary>
public abstract class CoreBlockChartOverlayIndicatorBase : CoreIndicatorBase
{
    /// <summary>The name of the Update series (Renko and Point and Figure only).</summary>
    public const string UpdateSeriesName = "Update";

    private readonly List<decimal?> _isUpTrend = new();

    public override bool IsOverlay => true;

    /// <summary>The Main series under the output name "Close" (output-name discovery lists public series properties; without this alias Main could not be chosen by name).</summary>
    public IReadOnlyList<decimal?> Close => _values;

    /// <summary>The price whose break reverses the chart (Renko: R bricks beyond the last brick, Point and Figure: R boxes back, Kagi: one reversal amount, Three Line Break: the extreme of the last lines).</summary>
    public List<decimal?> Reversal { get; } = new();

    /// <summary>The Update list of the charts that have the series; null for the others, which then produce no Update series at all.</summary>
    protected List<decimal?>? UpdateValues { get; init; }

    /// <summary>How the size is resolved, or null for a chart without a size (Three Line Break).</summary>
    protected abstract BlockChartSizingSpec? Sizing { get; }

    protected abstract IBlockChartBarEngine CreateEngine(decimal size);

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        Reversal.Clear();
        UpdateValues?.Clear();
        _isUpTrend.Clear();

        var series = BlockChartOverlayMapper.Map(candles, Sizing, CreateEngine, hasUpdate: UpdateValues is not null);
        _values.AddRange(series.Close);
        Reversal.AddRange(series.Reversal);
        UpdateValues?.AddRange(series.Update!);
        _isUpTrend.AddRange(series.IsUpTrend);

        var result = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { nameof(Reversal), Reversal },
        };
        if (UpdateValues is not null) result.Add(UpdateSeriesName, UpdateValues);
        result.Add(IndicatorSeriesNames.UpTrendFlag, _isUpTrend);

        // Explicit result: CreateAutomaticResult would also emit the Close alias as a duplicate series, and no CustomData is ever attached.
        return IndicatorResult.Success(result);
    }
}
