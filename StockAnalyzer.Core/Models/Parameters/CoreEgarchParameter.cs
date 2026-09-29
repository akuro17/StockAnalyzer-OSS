using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEgarchParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.EgarchDefaultPeriod;

    [DisplayName("Period")]
    [Description("Estimation window (bars). Each re-estimation fits the model on the latest Period returns. The first value appears at bar Period+1, so the earlier bars stay empty (a short history leaves few values).")]
    [Category("Periods")]
    [CoreParameterRange(IndicatorDefaultConstants.EgarchMinPeriod, IndicatorDefaultConstants.EgarchMaxPeriod)]
    [Range(IndicatorDefaultConstants.EgarchMinPeriod, IndicatorDefaultConstants.EgarchMaxPeriod)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _refitInterval = IndicatorDefaultConstants.EgarchDefaultRefitInterval;

    [DisplayName("Refit Interval")]
    [Description("Bars between re-estimations. Between refits the last estimated coefficients are kept fixed.")]
    [Category("Periods")]
    [CoreParameterRange(IndicatorDefaultConstants.EgarchMinRefitInterval, IndicatorDefaultConstants.EgarchMaxRefitInterval)]
    [Range(IndicatorDefaultConstants.EgarchMinRefitInterval, IndicatorDefaultConstants.EgarchMaxRefitInterval)]
    public int RefitInterval
    {
        get => _refitInterval;
        set => SetProperty(ref _refitInterval, value);
    }

    private int _p = IndicatorDefaultConstants.EgarchDefaultOrder;

    [DisplayName("P (Shock Lags)")]
    [Description("Number of lagged standardized shocks (magnitude and asymmetry terms). Orders above 1 need a longer Period; on short windows the estimate is less reliable.")]
    [Category("Model Orders")]
    [CoreParameterRange(IndicatorDefaultConstants.EgarchMinOrder, IndicatorDefaultConstants.EgarchMaxOrder)]
    [Range(IndicatorDefaultConstants.EgarchMinOrder, IndicatorDefaultConstants.EgarchMaxOrder)]
    public int P
    {
        get => _p;
        set => SetProperty(ref _p, value);
    }

    private int _q = IndicatorDefaultConstants.EgarchDefaultOrder;

    [DisplayName("Q (Variance Lags)")]
    [Description("Number of lagged log-variance terms (persistence). Orders above 1 need a longer Period; on short windows the estimate is less reliable.")]
    [Category("Model Orders")]
    [CoreParameterRange(IndicatorDefaultConstants.EgarchMinOrder, IndicatorDefaultConstants.EgarchMaxOrder)]
    [Range(IndicatorDefaultConstants.EgarchMinOrder, IndicatorDefaultConstants.EgarchMaxOrder)]
    public int Q
    {
        get => _q;
        set => SetProperty(ref _q, value);
    }

    private int _maxIterations = IndicatorDefaultConstants.EgarchDefaultMaxIterations;

    [DisplayName("Max Iterations")]
    [Description("Optimizer iteration limit per estimation run.")]
    [Category("Estimation")]
    [CoreParameterRange(IndicatorDefaultConstants.EgarchMinMaxIterations, IndicatorDefaultConstants.EgarchMaxMaxIterations)]
    [Range(IndicatorDefaultConstants.EgarchMinMaxIterations, IndicatorDefaultConstants.EgarchMaxMaxIterations)]
    public int MaxIterations
    {
        get => _maxIterations;
        set => SetProperty(ref _maxIterations, value);
    }

    private decimal _maxSigmaRatio = (decimal)IndicatorDefaultConstants.EgarchDefaultMaxSigmaRatio;

    [DisplayName("Max Sigma Ratio")]
    [Description("Stability limit: a bar whose estimated volatility exceeds this multiple of the estimation window's root mean square return is left empty (a run-away estimate, e.g. after a shock on a collapsed variance). Raise it to the maximum to switch the limit off.")]
    [Category("Stability")]
    [CoreParameterRange(IndicatorDefaultConstants.EgarchMinMaxSigmaRatio, IndicatorDefaultConstants.EgarchMaxMaxSigmaRatio)]
    [Range(IndicatorDefaultConstants.EgarchMinMaxSigmaRatio, IndicatorDefaultConstants.EgarchMaxMaxSigmaRatio)]
    public decimal MaxSigmaRatio
    {
        get => _maxSigmaRatio;
        set => SetProperty(ref _maxSigmaRatio, value);
    }

    private decimal _minSigmaRatio = (decimal)IndicatorDefaultConstants.EgarchDefaultMinSigmaRatio;

    [DisplayName("Min Sigma Ratio")]
    [Description("Stability limit: a bar whose estimated volatility is below this fraction of the estimation window's root mean square return is left empty (a collapsed estimate that would show as 0.00). 0 switches the limit off.")]
    [Category("Stability")]
    [CoreParameterRange(IndicatorDefaultConstants.EgarchMinMinSigmaRatio, IndicatorDefaultConstants.EgarchMaxMinSigmaRatio)]
    [Range(IndicatorDefaultConstants.EgarchMinMinSigmaRatio, IndicatorDefaultConstants.EgarchMaxMinSigmaRatio)]
    public decimal MinSigmaRatio
    {
        get => _minSigmaRatio;
        set => SetProperty(ref _minSigmaRatio, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({P},{Q}; {Period})";

    public override void Validate()
    {
        if (Period < IndicatorDefaultConstants.EgarchMinPeriod || Period > IndicatorDefaultConstants.EgarchMaxPeriod)
            throw new ArgumentOutOfRangeException(nameof(Period), $"Period must be between {IndicatorDefaultConstants.EgarchMinPeriod} and {IndicatorDefaultConstants.EgarchMaxPeriod}.");
        if (RefitInterval < IndicatorDefaultConstants.EgarchMinRefitInterval || RefitInterval > IndicatorDefaultConstants.EgarchMaxRefitInterval)
            throw new ArgumentOutOfRangeException(nameof(RefitInterval), $"RefitInterval must be between {IndicatorDefaultConstants.EgarchMinRefitInterval} and {IndicatorDefaultConstants.EgarchMaxRefitInterval}.");
        if (P < IndicatorDefaultConstants.EgarchMinOrder || P > IndicatorDefaultConstants.EgarchMaxOrder)
            throw new ArgumentOutOfRangeException(nameof(P), $"P must be between {IndicatorDefaultConstants.EgarchMinOrder} and {IndicatorDefaultConstants.EgarchMaxOrder}.");
        if (Q < IndicatorDefaultConstants.EgarchMinOrder || Q > IndicatorDefaultConstants.EgarchMaxOrder)
            throw new ArgumentOutOfRangeException(nameof(Q), $"Q must be between {IndicatorDefaultConstants.EgarchMinOrder} and {IndicatorDefaultConstants.EgarchMaxOrder}.");
        if (MaxIterations < IndicatorDefaultConstants.EgarchMinMaxIterations || MaxIterations > IndicatorDefaultConstants.EgarchMaxMaxIterations)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), $"MaxIterations must be between {IndicatorDefaultConstants.EgarchMinMaxIterations} and {IndicatorDefaultConstants.EgarchMaxMaxIterations}.");
        if (MaxSigmaRatio < (decimal)IndicatorDefaultConstants.EgarchMinMaxSigmaRatio || MaxSigmaRatio > (decimal)IndicatorDefaultConstants.EgarchMaxMaxSigmaRatio)
            throw new ArgumentOutOfRangeException(nameof(MaxSigmaRatio), $"MaxSigmaRatio must be between {IndicatorDefaultConstants.EgarchMinMaxSigmaRatio} and {IndicatorDefaultConstants.EgarchMaxMaxSigmaRatio}.");
        if (MinSigmaRatio < (decimal)IndicatorDefaultConstants.EgarchMinMinSigmaRatio || MinSigmaRatio > (decimal)IndicatorDefaultConstants.EgarchMaxMinSigmaRatio)
            throw new ArgumentOutOfRangeException(nameof(MinSigmaRatio), $"MinSigmaRatio must be between {IndicatorDefaultConstants.EgarchMinMinSigmaRatio} and {IndicatorDefaultConstants.EgarchMaxMinSigmaRatio}.");
    }
}
