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
    
    /// <summary>Standard Ichimoku displacement (bars): single source for the default used by the parameter and the indicator.</summary>
    public const int DefaultDisplacement = 26;

    /// <summary>Smallest legal displacement. A negative value would plot the Senkou spans into the past and read future bars (look-ahead bias), so it is unrepresentable: <see cref="Offset"/> clamps to this value.</summary>
    public const int MinDisplacement = 0;

    private int _offset = DefaultDisplacement;

    [CoreParameterRange(MinDisplacement, 100)]
    [Range(MinDisplacement, 100)]
    [DisplayName("Displacement")]
    [Description("Displacement in bars: the Chikou Span is plotted this many bars back and the Senkou Spans this many bars ahead (standard 26).")]
    [Category("Shifting")]
    public int Offset
    {
        get => _offset;
        // Single entry point for every source (saved settings/JSON, UI, code): a negative displacement can never be stored.
        set => SetProperty(ref _offset, Math.Max(MinDisplacement, value));
    }

    public override string GetDisplayName(string type) => $"{type} ({TenkanSample}, {KijunSample})";

    public override int GetRequiredWarmupBars() => Math.Max(TenkanSample, Math.Max(KijunSample, SenkouBSample)) - 1;

    /// <summary>The Senkou spans are stored Displacement bars past the last candle.</summary>
    public override int GetFutureProjectionBars() => Offset;

    public override void Validate()
    {
         if (TenkanSample <= 0) throw new ArgumentOutOfRangeException(nameof(TenkanSample));
         if (KijunSample <= 0) throw new ArgumentOutOfRangeException(nameof(KijunSample));
    }
}
