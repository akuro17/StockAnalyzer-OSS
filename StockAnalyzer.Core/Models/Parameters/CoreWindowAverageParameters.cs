using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreTrimmedMaParameter : CoreTrimFractionAverageParameterBase
{
}

public class CoreWinsorizedMaParameter : CoreTrimFractionAverageParameterBase
{
}

public class CoreGeometricMaParameter : CoreWindowAverageParameterBase
{
}

public class CoreHarmonicMaParameter : CoreWindowAverageParameterBase
{
}

public class CoreHuberMaParameter : CoreWindowAverageParameterBase
{
    private decimal _tuningK = IndicatorDefaultConstants.HuberMaDefaultTuningK;

    [CoreParameterRange(IndicatorDefaultConstants.HuberMaMinTuningK, IndicatorDefaultConstants.HuberMaMaxTuningK)]
    [DisplayName("Tuning K")]
    [Description("Huber threshold in robust standard deviations (MAD x 1.4826): residuals beyond it are down-weighted. 1.345 keeps 95 % efficiency at the normal distribution; a large value approaches the plain mean.")]
    [Category("Estimation")]
    public decimal TuningK
    {
        get => _tuningK;
        set => SetProperty(ref _tuningK, value);
    }

    protected override string LabelSuffix() => $", {TuningK}";

    public override void Validate()
    {
        base.Validate();
        ThrowIfOutOfRange(TuningK, IndicatorDefaultConstants.HuberMaMinTuningK, IndicatorDefaultConstants.HuberMaMaxTuningK, nameof(TuningK));
    }
}

public class CoreBiweightMaParameter : CoreWindowAverageParameterBase
{
    private decimal _tuningC = IndicatorDefaultConstants.BiweightMaDefaultTuningC;

    [CoreParameterRange(IndicatorDefaultConstants.BiweightMaMinTuningC, IndicatorDefaultConstants.BiweightMaMaxTuningC)]
    [DisplayName("Tuning C")]
    [Description("Tukey biweight cut-off in robust standard deviations (MAD x 1.4826): prices beyond it get zero weight. 4.685 keeps 95 % efficiency at the normal distribution.")]
    [Category("Estimation")]
    public decimal TuningC
    {
        get => _tuningC;
        set => SetProperty(ref _tuningC, value);
    }

    protected override string LabelSuffix() => $", {TuningC}";

    public override void Validate()
    {
        base.Validate();
        ThrowIfOutOfRange(TuningC, IndicatorDefaultConstants.BiweightMaMinTuningC, IndicatorDefaultConstants.BiweightMaMaxTuningC, nameof(TuningC));
    }
}
