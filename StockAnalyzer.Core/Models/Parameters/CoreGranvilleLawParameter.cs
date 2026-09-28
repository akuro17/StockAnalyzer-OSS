namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameters for Granville's Laws Analysis indicator.
/// </summary>
public class CoreGranvilleLawParameter : CoreIndicatorParameterBase
{
    /// <summary>
    /// Moving average period used for Granville analysis (e.g., 200-day MA).
    /// </summary>
    [System.ComponentModel.DisplayName("MA Period")]
    [System.ComponentModel.Description("Moving average period used for Granville analysis.")]
    [System.ComponentModel.Category("General")]
    [CoreParameterRange(5, 500)]
    public int MaPeriod
    {
        get => _maPeriod;
        set => SetProperty(ref _maPeriod, value);
    }
    private int _maPeriod = IndicatorDefaultConstants.GranvilleMaPeriod;

    /// <summary>
    /// Number of bars to measure MA slope direction.
    /// </summary>
    [System.ComponentModel.DisplayName("Slope Period")]
    [System.ComponentModel.Description("Number of bars to measure MA slope direction.")]
    [System.ComponentModel.Category("General")]
    [CoreParameterRange(2, 50)]
    public int SlopePeriod
    {
        get => _slopePeriod;
        set => SetProperty(ref _slopePeriod, value);
    }
    private int _slopePeriod = IndicatorDefaultConstants.GranvilleSlopePeriod;

    /// <summary>
    /// Percentage deviation threshold for extreme divergence signals (B4/S4).
    /// </summary>
    [System.ComponentModel.DisplayName("Deviation Threshold (%)")]
    [System.ComponentModel.Description("Percentage deviation threshold for extreme divergence signals (B4/S4).")]
    [System.ComponentModel.Category("Signal")]
    [CoreParameterRange(1.0, 50.0)]
    public decimal DeviationThreshold
    {
        get => _deviationThreshold;
        set => SetProperty(ref _deviationThreshold, value);
    }
    private decimal _deviationThreshold = IndicatorDefaultConstants.GranvilleDeviationThreshold;

    /// <summary>
    /// Percentage tolerance for bounce/rejection signals (B3/S3).
    /// Price must come within this % of the MA to qualify as a bounce.
    /// </summary>
    [System.ComponentModel.DisplayName("Bounce Tolerance (%)")]
    [System.ComponentModel.Description("Percentage tolerance for bounce/rejection signals (B3/S3).")]
    [System.ComponentModel.Category("Signal")]
    [CoreParameterRange(0.1, 10.0)]
    public decimal BounceTolerance
    {
        get => _bounceTolerance;
        set => SetProperty(ref _bounceTolerance, value);
    }
    private decimal _bounceTolerance = IndicatorDefaultConstants.GranvilleBounceTolerance;

    /// <summary>
    /// Percentage threshold below which MA slope is considered "flat" or sideways.
    /// </summary>
    [System.ComponentModel.DisplayName("Flat Threshold (%)")]
    [System.ComponentModel.Description("Percentage threshold below which MA slope is considered flat.")]
    [System.ComponentModel.Category("Signal")]
    [CoreParameterRange(0.01, 1.0)]
    public decimal FlatThreshold
    {
        get => _flatThreshold;
        set => SetProperty(ref _flatThreshold, value);
    }
    private decimal _flatThreshold = IndicatorDefaultConstants.GranvilleFlatThreshold;


    /// <summary>
    /// Whether to show a colored heatmap bar at the bottom of the indicator's panel.
    /// </summary>
    [System.ComponentModel.DisplayName("Show Sub-Window Bar")]
    [System.ComponentModel.Category("Display")]
    public bool ShowSubWindowBar
    {
        get => _showSubWindowBar;
        set => SetProperty(ref _showSubWindowBar, value);
    }
    private bool _showSubWindowBar = IndicatorDefaultConstants.GranvilleShowSubWindowBar;

    public override string GetDisplayName(string indicatorName)
    {
        return $"{indicatorName} (MA{MaPeriod}, Dev{DeviationThreshold}%)";
    }

    public override void Validate()
    {
        if (MaPeriod < 5)
            throw new System.ArgumentException("MA Period must be at least 5");
        if (SlopePeriod < 2)
            throw new System.ArgumentException("Slope Period must be at least 2");
        if (DeviationThreshold <= 0)
            throw new System.ArgumentException("Deviation Threshold must be greater than 0");
        if (BounceTolerance <= 0)
            throw new System.ArgumentException("Bounce Tolerance must be greater than 0");
        if (FlatThreshold < 0)
            throw new System.ArgumentException("Flat Threshold cannot be negative");
    }
}
