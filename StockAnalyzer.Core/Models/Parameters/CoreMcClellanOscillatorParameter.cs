using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameters for <see cref="StockAnalyzer.Core.Models.Indicators.Statistics.CoreMcClellanOscillatorIndicator"/>.
/// </summary>
public sealed class CoreMcClellanOscillatorParameter : CoreIndicatorParameterBase, IUniverseBreadthParameter
{
    private string _universeGroupId = string.Empty;
    private int _fastPeriod = IndicatorDefaultConstants.McClellanFastDefaultPeriod;
    private int _slowPeriod = IndicatorDefaultConstants.McClellanSlowDefaultPeriod;

    [DisplayName("List / Filter")]
    [Description("Workspace ticker group GUID used as the market breadth universe. Empty means no universe selected.")]
    public string UniverseGroupId
    {
        get => _universeGroupId;
        set => SetProperty(ref _universeGroupId, value ?? string.Empty);
    }

    [CoreParameterRange(IndicatorDefaultConstants.McClellanFastMinPeriod, IndicatorDefaultConstants.McClellanFastMaxPeriod)]
    [DisplayName("Fast Period")]
    [Description("Fast EMA period in bars for Net Advances in the McClellan Oscillator.")]
    public int FastPeriod
    {
        get => _fastPeriod;
        set => SetProperty(ref _fastPeriod, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.McClellanSlowMinPeriod, IndicatorDefaultConstants.McClellanSlowMaxPeriod)]
    [DisplayName("Slow Period")]
    [Description("Slow EMA period in bars for Net Advances in the McClellan Oscillator.")]
    public int SlowPeriod
    {
        get => _slowPeriod;
        set => SetProperty(ref _slowPeriod, value);
    }

    public override string GetDisplayName(string indicatorType)
        => $"{indicatorType} ({FastPeriod}, {SlowPeriod})";

    public override void Validate()
    {
        MarketBreadthDateAligner.ValidateUniverseGroupId(UniverseGroupId, nameof(UniverseGroupId));
        ThrowIfOutOfRange(FastPeriod, IndicatorDefaultConstants.McClellanFastMinPeriod, IndicatorDefaultConstants.McClellanFastMaxPeriod, nameof(FastPeriod));
        ThrowIfOutOfRange(SlowPeriod, IndicatorDefaultConstants.McClellanSlowMinPeriod, IndicatorDefaultConstants.McClellanSlowMaxPeriod, nameof(SlowPeriod));
        MarketBreadthDateAligner.ValidateFastBelowSlow(FastPeriod, SlowPeriod, nameof(FastPeriod));
    }
}
