using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameter configuration for Cross-Ticker Dynamic Time Warping (DTW) indicator.
/// </summary>
public class CoreCrossTickerDtwParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.CrossTickerDtwDefaultPeriod;
    private string _comparisonSymbol = string.Empty;
    private PriceType _priceSource = PriceType.Close;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private int _warpingRadius = IndicatorDefaultConstants.CrossTickerDtwDefaultWarpingRadius;
    private DtwOutputMode _outputMode = DtwOutputMode.Distance;

    [Category("1. Basic Settings")]
    [DisplayName("Period")]
    [Description("Window length (bars) used for DTW shape pattern comparison.")]
    [CoreParameterRange(5, 200)]
    [Range(5, 200)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Comparison Symbol")]
    [Description("Benchmark or target symbol to compare against.")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol, value ?? string.Empty);
    }

    [Category("2. Source Settings")]
    [DisplayName("Price Source")]
    [Description("Price field of the primary chart symbol.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    [Category("2. Source Settings")]
    [DisplayName("Comparison Price Source")]
    [Description("Price field of the comparison benchmark symbol.")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    [Category("3. DTW Constraints")]
    [DisplayName("Warping Radius")]
    [Description("Sakoe-Chiba band radius in bars (-1 = unconstrained / full warping matrix).")]
    [CoreParameterRange(-1, 50)]
    [Range(-1, 50)]
    public int WarpingRadius
    {
        get => _warpingRadius;
        set => SetProperty(ref _warpingRadius, value);
    }

    [Category("3. DTW Constraints")]
    [DisplayName("Output Mode")]
    [Description("Primary series output mode: Normalized DTW distance or Similarity score [0, 1].")]
    public DtwOutputMode OutputMode
    {
        get => _outputMode;
        set => SetProperty(ref _outputMode, value);
    }

    // SSoT Mandate: A two-ticker indicator without a fallback MUST implement this member EXPLICITLY,
    // never as a public property, preventing reflection-based parameter exposure.
    bool ICrossTickerParameter.ComparisonSymbolOptional => false;

    public override string GetDisplayName(string type)
    {
        string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol) ? ComparisonSymbol.Trim().ToUpperInvariant() : "Benchmark";
        string modeStr = OutputMode == DtwOutputMode.Similarity ? "Sim" : "Dist";
        return $"{type} ({Period}, {sym}, {modeStr})";
    }

    public override void Validate()
    {
        if (Period < 5 || Period > 200)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 5 and 200.");

        if (WarpingRadius < -1 || WarpingRadius > 50)
            throw new ArgumentOutOfRangeException(nameof(WarpingRadius), "WarpingRadius must be between -1 and 50.");

        if (!Enum.IsDefined(typeof(PriceType), PriceSource))
            throw new ArgumentOutOfRangeException(nameof(PriceSource), $"Invalid PriceSource: {PriceSource}");

        if (!Enum.IsDefined(typeof(PriceType), ComparisonPriceSource))
            throw new ArgumentOutOfRangeException(nameof(ComparisonPriceSource), $"Invalid ComparisonPriceSource: {ComparisonPriceSource}");

        if (!Enum.IsDefined(typeof(DtwOutputMode), OutputMode))
            throw new ArgumentOutOfRangeException(nameof(OutputMode), $"Invalid OutputMode: {OutputMode}");
    }
}
