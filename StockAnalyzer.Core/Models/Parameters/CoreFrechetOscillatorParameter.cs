using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFrechetOscillatorParameter : CoreIndicatorParameterBase
{
    private int _period = 20;

    [DisplayName("Period")]
    [Description("The window size (number of candles) used for waveform comparison.")]
    [CoreParameterRange(3, 500)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _lag = 10;

    [DisplayName("Lag")]
    [Description("How many candles back to look for the comparison waveform.")]
    [CoreParameterRange(1, 500)]
    public int Lag
    {
        get => _lag;
        set => SetProperty(ref _lag, value);
    }

    private PriceType _priceSource = PriceType.Close;

    [DisplayName("Price Type")]
    [Description("The price type used for Fréchet distance calculation.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    public override string GetDisplayName(string indicatorType)
    {
        return $"Frechet_Osc({Period},{Lag})";
    }

    public override void Validate()
    {
        if (Period < 3) throw new ArgumentOutOfRangeException(nameof(Period), "Period must be >= 3");
        if (Lag < 1) throw new ArgumentOutOfRangeException(nameof(Lag), "Lag must be >= 1");
    }

    public override bool Equals(object? obj)
    {
        if (obj is not CoreFrechetOscillatorParameter p) return false;
        return p.Period == Period && p.Lag == Lag && p.PriceSource == PriceSource;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Period, Lag, PriceSource);
    }
}
