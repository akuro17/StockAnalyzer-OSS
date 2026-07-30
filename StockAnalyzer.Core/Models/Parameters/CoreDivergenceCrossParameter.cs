namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDivergenceCrossParameter : CoreIndicatorParameterBase
{
    [System.ComponentModel.DisplayName("Oscillator Source")]
    [System.ComponentModel.Description("Indicator to use for divergence comparison (e.g., RSI, MACD, Stoch)")]
    public IndicatorType SourceIndicator
    {
        get => _sourceIndicator;
        set => SetProperty(ref _sourceIndicator, value);
    }
    private IndicatorType _sourceIndicator = IndicatorType.RSI;

    [System.ComponentModel.DisplayName("Pivot Lookback (Bars)")]
    [CoreParameterRange(2, 50)]
    [System.ComponentModel.Description("Number of bars required to confirm a swing high/low")]
    public int PivotLookback
    {
        get => _pivotLookback;
        set => SetProperty(ref _pivotLookback, value);
    }
    private int _pivotLookback = IndicatorDefaultConstants.DivergencePivotLookback;

    [System.ComponentModel.DisplayName("GC/DC Short MA Period")]
    [CoreParameterRange(1, 200)]
    public int ShortMaPeriod
    {
        get => _shortMaPeriod;
        set => SetProperty(ref _shortMaPeriod, value);
    }
    private int _shortMaPeriod = IndicatorDefaultConstants.CrossShortPeriod;

    [System.ComponentModel.DisplayName("GC/DC Long MA Period")]
    [CoreParameterRange(2, 500)]
    public int LongMaPeriod
    {
        get => _longMaPeriod;
        set => SetProperty(ref _longMaPeriod, value);
    }
    private int _longMaPeriod = IndicatorDefaultConstants.CrossLongPeriod;

    public override string GetDisplayName(string indicatorName)
    {
        return $"{indicatorName} ({SourceIndicator}, Extrema:{PivotLookback}, MA:{ShortMaPeriod}/{LongMaPeriod})";
    }

    public override void Validate()
    {
        if (PivotLookback < 2)
            throw new System.ArgumentException("Pivot Lookback must be at least 2 bars");
        if (ShortMaPeriod < 1)
            throw new System.ArgumentException("Short MA Period must be at least 1");
        if (LongMaPeriod < 2)
            throw new System.ArgumentException("Long MA Period must be at least 2");
        if (ShortMaPeriod >= LongMaPeriod)
            throw new System.ArgumentException("Short MA Period must be strictly less than Long MA Period");
    }
}
