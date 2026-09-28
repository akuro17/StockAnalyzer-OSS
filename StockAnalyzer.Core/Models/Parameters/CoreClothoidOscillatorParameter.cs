using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreClothoidOscillatorParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.ClothoidOscillatorDefaultPeriod;
    [CoreParameterRange(4, 200)]
    [Range(4, 200)]
    [DisplayName("Period")]
    [Description("Number of bars for local cubic WLS kinematics estimation window.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private double _decayAlpha = IndicatorDefaultConstants.ClothoidOscillatorDefaultDecayAlpha;
    [CoreParameterRange(0.0, 5.0)]
    [Range(0.0, 5.0)]
    [DisplayName("Decay Alpha")]
    [Description("Exponential time decay rate prioritizing recent price action.")]
    public double DecayAlpha
    {
        get => _decayAlpha;
        set => SetProperty(ref _decayAlpha, value);
    }

    private int _signalPeriod = IndicatorDefaultConstants.ClothoidOscillatorDefaultSignalPeriod;
    [CoreParameterRange(1, 50)]
    [Range(1, 50)]
    [DisplayName("Signal Period")]
    [Description("Period for EMA smoothing signal line.")]
    public int SignalPeriod
    {
        get => _signalPeriod;
        set => SetProperty(ref _signalPeriod, value);
    }

    private PriceType _priceSource = PriceType.Close;
    [DisplayName("Price Source")]
    [Description("Input price series to calculate kinematics from.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {DecayAlpha:F1}, {SignalPeriod})";

    public override void Validate()
    {
        if (Period < 4 || Period > 200)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 4 and 200.");
        if (!double.IsFinite(DecayAlpha) || DecayAlpha < 0.0 || DecayAlpha > 5.0)
            throw new ArgumentOutOfRangeException(nameof(DecayAlpha), "DecayAlpha must be a finite number between 0.0 and 5.0.");
        if (SignalPeriod < 1 || SignalPeriod > 50)
            throw new ArgumentOutOfRangeException(nameof(SignalPeriod), "SignalPeriod must be between 1 and 50.");
        if (!Enum.IsDefined(typeof(PriceType), PriceSource))
            throw new ArgumentOutOfRangeException(nameof(PriceSource), "PriceSource must be a defined PriceType value.");
    }
}
