using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreJarqueBeraParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.JarqueBeraDefaultPeriod;
    private double _alpha = IndicatorDefaultConstants.TestSignificanceDefault;
    private bool _useLogReturn = IndicatorDefaultConstants.JarqueBeraDefaultUseLogReturn;

    [CoreParameterRange(IndicatorDefaultConstants.JarqueBeraMinPeriod, IndicatorDefaultConstants.JarqueBeraMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of one-bar returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.TestSignificanceMin, IndicatorDefaultConstants.TestSignificanceMax)]
    [DisplayName("Significance Level")]
    [Description("Level of the reference line drawn on the p-value (a p-value below it rejects normality).")]
    [Category("Estimation")]
    public double Alpha
    {
        get => _alpha;
        set => SetProperty(ref _alpha, value);
    }

    [DisplayName("Use Log Return")]
    [Description("On: log returns ln(P / P[-1]). Off: simple returns (P - P[-1]) / P[-1].")]
    [Category("Estimation")]
    public bool UseLogReturn
    {
        get => _useLogReturn;
        set => SetProperty(ref _useLogReturn, value);
    }

    public override string GetDisplayName(string type) => UseLogReturn ? $"{type} ({Period})" : $"{type} ({Period}, simple)";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.JarqueBeraMinPeriod, IndicatorDefaultConstants.JarqueBeraMaxPeriod, nameof(Period));
        if (!(Alpha >= IndicatorDefaultConstants.TestSignificanceMin && Alpha <= IndicatorDefaultConstants.TestSignificanceMax))
        {
            throw new ArgumentOutOfRangeException(nameof(Alpha));
        }
    }
}
