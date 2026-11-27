using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>The trimmed and the winsorized moving average share one setting: the fraction <see cref="Alpha"/> of the window cut (or replaced) at each end of the sorted window.</summary>
public abstract class CoreTrimFractionAverageParameterBase : CoreWindowAverageParameterBase
{
    private decimal _alpha = IndicatorDefaultConstants.TrimFractionDefaultAlpha;

    [CoreParameterRange(IndicatorDefaultConstants.TrimFractionMinAlpha, IndicatorDefaultConstants.TrimFractionMaxAlpha)]
    [DisplayName("Alpha")]
    [Description("Fraction of the window cut at each end of the sorted prices: floor(Alpha x Period) values per end (0 = the plain mean, at most 0.49).")]
    [Category("Estimation")]
    public decimal Alpha
    {
        get => _alpha;
        set => SetProperty(ref _alpha, value);
    }

    protected override string LabelSuffix() => $", {Alpha}";

    public override void Validate()
    {
        base.Validate();
        ThrowIfOutOfRange(Alpha, IndicatorDefaultConstants.TrimFractionMinAlpha, IndicatorDefaultConstants.TrimFractionMaxAlpha, nameof(Alpha));
    }
}
