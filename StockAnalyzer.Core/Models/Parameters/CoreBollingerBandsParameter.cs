using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreBollingerBandsParameter : CoreIndicatorParameterBase
{
    private int _period = 20;
    private decimal _stdDevMultiplier = 2.0m;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for the moving average.")]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    [CoreParameterRange(0.1, 10.0)]
    [Range(0.1, 10.0)]
    [DisplayName("Std Dev Multiplier")]
    [Description("Standard Deviation multiplier for the bands.")]
    public decimal StdDevMultiplier 
    { 
        get => _stdDevMultiplier; 
        set => SetProperty(ref _stdDevMultiplier, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {StdDevMultiplier})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (StdDevMultiplier <= 0) throw new ArgumentOutOfRangeException(nameof(StdDevMultiplier));
    }
}
