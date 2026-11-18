using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// The one-bar returns of a price series as a <c>double</c> array plus an O(1) test whether a window of returns contains an invalid value.
/// The one return-window definition (Hurst, Entropy, Bocpd and the statistical-function indicators), for either return basis:
/// the log return r_k = ln(P_k / P_(k-1)) or the simple return R_k = (P_k - P_(k-1)) / P_(k-1), both from the shared
/// <see cref="IndicatorCalculationHelper"/> converters (invalid = NaN when either price is null or not positive; index 0 has no return and is invalid).
/// A window of <c>Period</c> returns ending at bar i is <c>[i - Period + 1, i]</c>; the first usable bar is therefore <c>Period</c>.
/// </summary>
public sealed class ReturnSeriesWindow
{
    private readonly double[] _returns;
    private readonly InvalidValueIndex _invalid;
    private readonly IReadOnlyList<decimal?> _source;
    private decimal[]? _decimalReturns;

    private ReturnSeriesWindow(IReadOnlyList<decimal?> returns)
    {
        _source = returns;
        int count = returns.Count;
        _returns = new double[count];
        _invalid = new InvalidValueIndex(count);
        for (int i = 0; i < count; i++)
        {
            decimal? value = returns[i];
            _returns[i] = value.HasValue ? (double)value.Value : double.NaN;
            _invalid.Add(!value.HasValue);
        }
    }

    /// <summary>Builds the window source of <paramref name="prices"/> on the chosen return basis (<paramref name="useLogReturn"/>: log return, otherwise simple return).</summary>
    public static ReturnSeriesWindow FromPrices(IReadOnlyList<decimal?> prices, bool useLogReturn) =>
        new(useLogReturn ? IndicatorCalculationHelper.ConvertToLogReturns(prices) : IndicatorCalculationHelper.ConvertToSimpleReturns(prices));

    /// <summary>Number of bars (equals the number of prices).</summary>
    public int Count => _returns.Length;

    /// <summary>The returns by bar index (NaN where invalid).</summary>
    public double[] Returns => _returns;

    /// <summary>
    /// The returns by bar index as <see cref="decimal"/> (0 where invalid: read only windows for which <see cref="IsValid"/> is true), for the statistics that stay in decimal.
    /// Built on first access, once.
    /// </summary>
    public decimal[] DecimalReturns => _decimalReturns ??= _source.Select(value => value ?? 0m).ToArray();

    /// <summary>True when every return of the bars [<paramref name="first"/>, <paramref name="last"/>] is valid.</summary>
    public bool IsValid(int first, int last) => _invalid.IsClean(first, last);
}
