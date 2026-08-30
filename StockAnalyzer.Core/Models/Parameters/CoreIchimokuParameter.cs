using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreIchimokuParameter : CoreIndicatorParameterBase
{
    private int _tenkanSample = 9;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Tenkan-sen")]
    [Description("Conversion Line period.")]
    [Category("Periods")]
    public int TenkanSample
    {
        get => _tenkanSample;
        set => SetProperty(ref _tenkanSample, value);
    }

    private int _kijunSample = 26;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Kijun-sen")]
    [Description("Base Line period.")]
    [Category("Periods")]
    public int KijunSample
    {
        get => _kijunSample;
        set => SetProperty(ref _kijunSample, value);
    }
    
    private int _senkouBSample = 52;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Senkou Span B")]
    [Description("Leading Span B period.")]
    [Category("Periods")]
    public int SenkouBSample
    {
        get => _senkouBSample;
        set => SetProperty(ref _senkouBSample, value);
    }
    
    private int _offset = 26;

    [CoreParameterRange(-100, 100)]
    [Range(-100, 100)]
    [DisplayName("Displacement")]
    [Description("Chikou Span displacement.")]
    [Category("Shifting")]
    public int Offset
    {
        get => _offset;
        set => SetProperty(ref _offset, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({TenkanSample}, {KijunSample})";

    public override void Validate()
    {
         if (TenkanSample <= 0) throw new ArgumentOutOfRangeException(nameof(TenkanSample));
         if (KijunSample <= 0) throw new ArgumentOutOfRangeException(nameof(KijunSample));
    }
}
