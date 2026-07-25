using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreStochasticParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("K Period")]
    [Description("Period for the %K line.")]
    public int KPeriod { get; set; } = 14;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("D Period")]
    [Description("Period for the %D line.")]
    public int DPeriod { get; set; } = 3;
    
    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Smooth")]
    [Description("Smoothing factor.")]
    public int Smooth { get; set; } = 3;

    public override string GetDisplayName(string type) => $"{type} ({KPeriod}, {DPeriod}, {Smooth})";

    public override void Validate()
    {
         if (KPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(KPeriod));
         if (DPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(DPeriod));
         if (Smooth <= 0) throw new ArgumentOutOfRangeException(nameof(Smooth));
    }
}
