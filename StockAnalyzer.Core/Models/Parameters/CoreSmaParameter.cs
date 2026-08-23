using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSmaParameter : CoreIndicatorParameterBase
{
    private int _period = 14;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for the Simple Moving Average.")]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0 || Period > 10000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 1 and 10000");
    }
}
