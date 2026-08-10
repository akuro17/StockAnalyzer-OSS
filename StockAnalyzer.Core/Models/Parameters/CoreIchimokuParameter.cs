using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreIchimokuParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Tenkan-sen")]
    [Description("Conversion Line period.")]
    public int TenkanSample { get; set; } = 9;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Kijun-sen")]
    [Description("Base Line period.")]
    public int KijunSample { get; set; } = 26;
    
    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Senkou Span B")]
    [Description("Leading Span B period.")]
    public int SenkouBSample { get; set; } = 52;
    
    [CoreParameterRange(-100, 100)]
    [Range(-100, 100)]
    [DisplayName("Displacement")]
    [Description("Chikou Span displacement.")]
    public int Offset { get; set; } = 26;

    public override string GetDisplayName(string type) => $"{type} ({TenkanSample}, {KijunSample})";

    public override void Validate()
    {
         if (TenkanSample <= 0) throw new ArgumentOutOfRangeException(nameof(TenkanSample));
         if (KijunSample <= 0) throw new ArgumentOutOfRangeException(nameof(KijunSample));
    }
}
