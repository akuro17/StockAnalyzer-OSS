using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAtrParameter : CoreIndicatorParameterBase
{
    private int _period = 14;
    [CoreParameterRange(1, 1000)]
    [Range(1, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for the Average True Range.")]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
