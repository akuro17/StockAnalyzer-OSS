using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>The return basis shared by the hypothesis-test indicators: simple returns <c>(P - P_prev) / P_prev</c> or, with <see cref="UseLogReturn"/>, log returns <c>ln(P / P_prev)</c>.</summary>
public abstract class CoreReturnWindowParameterBase : CoreIndicatorParameterBase
{
    private bool _useLogReturn;

    protected CoreReturnWindowParameterBase(bool defaultUseLogReturn)
    {
        _useLogReturn = defaultUseLogReturn;
    }

    [DisplayName("Use Log Return")]
    [Description("Off: simple returns (P - P_prev) / P_prev. On: log returns ln(P / P_prev).")]
    [Category("Estimation")]
    public bool UseLogReturn
    {
        get => _useLogReturn;
        set => SetProperty(ref _useLogReturn, value);
    }
}

/// <summary>
/// The two windows of a two-sample test of returns: the recent window of <see cref="Period"/> returns ending at the bar and the baseline window of <see cref="BaselinePeriod"/> returns that end right
/// before it. The ranges differ by test (the Mann-Whitney kernel limits the sum), so each derived class declares the two properties with its own range attributes.
/// </summary>
public abstract class CoreTwoWindowReturnParameterBase : CoreReturnWindowParameterBase
{
    protected CoreTwoWindowReturnParameterBase(bool defaultUseLogReturn)
        : base(defaultUseLogReturn)
    {
    }

    public abstract int Period { get; set; }

    public abstract int BaselinePeriod { get; set; }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {BaselinePeriod})";

    // The first value is at bar index Period + BaselinePeriod (both windows are returns, so they need Period + BaselinePeriod + 1 prices).
    public override int GetRequiredWarmupBars() => Period + BaselinePeriod;
}
