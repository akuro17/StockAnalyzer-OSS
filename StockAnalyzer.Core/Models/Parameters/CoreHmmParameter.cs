using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreHmmParameter : CoreIndicatorParameterBase
{
    private int _states = 2;

    [DisplayName("States")]
    [Description("Number of hidden regime states (2 or 3).")]
    [Category("HMM Parameters")]
    [CoreParameterRange(2, 3)]
    public int States
    {
        get => _states;
        set => SetProperty(ref _states, value);
    }

    private int _period = 100;

    [DisplayName("Period")]
    [Description("Lookback rolling estimation window in bars.")]
    [Category("Periods")]
    [CoreParameterRange(10, 1000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _maxIterations = 30;

    [DisplayName("Max Iterations")]
    [Description("Maximum iterations for Baum-Welch EM convergence.")]
    [Category("Convergence")]
    [CoreParameterRange(1, 200)]
    public int MaxIterations
    {
        get => _maxIterations;
        set => SetProperty(ref _maxIterations, value);
    }

    private double _tolerance = 1e-4;

    [DisplayName("Tolerance")]
    [Description("Convergence log-likelihood threshold for EM termination.")]
    [Category("Convergence")]
    [CoreParameterRange(1e-6, 1e-2)]
    public double Tolerance
    {
        get => _tolerance;
        set => SetProperty(ref _tolerance, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period},{States})";

    public override void Validate()
    {
        if (States < 2 || States > 3)
            throw new ArgumentOutOfRangeException(nameof(States), "States must be between 2 and 3.");
        if (Period < 10 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 10 and 1000.");
        if (MaxIterations < 1 || MaxIterations > 200)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), "MaxIterations must be between 1 and 200.");
        if (Tolerance < 1e-6 || Tolerance > 1e-2)
            throw new ArgumentOutOfRangeException(nameof(Tolerance), "Tolerance must be between 1e-6 and 1e-2.");
    }
}
