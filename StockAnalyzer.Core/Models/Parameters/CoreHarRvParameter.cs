using System;
using System.ComponentModel;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreHarRvParameter : CoreAnnualizableVolatilityParameterBase
{
    private int _estimationWindow = IndicatorDefaultConstants.HarRvDefaultEstimationWindow;

    [CoreParameterRange(IndicatorDefaultConstants.HarRvMinEstimationWindow, IndicatorDefaultConstants.HarRvMaxEstimationWindow)]
    [DisplayName("Estimation Window")]
    [Description("Rolling OLS regression lookback window (bars).")]
    [Category("Estimation")]
    public int EstimationWindow
    {
        get => _estimationWindow;
        set => SetProperty(ref _estimationWindow, value);
    }

    private int _shortPeriod = IndicatorDefaultConstants.HarRvDefaultShortPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.HarRvMinShortPeriod, IndicatorDefaultConstants.HarRvMaxShortPeriod)]
    [DisplayName("Short Period")]
    [Description("Short realized volatility horizon in bars (daily default = 1).")]
    [Category("Periods")]
    public int ShortPeriod
    {
        get => _shortPeriod;
        set => SetProperty(ref _shortPeriod, value);
    }

    private int _mediumPeriod = IndicatorDefaultConstants.HarRvDefaultMediumPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.HarRvMinMediumPeriod, IndicatorDefaultConstants.HarRvMaxMediumPeriod)]
    [DisplayName("Medium Period")]
    [Description("Medium realized volatility horizon in bars (weekly default = 5). Must exceed Short Period.")]
    [Category("Periods")]
    public int MediumPeriod
    {
        get => _mediumPeriod;
        set => SetProperty(ref _mediumPeriod, value);
    }

    private int _longPeriod = IndicatorDefaultConstants.HarRvDefaultLongPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.HarRvMinLongPeriod, IndicatorDefaultConstants.HarRvMaxLongPeriod)]
    [DisplayName("Long Period")]
    [Description("Long realized volatility horizon in bars (monthly default = 22). Must exceed Medium Period.")]
    [Category("Periods")]
    public int LongPeriod
    {
        get => _longPeriod;
        set => SetProperty(ref _longPeriod, value);
    }

    private HarRvBaseEstimator _baseEstimator = HarRvBaseEstimator.RogersSatchell;

    [DisplayName("Base Estimator")]
    [Description("Daily variance proxy estimator: Rogers-Satchell, Yang-Zhang, or Close-to-Close squared log returns.")]
    [Category("Model")]
    public HarRvBaseEstimator BaseEstimator
    {
        get => _baseEstimator;
        set => SetProperty(ref _baseEstimator, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({ShortPeriod}/{MediumPeriod}/{LongPeriod})";

    public override int GetRequiredWarmupBars()
    {
        int estimatorWarmup = BaseEstimator switch
        {
            HarRvBaseEstimator.CloseToClose => 1,
            HarRvBaseEstimator.YangZhang => IndicatorDefaultConstants.HarRvYangZhangInnerPeriod,
            _ => 0
        };
        return estimatorWarmup + LongPeriod + EstimationWindow - 1;
    }

    public override void Validate()
    {
        ThrowIfOutOfRange(EstimationWindow, IndicatorDefaultConstants.HarRvMinEstimationWindow, IndicatorDefaultConstants.HarRvMaxEstimationWindow, nameof(EstimationWindow));
        ThrowIfOutOfRange(ShortPeriod, IndicatorDefaultConstants.HarRvMinShortPeriod, IndicatorDefaultConstants.HarRvMaxShortPeriod, nameof(ShortPeriod));
        ThrowIfOutOfRange(MediumPeriod, IndicatorDefaultConstants.HarRvMinMediumPeriod, IndicatorDefaultConstants.HarRvMaxMediumPeriod, nameof(MediumPeriod));
        ThrowIfOutOfRange(LongPeriod, IndicatorDefaultConstants.HarRvMinLongPeriod, IndicatorDefaultConstants.HarRvMaxLongPeriod, nameof(LongPeriod));
        if (MediumPeriod <= ShortPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(MediumPeriod), "Medium Period must be strictly greater than Short Period.");
        }

        if (LongPeriod <= MediumPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(LongPeriod), "Long Period must be strictly greater than Medium Period.");
        }

        if (!Enum.IsDefined(BaseEstimator))
        {
            throw new ArgumentOutOfRangeException(nameof(BaseEstimator), "Unsupported HAR-RV base estimator.");
        }

        ValidateAnnualization();
    }
}
