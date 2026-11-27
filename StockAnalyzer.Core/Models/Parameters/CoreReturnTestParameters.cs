using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreOneSampleTTestParameter : CoreReturnWindowParameterBase
{
    private int _period = IndicatorDefaultConstants.OneSampleTTestDefaultPeriod;
    private decimal _nullMean = IndicatorDefaultConstants.OneSampleTTestDefaultNullMean;

    public CoreOneSampleTTestParameter()
        : base(IndicatorDefaultConstants.TestDefaultUseLogReturn)
    {
    }

    [CoreParameterRange(IndicatorDefaultConstants.OneSampleTTestMinPeriod, IndicatorDefaultConstants.OneSampleTTestMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of returns in the window (bars). A window with an unusable price, or a window of equal returns, gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.OneSampleTTestMinNullMean, IndicatorDefaultConstants.OneSampleTTestMaxNullMean)]
    [DisplayName("Null Mean")]
    [Description("The mean return per bar the null hypothesis states (0 = no drift). The statistic is the t value of the window mean against it.")]
    [Category("Estimation")]
    public decimal NullMean
    {
        get => _nullMean;
        set => SetProperty(ref _nullMean, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.OneSampleTTestMinPeriod, IndicatorDefaultConstants.OneSampleTTestMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(NullMean, IndicatorDefaultConstants.OneSampleTTestMinNullMean, IndicatorDefaultConstants.OneSampleTTestMaxNullMean, nameof(NullMean));
    }
}

/// <summary>Welch two-sample t-test and the two-sample Kolmogorov-Smirnov test share the ranges of their windows (the settings classes are separate types).</summary>
public abstract class CoreUnrestrictedTwoSampleParameterBase : CoreTwoWindowReturnParameterBase
{
    private int _period = IndicatorDefaultConstants.TwoSampleTestDefaultPeriod;
    private int _baselinePeriod = IndicatorDefaultConstants.TwoSampleTestDefaultBaselinePeriod;

    protected CoreUnrestrictedTwoSampleParameterBase()
        : base(IndicatorDefaultConstants.TestDefaultUseLogReturn)
    {
    }

    [CoreParameterRange(IndicatorDefaultConstants.TwoSampleTTestMinPeriod, IndicatorDefaultConstants.TwoSampleTTestMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of returns in the recent window (bars). A window with an unusable price gives no value.")]
    [Category("Periods")]
    public override int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.TwoSampleTTestMinBaselinePeriod, IndicatorDefaultConstants.TwoSampleTTestMaxBaselinePeriod)]
    [DisplayName("Baseline Period")]
    [Description("Number of returns in the baseline window (bars): the returns that end right before the recent window.")]
    [Category("Periods")]
    public override int BaselinePeriod
    {
        get => _baselinePeriod;
        set => SetProperty(ref _baselinePeriod, value);
    }

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.TwoSampleTTestMinPeriod, IndicatorDefaultConstants.TwoSampleTTestMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(BaselinePeriod, IndicatorDefaultConstants.TwoSampleTTestMinBaselinePeriod, IndicatorDefaultConstants.TwoSampleTTestMaxBaselinePeriod, nameof(BaselinePeriod));
    }
}

public class CoreTwoSampleTTestParameter : CoreUnrestrictedTwoSampleParameterBase
{
}

public class CoreKsTwoSampleTestParameter : CoreUnrestrictedTwoSampleParameterBase
{
}

public class CoreMannWhitneyUTestParameter : CoreTwoWindowReturnParameterBase
{
    private int _period = IndicatorDefaultConstants.TwoSampleTestDefaultPeriod;
    private int _baselinePeriod = IndicatorDefaultConstants.TwoSampleTestDefaultBaselinePeriod;

    public CoreMannWhitneyUTestParameter()
        : base(IndicatorDefaultConstants.TestDefaultUseLogReturn)
    {
    }

    [CoreParameterRange(IndicatorDefaultConstants.MannWhitneyUMinPeriod, IndicatorDefaultConstants.MannWhitneyUMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of returns in the recent window (bars, at most 200: the kernel takes at most 512 returns in both windows together). A window with an unusable price gives no value.")]
    [Category("Periods")]
    public override int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.MannWhitneyUMinBaselinePeriod, IndicatorDefaultConstants.MannWhitneyUMaxBaselinePeriod)]
    [DisplayName("Baseline Period")]
    [Description("Number of returns in the baseline window (bars, at most 300): the returns that end right before the recent window.")]
    [Category("Periods")]
    public override int BaselinePeriod
    {
        get => _baselinePeriod;
        set => SetProperty(ref _baselinePeriod, value);
    }

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.MannWhitneyUMinPeriod, IndicatorDefaultConstants.MannWhitneyUMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(BaselinePeriod, IndicatorDefaultConstants.MannWhitneyUMinBaselinePeriod, IndicatorDefaultConstants.MannWhitneyUMaxBaselinePeriod, nameof(BaselinePeriod));
    }
}

public class CoreKlDivergenceParameter : CoreTwoWindowReturnParameterBase
{
    private int _period = IndicatorDefaultConstants.KlDivergenceDefaultPeriod;
    private int _baselinePeriod = IndicatorDefaultConstants.KlDivergenceDefaultBaselinePeriod;
    private int _binCount = IndicatorDefaultConstants.KlDivergenceDefaultBinCount;

    public CoreKlDivergenceParameter()
        : base(IndicatorDefaultConstants.KlDivergenceDefaultUseLogReturn)
    {
    }

    [CoreParameterRange(IndicatorDefaultConstants.KlDivergenceMinPeriod, IndicatorDefaultConstants.KlDivergenceMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of returns in the recent window (bars). A window with an unusable price gives no value.")]
    [Category("Periods")]
    public override int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.KlDivergenceMinBaselinePeriod, IndicatorDefaultConstants.KlDivergenceMaxBaselinePeriod)]
    [DisplayName("Baseline Period")]
    [Description("Number of returns in the baseline window (bars): the returns that end right before the recent window.")]
    [Category("Periods")]
    public override int BaselinePeriod
    {
        get => _baselinePeriod;
        set => SetProperty(ref _baselinePeriod, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.KlDivergenceMinBinCount, IndicatorDefaultConstants.KlDivergenceMaxBinCount)]
    [DisplayName("Bin Count")]
    [Description("Number of equal-width bins over the shared range of both windows. The two histograms are Laplace-smoothed (1e-6 per bin), so an empty bin never gives an infinite divergence. A range of equal returns gives no value.")]
    [Category("Estimation")]
    public int BinCount
    {
        get => _binCount;
        set => SetProperty(ref _binCount, value);
    }

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.KlDivergenceMinPeriod, IndicatorDefaultConstants.KlDivergenceMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(BaselinePeriod, IndicatorDefaultConstants.KlDivergenceMinBaselinePeriod, IndicatorDefaultConstants.KlDivergenceMaxBaselinePeriod, nameof(BaselinePeriod));
        ThrowIfOutOfRange(BinCount, IndicatorDefaultConstants.KlDivergenceMinBinCount, IndicatorDefaultConstants.KlDivergenceMaxBinCount, nameof(BinCount));
    }
}
