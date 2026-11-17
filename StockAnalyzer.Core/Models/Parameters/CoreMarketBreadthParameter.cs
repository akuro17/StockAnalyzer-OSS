using System;
using System.ComponentModel;
using System.Globalization;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Selects which universe breadth metric <see cref="StockAnalyzer.Core.Models.Indicators.Statistics.CoreMarketBreadthIndicator"/> outputs.
/// </summary>
[EnumDisplaySort]
public enum MarketBreadthMetricMode
{
    /// <summary>
    /// Advance/Decline Ratio scaled from ratio (<c>sumA / sumD</c> in <c>MarketBreadthCalculator</c>) to percentage (<c>* 100</c>). Unit: percentage.
    /// </summary>
    [Description("Advance/Decline Ratio (%)")]
    AdRatio = 0,

    /// <summary>
    /// Percentage of universe stocks closing above their <c>MaBars</c> SMA, scaled from <c>[0, 1]</c> in <c>MarketBreadthCalculator</c> to percentage (<c>* 100</c>). Unit: percentage [0, 100].
    /// </summary>
    [Description("Percentage Above Moving Average (%)")]
    PctAboveMa = 1,

    /// <summary>
    /// Cumulative Advance-Decline Line (<c>MarketBreadthCalculator.Series.AdLine</c>). Unit: cumulative issue count.
    /// </summary>
    [Description("Advance-Decline Line")]
    AdLine = 2,

    /// <summary>
    /// Net New Highs minus New Lows over <c>HighLowBars</c> (<c>MarketBreadthCalculator.Series.NetNewHighs</c>). Unit: issue count.
    /// </summary>
    [Description("Net New Highs")]
    NetNewHighs = 3,

    /// <summary>
    /// Arms Index / TRIN (<c>MarketBreadthCalculator.Series.Trin</c>). Unit: ratio.
    /// </summary>
    [Description("Arms Index (Trading Index)")]
    Trin = 4,

    /// <summary>
    /// Cumulative Volume Index (<c>MarketBreadthCalculator.Series.Cvi</c>). Unit: cumulative net advancing volume.
    /// </summary>
    [Description("Cumulative Volume Index")]
    Cvi = 5
}

/// <summary>
/// Extension helpers for <see cref="MarketBreadthMetricMode"/>.
/// </summary>
public static class MarketBreadthMetricModeExtensions
{
    /// <summary>
    /// Returns the formal description defined via <see cref="DescriptionAttribute"/> for <paramref name="mode"/>,
    /// falling back to its member name when undefined.
    /// </summary>
    public static string GetDescription(this MarketBreadthMetricMode mode)
    {
        var field = typeof(MarketBreadthMetricMode).GetField(mode.ToString());
        if (field == null) return mode.ToString();

        var attribute = (DescriptionAttribute?)Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute));
        return attribute?.Description ?? mode.ToString();
    }
}

/// <summary>
/// Parameters for <see cref="StockAnalyzer.Core.Models.Indicators.Statistics.CoreMarketBreadthIndicator"/>.
/// </summary>
public sealed class CoreMarketBreadthParameter : CoreIndicatorParameterBase, IUniverseBreadthParameter
{
    private string _universeGroupId = string.Empty;
    private MarketBreadthMetricMode _metricMode = MarketBreadthMetricMode.AdRatio;
    private int _adRatioBars = IndicatorDefaultConstants.MarketBreadthAdRatioDefaultBars;
    private int _maBars = IndicatorDefaultConstants.MarketBreadthMaDefaultBars;
    private int _highLowBars = IndicatorDefaultConstants.MarketBreadthHighLowDefaultBars;

    [DisplayName("List / Filter")]
    [Description("Workspace ticker group GUID used as the market breadth universe. Empty means no universe selected.")]
    public string UniverseGroupId
    {
        get => _universeGroupId;
        set => SetProperty(ref _universeGroupId, value ?? string.Empty);
    }

    [DisplayName("Metric Mode")]
    [Description("Breadth metric to output: AdRatio (%), PctAboveMa (% [0..100]), AdLine (issues), NetNewHighs (issues), Trin (ratio), or Cvi (cumulative volume).")]
    public MarketBreadthMetricMode MetricMode
    {
        get => _metricMode;
        set => SetProperty(ref _metricMode, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("A/D Ratio Bars")]
    [Description("Lookback window in bars for the rolling Advance/Decline Ratio.")]
    public int AdRatioBars
    {
        get => _adRatioBars;
        set => SetProperty(ref _adRatioBars, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("MA Bars")]
    [Description("SMA lookback period in bars for % Above MA.")]
    public int MaBars
    {
        get => _maBars;
        set => SetProperty(ref _maBars, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("High/Low Bars")]
    [Description("Rolling lookback window in bars for New Highs / New Lows and Net New Highs.")]
    public int HighLowBars
    {
        get => _highLowBars;
        set => SetProperty(ref _highLowBars, value);
    }

    public override string GetDisplayName(string indicatorType)
        => FormatDisplayName(indicatorType, MetricMode, AdRatioBars, MaBars, HighLowBars);

    /// <summary>
    /// The one definition of the display name, shared by this parameter and <see cref="StockAnalyzer.Core.Models.Indicators.Statistics.CoreMarketBreadthIndicator.Name"/>:
    /// <c>Type (Metric)</c>, with the one lookback that the metric uses appended as <c>Type (Metric, bars)</c> (AdRatio: <paramref name="adRatioBars"/>, PctAboveMa: <paramref name="maBars"/>,
    /// NetNewHighs: <paramref name="highLowBars"/>), so that two instances of one metric with different periods can be told apart. AdLine, Trin and Cvi have no lookback.
    /// </summary>
    internal static string FormatDisplayName(string typeLabel, MarketBreadthMetricMode metricMode, int adRatioBars, int maBars, int highLowBars)
    {
        int? lookbackBars = metricMode switch
        {
            MarketBreadthMetricMode.AdRatio => adRatioBars,
            MarketBreadthMetricMode.PctAboveMa => maBars,
            MarketBreadthMetricMode.NetNewHighs => highLowBars,
            _ => null
        };

        return lookbackBars is { } bars
            ? $"{typeLabel} ({metricMode.GetDescription()}, {bars.ToString(CultureInfo.InvariantCulture)})"
            : $"{typeLabel} ({metricMode.GetDescription()})";
    }

    public override void Validate()
    {
        MarketBreadthDateAligner.ValidateUniverseGroupId(UniverseGroupId, nameof(UniverseGroupId));

        if (!Enum.IsDefined(MetricMode))
        {
            throw new ArgumentOutOfRangeException(nameof(MetricMode), MetricMode, "Unsupported MarketBreadthMetricMode value.");
        }

        ThrowIfOutOfRange(AdRatioBars, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(AdRatioBars));
        ThrowIfOutOfRange(MaBars, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(MaBars));
        ThrowIfOutOfRange(HighLowBars, IndicatorDefaultConstants.DefaultPeriodMin, IndicatorDefaultConstants.DefaultPeriodMax, nameof(HighLowBars));
    }
}
