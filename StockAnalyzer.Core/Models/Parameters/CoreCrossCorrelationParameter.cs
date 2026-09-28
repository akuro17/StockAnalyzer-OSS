using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public enum CrossCorrelationPeakSelectionMode
{
    [Description("Max Correlation")]
    MaxCorrelation = 0,

    [Description("Max Absolute Correlation")]
    MaxAbsoluteCorrelation = 1
}

public class CoreCrossCorrelationParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.CrossCorrelationPeriod;
    private int _lag = IndicatorDefaultConstants.CrossCorrelationLag;
    private bool _enableDynamicLag = true;
    private int _minLag = IndicatorDefaultConstants.CrossCorrelationMinLag;
    private int _maxLag = IndicatorDefaultConstants.CrossCorrelationMaxLag;
    private int _lagSmoothingPeriod = IndicatorDefaultConstants.CrossCorrelationLagSmoothingPeriod;
    private double _threshold = IndicatorDefaultConstants.CrossCorrelationThreshold;
    private CrossCorrelationPeakSelectionMode _peakSelectionMode = CrossCorrelationPeakSelectionMode.MaxCorrelation;
    private string _comparisonSymbol = string.Empty;
    private PriceType _priceSource = PriceType.Close;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private CorrelationCalculationMode _calculationMode = CorrelationCalculationMode.LogReturn;

    [DisplayName("Price Source")]
    [Description("Price source to use for primary symbol calculation.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    [CoreParameterRange(2, 1000)]
    [Range(2, 1000)]
    [DisplayName("Period")]
    [Description("Evaluation window size for cross-correlation (number of bars).")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(-100, 100)]
    [Range(-100, 100)]
    [DisplayName("Lag")]
    [Description("Fixed lag bar offset. Positive: primary leads secondary; Negative: secondary leads primary.")]
    public int Lag
    {
        get => _lag;
        set => SetProperty(ref _lag, value);
    }

    [DisplayName("Enable Dynamic Lag Search")]
    [Description("Scan across lag range [MinLag, MaxLag] for optimal lead-lag peak correlation.")]
    public bool EnableDynamicLag
    {
        get => _enableDynamicLag;
        set => SetProperty(ref _enableDynamicLag, value);
    }

    [CoreParameterRange(-100, 100)]
    [Range(-100, 100)]
    [DisplayName("Min Lag")]
    [Description("Minimum lag boundary for dynamic peak scan (bars).")]
    public int MinLag
    {
        get => _minLag;
        set => SetProperty(ref _minLag, value);
    }

    [CoreParameterRange(-100, 100)]
    [Range(-100, 100)]
    [DisplayName("Max Lag")]
    [Description("Maximum lag boundary for dynamic peak scan (bars).")]
    public int MaxLag
    {
        get => _maxLag;
        set => SetProperty(ref _maxLag, value);
    }

    [CoreParameterRange(0.0, 1.0)]
    [Range(0.0, 1.0)]
    [DisplayName("Threshold")]
    [Description("Minimum correlation prominence required for peak detection.")]
    public double Threshold
    {
        get => _threshold;
        set => SetProperty(ref _threshold, value);
    }

    [CoreParameterRange(1, 20)]
    [Range(1, 20)]
    [DisplayName("Lag Smoothing Period")]
    [Description("Exponential moving average smoothing window for optimal lag time series (bars).")]
    public int LagSmoothingPeriod
    {
        get => _lagSmoothingPeriod;
        set => SetProperty(ref _lagSmoothingPeriod, value);
    }

    [DisplayName("Peak Selection Mode")]
    [Description("Criterion for choosing optimal lag: Max Correlation or Max Absolute Correlation (detects inverse correlation).")]
    public CrossCorrelationPeakSelectionMode PeakSelectionMode
    {
        get => _peakSelectionMode;
        set => SetProperty(ref _peakSelectionMode, value);
    }

    [DisplayName("Comparison Symbol")]
    [Description("Secondary ticker symbol for cross-ticker correlation (leave empty for Price vs Volume).")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol, string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant());
    }

    [DisplayName("Comparison Price Type")]
    [Description("Price source type to use for the comparison symbol.")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    [DisplayName("Calculation Mode")]
    [Description("Calculation basis: Log Return (recommended for non-stationary prices) or Price Level.")]
    public CorrelationCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => SetProperty(ref _calculationMode, value);
    }

    public override string GetDisplayName(string type)
    {
        string modeSuffix = CalculationMode == CorrelationCalculationMode.LogReturn ? ", Return" : string.Empty;
        string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol) ? ComparisonSymbol.Trim().ToUpperInvariant() : "Volume";

        if (EnableDynamicLag)
        {
            return $"{type} ({Period}, {sym}, L[{MinLag}:{MaxLag}]{modeSuffix})";
        }

        return Lag != 0
            ? $"{type} ({Period}, {sym}, Lag={Lag}{modeSuffix})"
            : $"{type} ({Period}, {sym}{modeSuffix})";
    }

    public override void Validate()
    {
        if (Period < 2 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 2 and 1000.");
        if (Lag < -100 || Lag > 100)
            throw new ArgumentOutOfRangeException(nameof(Lag), "Lag must be between -100 and 100.");
        if (MinLag < -100 || MinLag > 100)
            throw new ArgumentOutOfRangeException(nameof(MinLag), "MinLag must be between -100 and 100.");
        if (MaxLag < -100 || MaxLag > 100)
            throw new ArgumentOutOfRangeException(nameof(MaxLag), "MaxLag must be between -100 and 100.");
        if (MinLag > MaxLag)
            throw new ArgumentException("MinLag must be less than or equal to MaxLag.");
        if (Threshold < 0.0 || Threshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(Threshold), "Threshold must be between 0.0 and 1.0.");
        if (LagSmoothingPeriod < 1 || LagSmoothingPeriod > 20)
            throw new ArgumentOutOfRangeException(nameof(LagSmoothingPeriod), "LagSmoothingPeriod must be between 1 and 20.");
        if (EnableDynamicLag && Period < (MaxLag - MinLag))
            throw new ArgumentException("Period must be greater than or equal to lag range (MaxLag - MinLag) when dynamic lag is enabled.");
    }
}
