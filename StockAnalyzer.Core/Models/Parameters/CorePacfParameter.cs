using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameter configuration for Partial Autocorrelation Function (PACF) indicator.
/// </summary>
public class CorePacfParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.PacfDefaultPeriod;
    private int _lag = IndicatorDefaultConstants.PacfDefaultLag;
    private CorrelationCalculationMode _calculationMode = CorrelationCalculationMode.PriceLevel;
    private PriceType _priceSource = PriceType.Close;

    [Category("1. Basic Settings")]
    [DisplayName("Period")]
    [Description("Calculation window size (bars) used to evaluate sample autocovariances.")]
    [CoreParameterRange(10, 500)]
    [Range(10, 500)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Lag")]
    [Description("Target lag order k for partial autocorrelation evaluation.")]
    [CoreParameterRange(1, 50)]
    [Range(1, 50)]
    public int Lag
    {
        get => _lag;
        set => SetProperty(ref _lag, value);
    }

    [Category("2. Calculation Settings")]
    [DisplayName("Calculation Mode")]
    [Description("Series representation: PriceLevel or LogReturn differencing.")]
    public CorrelationCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => SetProperty(ref _calculationMode, value);
    }

    [Category("2. Calculation Settings")]
    [DisplayName("Price Source")]
    [Description("Price field used for calculation.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    public override string GetDisplayName(string type)
    {
        string modeSuffix = CalculationMode == CorrelationCalculationMode.LogReturn ? ", Return" : string.Empty;
        return $"{type} ({Period}, Lag={Lag}{modeSuffix})";
    }

    public override void Validate()
    {
        if (Period < 10 || Period > 500)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 10 and 500.");

        if (Lag < 1 || Lag > 50)
            throw new ArgumentOutOfRangeException(nameof(Lag), "Lag must be between 1 and 50.");

        if (!Enum.IsDefined(typeof(PriceType), PriceSource))
            throw new ArgumentOutOfRangeException(nameof(PriceSource), $"Invalid PriceSource: {PriceSource}");

        if (!Enum.IsDefined(typeof(CorrelationCalculationMode), CalculationMode))
            throw new ArgumentOutOfRangeException(nameof(CalculationMode), $"Invalid CalculationMode: {CalculationMode}");
    }
}
