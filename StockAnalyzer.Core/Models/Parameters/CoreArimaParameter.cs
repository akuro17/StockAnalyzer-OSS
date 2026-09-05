using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreArimaParameter : CoreIndicatorParameterBase
{
    private int _p = IndicatorDefaultConstants.ArimaDefaultP;

    [DisplayName("AR Order (p)")]
    [Description("Autoregressive order p (number of lag observations).")]
    [CoreParameterRange(0, 5)]
    public int P
    {
        get => _p;
        set => SetProperty(ref _p, Math.Clamp(value, 0, 5));
    }

    private int _d = IndicatorDefaultConstants.ArimaDefaultD;

    [DisplayName("Differencing (d)")]
    [Description("Degree of differencing d (0, 1, or 2).")]
    [CoreParameterRange(0, 2)]
    public int D
    {
        get => _d;
        set => SetProperty(ref _d, Math.Clamp(value, 0, 2));
    }

    private int _q = IndicatorDefaultConstants.ArimaDefaultQ;

    [DisplayName("MA Order (q)")]
    [Description("Moving average order q (size of moving average window of shocks).")]
    [CoreParameterRange(0, 5)]
    public int Q
    {
        get => _q;
        set => SetProperty(ref _q, Math.Clamp(value, 0, 5));
    }

    private int _period = IndicatorDefaultConstants.ArimaDefaultPeriod;

    [DisplayName("Period")]
    [Description("Rolling estimation lookback window size in bars.")]
    [CoreParameterRange(10, 500)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, Math.Clamp(value, 10, 500));
    }

    private PriceType _priceSource = PriceType.Close;

    [DisplayName("Price Type")]
    [Description("The price type used for ARIMA modeling.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    public override string GetDisplayName(string indicatorType)
    {
        return $"ARIMA({Period},{P},{D},{Q})";
    }

    public override void Validate()
    {
        if (P < 0 || P > 5) throw new ArgumentOutOfRangeException(nameof(P), "P must be between 0 and 5.");
        if (D < 0 || D > 2) throw new ArgumentOutOfRangeException(nameof(D), "D must be between 0 and 2.");
        if (Q < 0 || Q > 5) throw new ArgumentOutOfRangeException(nameof(Q), "Q must be between 0 and 5.");
        if (Period < 10 || Period > 500) throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 10 and 500.");
        if (Period < P + D + Q + 2) throw new ArgumentException($"Period ({Period}) must be >= P + D + Q + 2 ({P + D + Q + 2}).", nameof(Period));
    }

    public override bool Equals(object? obj)
    {
        if (obj is not CoreArimaParameter p) return false;
        return p.P == P && p.D == D && p.Q == Q && p.Period == Period && p.PriceSource == PriceSource;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(P, D, Q, Period, PriceSource);
    }
}
