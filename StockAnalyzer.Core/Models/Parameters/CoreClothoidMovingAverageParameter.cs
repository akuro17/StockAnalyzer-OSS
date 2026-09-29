using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreClothoidMovingAverageParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.ClothoidMovingAverageDefaultPeriod;
    [CoreParameterRange(2, 500)]
    [Range(2, 500)]
    [DisplayName("Period")]
    [Description("Number of periods for Fresnel cosine kernel convolution window.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private double _offset = IndicatorDefaultConstants.ClothoidMovingAverageDefaultOffset;
    [CoreParameterRange(0.0, 1.0)]
    [Range(0.0, 1.0)]
    [DisplayName("Offset")]
    [Description("Kernel peak position ratio within window (0 = oldest, 1 = newest).")]
    public double Offset
    {
        get => _offset;
        set => SetProperty(ref _offset, value);
    }

    private double _sigma = IndicatorDefaultConstants.ClothoidMovingAverageDefaultSigma;
    [CoreParameterRange(0.5, 20.0)]
    [Range(0.5, 20.0)]
    [DisplayName("Sigma")]
    [Description("Bandwidth parameter for Fresnel cosine smoothing filter.")]
    public double Sigma
    {
        get => _sigma;
        set => SetProperty(ref _sigma, value);
    }

    private PriceType _priceSource = PriceType.Close;
    [DisplayName("Price Source")]
    [Description("Input price series to smooth.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Offset:F2}, {Sigma:F1})";

    public override void Validate()
    {
        if (Period < 2 || Period > 500)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 2 and 500.");
        if (!double.IsFinite(Offset) || Offset < 0.0 || Offset > 1.0)
            throw new ArgumentOutOfRangeException(nameof(Offset), "Offset must be a finite number between 0.0 and 1.0.");
        if (!double.IsFinite(Sigma) || Sigma < 0.5 || Sigma > 20.0)
            throw new ArgumentOutOfRangeException(nameof(Sigma), "Sigma must be a finite number between 0.5 and 20.0.");
        if (!Enum.IsDefined(typeof(PriceType), PriceSource))
            throw new ArgumentOutOfRangeException(nameof(PriceSource), "PriceSource must be a defined PriceType value.");
    }
}
