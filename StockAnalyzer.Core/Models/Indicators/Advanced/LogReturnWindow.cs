namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// The log-return input shared by the window statistics of the complexity indicators (Hurst exponent, Shannon entropy):
/// r_k = ln(P_k / P_(k-1)), invalid (NaN) when either price is null or not positive (the shared <see cref="IndicatorCalculationHelper.ConvertToLogReturns"/> rule),
/// plus an O(1) test whether a window of returns contains an invalid value.
/// </summary>
public sealed class LogReturnWindow
{
    private readonly double[] _returns;
    private readonly int[] _invalidBefore; // _invalidBefore[i] = number of invalid returns among indices 0 .. i-1

    public LogReturnWindow(IReadOnlyList<decimal?> prices)
    {
        IReadOnlyList<decimal?> logReturns = IndicatorCalculationHelper.ConvertToLogReturns(prices);
        int count = logReturns.Count;
        _returns = new double[count];
        _invalidBefore = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            decimal? value = logReturns[i];
            _returns[i] = value.HasValue ? (double)value.Value : double.NaN;
            _invalidBefore[i + 1] = _invalidBefore[i] + (value.HasValue ? 0 : 1);
        }
    }

    /// <summary>Number of bars (equals the number of prices); index 0 has no return and is invalid.</summary>
    public int Count => _returns.Length;

    /// <summary>The returns by bar index (NaN where invalid).</summary>
    public double[] Returns => _returns;

    /// <summary>True when every return of the bars [<paramref name="first"/>, <paramref name="last"/>] is valid.</summary>
    public bool IsValid(int first, int last) => _invalidBefore[last + 1] - _invalidBefore[first] == 0;
}
