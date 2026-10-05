using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreStandardDeviationParameter : CoreIndicatorParameterBase
{
    private int _period = 20;
    private bool _isPopulation = false;

    [CoreParameterRange(1, 10000)]
    [Range(1, 10000)]
    [DisplayName("Period")]
    [Description("Number of periods for the standard deviation.")]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    [DisplayName("Is Population")]
    [Description("True for population standard deviation (divide by N, TA-Lib default); false for sample standard deviation (divide by N-1).")]
    public bool IsPopulation 
    { 
        get => _isPopulation; 
        set => SetProperty(ref _isPopulation, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {(IsPopulation ? "Pop" : "Sample")})";

    public override void Validate()
    {
        if (Period <= 0 || Period > 10000) 
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 1 and 10000");
    }
}
